using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ContentRepo.Editor
{
    public sealed class ContentUploadResult
    {
        public string ContentPackageName;
        public string Platform;
        public string Generation;
        public string BuildId;
        public string Environment;
        public bool Success;
        public string ErrorMessage;
    }

    public static class ContentUploadApi
    {
        public static event Action<ContentUploadResult> OnUploadComplete;

        // ── Upload ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Rewrites placeholder URLs in a temp copy of the artifact, uploads to the CDN, then
        /// updates the manifest entry for the target environment.
        /// </summary>
        public static async Task<ContentUploadResult> UploadContentPackageAsync(
            string contentPackageName, string environment,
            string buildId = null, string platform = null,
            UploadLogHandler log = null)
        {
            var artifactPath = ResolveArtifactPath(contentPackageName, ref buildId, ref platform);
            var meta = ContentBuildApi.ReadMetadata(artifactPath)
                ?? throw new InvalidOperationException(
                    $"build-metadata.json missing in '{artifactPath}'. Rebuild the package first.");

            var generation = meta.generation;
            var provider = ContentUploadProviderFactory.Resolve();

            var remotePrefix = BuildRemotePrefix(generation, buildId, platform, contentPackageName);
            var finalBaseUrl = provider.GetPublicUrl(remotePrefix);

            ContentUploadResult result = null;
            var tempDir = Path.Combine(Path.GetTempPath(), $"content-repo-upload-{Guid.NewGuid():N}");
            try
            {
                log?.Invoke($"[Upload] '{contentPackageName}'  buildId={buildId}  platform={platform}  env={environment}");

                await Task.Run(() =>
                {
                    CopyDirectory(artifactPath, tempDir);
                    ContentBuildApi.RewriteCatalogLoadPaths(tempDir, finalBaseUrl);
                });

                await provider.UploadFolderAsync(tempDir, remotePrefix, log);
                await provider.InvalidatePathAsync($"/{remotePrefix}*", log);

                var catalogUrl = provider.GetPublicUrl($"{remotePrefix}catalog_{contentPackageName}.json");
                var manifestEntry = new ContentManifestEntry
                {
                    name = contentPackageName,
                    gitSha = meta.gitSha ?? "",
                    platforms = new System.Collections.Generic.List<ContentManifestPlatformEntry>
                    {
                        new ContentManifestPlatformEntry
                        {
                            platform = platform,
                            catalogUrl = catalogUrl,
                            buildId = buildId,
                        }
                    }
                };
                await ContentManifestApi.UpsertEntryAsync(environment, generation, manifestEntry, provider, log);

                RecordUploadTimestamp(contentPackageName, platform, environment);
                log?.Invoke($"[Upload] Done. Remote prefix: {remotePrefix}");

                result = new ContentUploadResult
                {
                    ContentPackageName = contentPackageName,
                    Platform = platform,
                    Generation = generation,
                    BuildId = buildId,
                    Environment = environment,
                    Success = true,
                };
                return result;
            }
            catch (Exception ex)
            {
                result = new ContentUploadResult
                {
                    ContentPackageName = contentPackageName,
                    Platform = platform,
                    Generation = generation,
                    BuildId = buildId,
                    Environment = environment,
                    Success = false,
                    ErrorMessage = ex.Message,
                };
                log?.Invoke($"[Upload] FAILED: {ex.Message}");
                throw;
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best-effort */ }
                try { OnUploadComplete?.Invoke(result); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        public static async Task<List<ContentUploadResult>> UploadAllCheckedOutAsync(
            string environment, UploadLogHandler log = null)
        {
            var folders = await ContentGitApi.GetCheckedOutFoldersAsync();
            var results = new List<ContentUploadResult>(folders.Count);
            foreach (var f in folders)
            {
                try { results.Add(await UploadContentPackageAsync(f, environment, log: log)); }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    results.Add(new ContentUploadResult
                    {
                        ContentPackageName = f,
                        Environment = environment,
                        Success = false,
                        ErrorMessage = ex.Message,
                    });
                }
            }
            return results;
        }

        public static async Task RemoveFromManifestAsync(string contentPackageName, string environment, UploadLogHandler log = null)
        {
            var generation = ContentRepoGenerationSettings.instance.Generation;
            var provider   = ContentUploadProviderFactory.Resolve();
            await ContentManifestApi.RemoveEntryAsync(environment, generation, contentPackageName, provider, log);
            await provider.InvalidatePathAsync($"/{generation}/{environment}/manifest.json", log);
        }

        // ── Manifest read ────────────────────────────────────────────────────────

        public static async Task<ContentManifest> GetManifestAsync(string environment)
        {
            var generation = ContentRepoGenerationSettings.instance.Generation;
            var provider = ContentUploadProviderFactory.Resolve();
            try
            {
                var json = await provider.DownloadTextAsync($"{generation}/{environment}/manifest.json");
                return ContentManifest.FromJson(json);
            }
            catch { return null; }
        }

        // ── Promote ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Promotes a single content package from staging to production by updating the production
        /// manifest to reference the same buildId/catalogUrl that staging uses. No files move.
        /// </summary>
        public static async Task PromoteContentPackageAsync(
            string contentPackageName, string fromEnvironment, string toEnvironment,
            UploadLogHandler log = null)
        {
            var generation = ContentRepoGenerationSettings.instance.Generation;
            var provider = ContentUploadProviderFactory.Resolve();

            var fromKey = $"{generation}/{fromEnvironment}/manifest.json";
            var fromJson = await provider.DownloadTextAsync(fromKey);
            var fromManifest = ContentManifest.FromJson(fromJson)
                ?? throw new InvalidOperationException($"Could not read {fromEnvironment} manifest.");

            var entry = fromManifest.Find(contentPackageName)
                ?? throw new InvalidOperationException(
                    $"'{contentPackageName}' not found in {fromEnvironment} manifest.");

            log?.Invoke($"[Promote] '{contentPackageName}' {fromEnvironment} → {toEnvironment}");
            await ContentManifestApi.UpsertEntryAsync(toEnvironment, generation, entry, provider, log);
            log?.Invoke("[Promote] Done.");
        }

        public static async Task PromoteAllAsync(
            string fromEnvironment, string toEnvironment, UploadLogHandler log = null)
        {
            var folders = await ContentGitApi.GetCheckedOutFoldersAsync();
            foreach (var f in folders)
            {
                try { await PromoteContentPackageAsync(f, fromEnvironment, toEnvironment, log); }
                catch (Exception ex) { Debug.LogException(ex); log?.Invoke($"[Promote] FAILED '{f}': {ex.Message}"); }
            }
        }

        // ── Manifest ─────────────────────────────────────────────────────────────

        public static async Task PublishManifestAsync(
            string environment, UploadLogHandler log = null)
        {
            var generation = ContentRepoGenerationSettings.instance.Generation;
            var folders = await ContentGitApi.GetCheckedOutFoldersAsync();
            var provider = ContentUploadProviderFactory.Resolve();

            var entries = new List<ContentManifestEntry>();
            foreach (var f in folders)
            {
                // Build entry from the most recent upload (read from last result or scan artifacts).
                var lastResult = ContentBuildApi.GetLastBuildResult(f);
                if (lastResult == null)
                {
                    log?.Invoke($"[Manifest] Skipping '{f}' — no build result in session.");
                    continue;
                }
                var remotePrefix = BuildRemotePrefix(generation, lastResult.BuildId, lastResult.Platform, f);
                var catalogUrl = provider.GetPublicUrl($"{remotePrefix}catalog_{f}.json");
                var entry = new ContentManifestEntry
                {
                    name = f,
                    gitSha = lastResult.GitSha ?? "",
                    platforms = new System.Collections.Generic.List<ContentManifestPlatformEntry>
                    {
                        new ContentManifestPlatformEntry
                        {
                            platform = lastResult.Platform,
                            catalogUrl = catalogUrl,
                            buildId = lastResult.BuildId,
                        }
                    }
                };
                entries.Add(entry);
            }

            await ContentManifestApi.PublishAsync(environment, generation, entries, provider, log);
        }

        // ── Deletion schedule ─────────────────────────────────────────────────────

        public static async Task MarkForDeletionAsync(
            string buildId, string generation, string contentPackage, string platform,
            int retentionDays = 30, UploadLogHandler log = null)
        {
            var provider = ContentUploadProviderFactory.Resolve();
            var schedule = await GetDeleteScheduleAsync(generation, provider);

            if (schedule.Contains(buildId, generation))
            {
                log?.Invoke($"[Cleanup] buildId={buildId} is already scheduled for deletion.");
                return;
            }

            var now = DateTime.UtcNow;
            schedule.Add(new DeleteScheduleEntry
            {
                buildId = buildId,
                generation = generation,
                contentPackage = contentPackage,
                platform = platform,
                markedAt = now.ToString("O"),
                deleteAfter = now.AddDays(retentionDays).ToString("O"),
                markedBy = Environment.MachineName,
            });

            await SaveDeleteScheduleAsync(generation, schedule, provider, log);
            log?.Invoke($"[Cleanup] Marked buildId={buildId} for deletion after {retentionDays} days.");
        }

        public static async Task UnmarkForDeletionAsync(
            string buildId, string generation, UploadLogHandler log = null)
        {
            var provider = ContentUploadProviderFactory.Resolve();
            var schedule = await GetDeleteScheduleAsync(generation, provider);
            if (schedule.Remove(buildId, generation))
            {
                await SaveDeleteScheduleAsync(generation, schedule, provider, log);
                log?.Invoke($"[Cleanup] Unmarked buildId={buildId}.");
            }
        }

        public static async Task<DeleteSchedule> GetDeletionScheduleAsync(
            string generation = null, UploadLogHandler log = null)
        {
            var provider = ContentUploadProviderFactory.Resolve();
            return await GetDeleteScheduleAsync(
                generation ?? ContentRepoGenerationSettings.instance.Generation, provider);
        }

        // ── CLI entry points ──────────────────────────────────────────────────────

        public static void UploadContentPackageCLI()
        {
            try
            {
                var args = ParseCommandLine();
                if (!args.TryGetValue("contentPackage", out var pkg))
                    throw new InvalidOperationException("Missing -contentPackage <name>");
                if (!args.TryGetValue("environment", out var env))
                    env = ContentUploadSettings.instance.StagingPrefix;

                UploadContentPackageAsync(pkg, env, log: Debug.Log).GetAwaiter().GetResult();
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogError($"[Upload CLI] {ex}"); EditorApplication.Exit(1); }
        }

        public static void UploadAllCLI()
        {
            try
            {
                var args = ParseCommandLine();
                if (!args.TryGetValue("environment", out var env))
                    env = ContentUploadSettings.instance.StagingPrefix;

                UploadAllCheckedOutAsync(env, Debug.Log).GetAwaiter().GetResult();
                PublishManifestAsync(env, Debug.Log).GetAwaiter().GetResult();
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogError($"[Upload CLI] {ex}"); EditorApplication.Exit(1); }
        }

        public static void RunCleanupCLI()
        {
            try
            {
                var args = ParseCommandLine();
                var generation = args.TryGetValue("generation", out var g) ? g
                    : ContentRepoGenerationSettings.instance.Generation;

                RunCleanupAsync(generation, Debug.Log).GetAwaiter().GetResult();
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogError($"[Cleanup CLI] {ex}"); EditorApplication.Exit(1); }
        }

        // ── Cleanup (same logic as the Lambda, runnable from editor too) ──────────

        public static async Task RunCleanupAsync(string generation, UploadLogHandler log = null)
        {
            var provider = ContentUploadProviderFactory.Resolve();
            var schedule = await GetDeleteScheduleAsync(generation, provider);
            var due = schedule.DueEntries();

            log?.Invoke($"[Cleanup] {due.Count} build(s) due for deletion.");

            // Fetch both manifests to safety-check references.
            var stgKey = $"{generation}/{ContentUploadSettings.instance.StagingPrefix}/manifest.json";
            var prdKey = $"{generation}/{ContentUploadSettings.instance.ProductionPrefix}/manifest.json";
            var stgJson = await provider.DownloadTextAsync(stgKey) ?? "";
            var prdJson = await provider.DownloadTextAsync(prdKey) ?? "";

            foreach (var entry in due)
            {
                if (stgJson.Contains(entry.buildId) || prdJson.Contains(entry.buildId))
                {
                    log?.Invoke($"[Cleanup] Skipping buildId={entry.buildId} — still referenced by a manifest.");
                    continue;
                }

                var prefix = BuildRemotePrefix(entry.generation, entry.buildId, entry.platform, entry.contentPackage);
                try
                {
                    await DeleteS3PrefixAsync(prefix, provider, log);
                    schedule.Remove(entry.buildId, entry.generation);
                    log?.Invoke($"[Cleanup] Deleted {prefix}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[Cleanup] FAILED deleting {prefix}: {ex.Message}");
                }
            }

            await SaveDeleteScheduleAsync(generation, schedule, provider, log);
        }

        // ── Timestamps ────────────────────────────────────────────────────────────

        public static DateTime? GetLastUploadTimestamp(string contentPackageName, string platform, string environment)
        {
            var key = UploadTimestampKey(contentPackageName, platform, environment);
            var ticks = EditorPrefs.GetString(key, null);
            if (string.IsNullOrEmpty(ticks) || !long.TryParse(ticks, out var t)) return null;
            return new DateTime(t, DateTimeKind.Utc);
        }

        // ── Internals ─────────────────────────────────────────────────────────────

        internal static string BuildRemotePrefix(string generation, string buildId, string platform, string pkg) =>
            $"{generation}/builds/{buildId}/{platform}/{pkg}/";

        private static string ResolveArtifactPath(string pkg, ref string buildId, ref string platform)
        {
            if (!string.IsNullOrEmpty(buildId) && !string.IsNullOrEmpty(platform))
                return ContentBuildApi.GetArtifactPath(pkg, buildId, platform);

            var last = ContentBuildApi.GetLastBuildResult(pkg);
            if (last != null)
            {
                buildId = last.BuildId;
                platform = last.Platform;
                return last.ArtifactPath;
            }

            // Scan build output root for the newest metadata file for this package.
            var root = ContentBuildSettings.instance.BuildOutputRoot;
            var absRoot = Path.IsPathRooted(root) ? root : Path.Combine(ContentGitApi.ProjectRoot, root);
            var buildsRoot = Path.Combine(absRoot, "builds");
            if (!Directory.Exists(buildsRoot))
                throw new DirectoryNotFoundException(
                    $"No builds found for '{pkg}'. Build the content package first.");

            BuildMetadata newest = null;
            string newestPath = null;
            foreach (var meta in Directory.GetFiles(buildsRoot, "build-metadata.json", SearchOption.AllDirectories)
                         .Select(f => (path: Path.GetDirectoryName(f), meta: BuildMetadata.FromJson(File.ReadAllText(f))))
                         .Where(t => t.meta?.contentPackage == pkg)
                         .OrderByDescending(t => t.meta.builtAt))
            {
                newest = meta.meta;
                newestPath = meta.path;
                break;
            }

            if (newest == null)
                throw new DirectoryNotFoundException($"No build artifacts found for '{pkg}'. Build the content package first.");

            buildId = newest.buildId;
            platform = newest.platform;
            return newestPath;
        }

        private static async Task<DeleteSchedule> GetDeleteScheduleAsync(string generation, IContentUploadProvider provider)
        {
            var key = $"{generation}/delete-schedule.json";
            var json = await provider.DownloadTextAsync(key);
            return DeleteSchedule.FromJson(json);
        }

        private static async Task SaveDeleteScheduleAsync(string generation, DeleteSchedule schedule,
            IContentUploadProvider provider, UploadLogHandler log)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"delete-schedule-{Guid.NewGuid():N}.json");
            try
            {
                await System.Threading.Tasks.Task.Run(() => File.WriteAllText(tempFile, schedule.ToJson()));
                await provider.UploadFileAsync(tempFile, $"{generation}/delete-schedule.json", log);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* best-effort */ }
            }
        }

        private static async Task DeleteS3PrefixAsync(string prefix, IContentUploadProvider provider, UploadLogHandler log)
        {
            // AWS provider: use aws s3 rm --recursive. Delegated via a shell command since the interface
            // doesn't expose delete — this is intentionally NOT in the interface to avoid accidental deletion.
            if (provider is AwsUploadProvider aws)
            {
                var settings = ContentUploadSettings.instance;
                var s3Uri = $"s3://{settings.S3BucketName}/{prefix}";
                await new AwsUploadProvider().ValidateConfigAsync(log); // ensure credentials work
                // Use process directly since AwsUploadProvider doesn't expose rm.
                await System.Threading.Tasks.Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "aws",
                        Arguments = $"s3 rm \"{s3Uri}\" --recursive --region {settings.S3Region}",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    var cmd = $"aws {psi.Arguments}";
                    Debug.Log($"[ContentRepo] > {cmd}");
                    log?.Invoke($"> {cmd}");
                    var p = System.Diagnostics.Process.Start(psi)!;
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        throw new InvalidOperationException($"aws rm failed (exit {p.ExitCode})");
                });
            }
            else
            {
                throw new NotSupportedException("DeleteS3PrefixAsync is only implemented for AwsUploadProvider.");
            }
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, dest));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, dest), true);
        }

        private static void RecordUploadTimestamp(string pkg, string platform, string environment) =>
            EditorPrefs.SetString(UploadTimestampKey(pkg, platform, environment), DateTime.UtcNow.Ticks.ToString());

        private static string UploadTimestampKey(string pkg, string platform, string env) =>
            $"ContentRepo.LastUpload.{Application.dataPath.GetHashCode():X}.{env}.{platform}.{pkg}";

        private static Dictionary<string, string> ParseCommandLine()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("-", StringComparison.Ordinal)) continue;
                var key = args[i].TrimStart('-');
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    result[key] = args[++i];
                else
                    result[key] = "";
            }
            return result;
        }
    }
}
