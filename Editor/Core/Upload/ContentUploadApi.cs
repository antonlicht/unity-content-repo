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
                            sourceSha = meta.gitSha ?? "",
                            sourceTree = meta.sourceTree ?? "",
                            sourceDirty = meta.sourceDirty,
                            builtAt = meta.builtAt ?? "",
                        }
                    }
                };
                if (meta.sourceDirty)
                    log?.Invoke($"[Upload] '{contentPackageName}' was built from local changes that are not committed. Commit and push them before promoting this build.");

                // Capture the build this environment currently points at (before we overwrite it), so a
                // staging upload can prune the now-superseded bundles below.
                string previousBuildId = null;
                try { previousBuildId = (await ReadManifestAsync(environment, generation, provider))?.Find(contentPackageName)?.FindPlatform(platform)?.buildId; }
                catch (Exception ex) { log?.Invoke($"[Upload] Could not read the {environment} manifest, so the build it replaces will not be pruned: {ex.Message}"); }

                await ContentManifestApi.UpsertEntryAsync(environment, generation, manifestEntry, provider, log);

                RecordUploadTimestamp(contentPackageName, platform, environment);
                log?.Invoke($"[Upload] Done. Remote prefix: {remotePrefix}");

                await PruneSupersededStagingBuildAsync(
                    contentPackageName, environment, previousBuildId, buildId, platform, generation, provider, log);

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

        /// <summary>
        /// After a STAGING upload replaces a package's build, deletes the previous build's S3 bundles so
        /// staging holds only the actively-staged content. Guarded so it can never break production:
        /// skips a build still referenced by the production manifest (a promoted build shares the same
        /// S3 path) and one recorded in the delete-schedule (production retention/rollback window).
        /// No-op for non-staging environments or when the setting is off.
        /// </summary>
        private static async Task PruneSupersededStagingBuildAsync(
            string package, string environment, string previousBuildId, string newBuildId,
            string platform, string generation, IContentUploadProvider provider, UploadLogHandler log)
        {
            var settings = ContentUploadSettings.instance;
            if (!settings.AutoPruneSupersededStagingBuilds) return;
            if (!string.Equals(environment, settings.StagingPrefix, StringComparison.Ordinal)) return; // staging only
            if (string.IsNullOrEmpty(previousBuildId) || previousBuildId == newBuildId) return;

            // Never delete a build production points at — promotion copies the entry, so prod shares the
            // same buildId + S3 path; deleting it would pull the bundles out from under production. Never delete a
            // build being retained for rollback either; the daily cleanup Lambda handles those on their own schedule.
            var keepReason = await GetKeepReasonAsync(previousBuildId, generation, provider);
            if (keepReason != null)
            {
                log?.Invoke($"[Upload] Kept superseded build {previousBuildId} — {keepReason}.");
                return;
            }

            try
            {
                var prefix = BuildRemotePrefix(generation, previousBuildId, platform, package);
                await DeleteS3PrefixAsync(prefix, provider, log);
                log?.Invoke($"[Upload] Pruned superseded staging build {previousBuildId} ({prefix}).");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Upload] WARNING: could not prune superseded staging build {previousBuildId}: {ex.Message}");
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

        /// <summary>
        /// Removes a package from the staging manifest and prunes its staging bundles from S3 — the manual
        /// counterpart to <see cref="PruneSupersededStagingBuildAsync"/>. The manifest entry is always
        /// removed; the S3 delete is guarded exactly like the auto-prune: skipped if production still
        /// references the same build (promotion shares the buildId + S3 path, so deleting it would pull the
        /// bundles out from under production) or if the build is retained by the delete-schedule.
        /// </summary>
        public static async Task RemoveFromStagingAsync(string package, string platform, UploadLogHandler log = null)
        {
            var settings   = ContentUploadSettings.instance;
            var generation = ContentRepoGenerationSettings.instance.Generation;
            var provider   = ContentUploadProviderFactory.Resolve();

            // Capture the staging buildId before dropping the entry, so we know which bundles to prune. A failed read
            // of the staging manifest aborts here, before anything changes.
            var stagingManifest = await ReadManifestAsync(settings.StagingPrefix, generation, provider);
            var buildId = stagingManifest?.Find(package)?.FindPlatform(platform)?.buildId;

            await RemoveFromManifestAsync(package, settings.StagingPrefix, log);

            if (string.IsNullOrEmpty(buildId)) return;

            var keepReason = await GetKeepReasonAsync(buildId, generation, provider);
            if (keepReason != null)
            {
                log?.Invoke($"[Upload] Removed '{package}' from staging; kept build {buildId} bundles — {keepReason}.");
                return;
            }

            try
            {
                var prefix = BuildRemotePrefix(generation, buildId, platform, package);
                await DeleteS3PrefixAsync(prefix, provider, log);
                log?.Invoke($"[Upload] Removed '{package}' from staging and pruned build {buildId} ({prefix}).");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Upload] WARNING: removed '{package}' from staging manifest but could not prune build {buildId}: {ex.Message}");
            }
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

            // When promoting TO production over an existing build, schedule the superseded production
            // build for deletion after the retention window. The daily cleanup Lambda drains the
            // schedule and never deletes a build a manifest still references, so this is safe.
            var settings = ContentUploadSettings.instance;
            var toProduction = string.Equals(toEnvironment, settings.ProductionPrefix, StringComparison.Ordinal);

            // The delete-schedule must be readable before production changes: a promote that cannot keep it consistent
            // stops here, with production unchanged.
            if (toProduction) await GetDeleteScheduleAsync(generation, provider);

            ContentManifestEntry supersededProd = null;
            if (toProduction)
                try { supersededProd = (await ReadManifestAsync(toEnvironment, generation, provider))?.Find(contentPackageName); }
                catch (Exception ex) { log?.Invoke($"[Promote] WARNING: could not read the {toEnvironment} manifest, so the build it replaces will not be scheduled for deletion and stays on S3: {ex.Message}"); }

            await ContentManifestApi.UpsertEntryAsync(toEnvironment, generation, entry, provider, log);

            if (toProduction)
            {
                // A build that is live again (a rollback, or an identical rebuild) may still carry an old delete date;
                // drop it, so the build gets a full retention window when it is superseded again. Platforms the promoted
                // entry does not carry stay live too. If this fails the old date stays, which is harmless while the
                // build is live: cleanup skips live builds and scheduling it again moves the date forward.
                var live = entry.platforms.Select(p => p.buildId).ToList();
                if (supersededProd?.platforms != null)
                    live.AddRange(supersededProd.platforms.Where(p => entry.FindPlatform(p.platform) == null).Select(p => p.buildId));
                try { await UnscheduleAsync(live, generation, provider, log); }
                catch (Exception ex) { log?.Invoke($"[Promote] WARNING: could not clear old delete-schedule entries of the live builds: {ex.Message}"); }
            }

            if (supersededProd?.platforms != null)
                foreach (var oldPlat in supersededProd.platforms)
                {
                    var promoted = entry.FindPlatform(oldPlat.platform);
                    if (promoted == null || string.IsNullOrEmpty(oldPlat.buildId) || oldPlat.buildId == promoted.buildId) continue;
                    try
                    {
                        await MarkForDeletionAsync(oldPlat.buildId, generation, contentPackageName,
                            oldPlat.platform, settings.ProductionRetentionDays, log);
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[Promote] WARNING: the superseded build {oldPlat.buildId} was not scheduled for deletion and stays on S3: {ex.Message}");
                    }
                }

            log?.Invoke("[Promote] Done.");
        }

        private static async Task UnscheduleAsync(IEnumerable<string> liveBuildIds, string generation, IContentUploadProvider provider, UploadLogHandler log)
        {
            var ids = new HashSet<string>(liveBuildIds.Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
            if (ids.Count == 0) return;
            var schedule = await GetDeleteScheduleAsync(generation, provider);
            var removed = schedule.entries.RemoveAll(e => ids.Contains(e.buildId ?? "") && string.Equals(e.generation, generation, StringComparison.Ordinal));
            if (removed == 0) return;
            await SaveDeleteScheduleAsync(generation, schedule, provider, log);
            log?.Invoke($"[Promote] Removed {removed} old delete-schedule entr{(removed == 1 ? "y" : "ies")} of builds that are live in production.");
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
                            sourceSha = lastResult.GitSha ?? "",
                            sourceTree = lastResult.SourceTree ?? "",
                            sourceDirty = lastResult.SourceDirty,
                            builtAt = ContentBuildApi.ReadMetadata(lastResult.ArtifactPath)?.builtAt ?? "",
                        }
                    }
                };
                entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                log?.Invoke("[Manifest] Nothing to publish: no package was built in this session.");
                return;
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

            var now = DateTime.UtcNow;
            var existing = schedule.entries.Find(e =>
                string.Equals(e.buildId, buildId, StringComparison.Ordinal) && string.Equals(e.generation, generation, StringComparison.Ordinal));
            if (existing != null)
            {
                // A build that went live again (a rollback) and is now superseded once more keeps the full retention
                // window from now, not the date from its first time.
                var deleteAfter = now.AddDays(retentionDays);
                if (DateTime.TryParse(existing.deleteAfter, null, System.Globalization.DateTimeStyles.RoundtripKind, out var scheduled)
                    && scheduled.ToUniversalTime() >= deleteAfter)
                {
                    log?.Invoke($"[Cleanup] buildId={buildId} is already scheduled for deletion.");
                    return;
                }
                existing.markedAt = now.ToString("O");
                existing.deleteAfter = deleteAfter.ToString("O");
                existing.markedBy = Environment.MachineName;
                await SaveDeleteScheduleAsync(generation, schedule, provider, log);
                log?.Invoke($"[Cleanup] buildId={buildId} was already scheduled; its deletion moved to {retentionDays} days from now.");
                return;
            }

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

        public static void UploadContentPackageCLI() => ContentCli.Run("Upload CLI", async () =>
        {
            var args = ParseCommandLine();
            if (!args.TryGetValue("contentPackage", out var pkg))
                throw new InvalidOperationException("Missing -contentPackage <name>");
            if (!args.TryGetValue("environment", out var env))
                env = ContentUploadSettings.instance.StagingPrefix;

            await UploadContentPackageAsync(pkg, env, log: Debug.Log);
            return true;
        });

        public static void UploadAllCLI() => ContentCli.Run("Upload CLI", async () =>
        {
            var args = ParseCommandLine();
            if (!args.TryGetValue("environment", out var env))
                env = ContentUploadSettings.instance.StagingPrefix;

            await ContentCli.RequireCheckedOutPackagesAsync();
            var results = await UploadAllCheckedOutAsync(env, Debug.Log);
            await PublishManifestAsync(env, Debug.Log);
            return results.All(r => r.Success);
        });

        public static void RunCleanupCLI() => ContentCli.Run("Cleanup CLI", async () =>
        {
            var args = ParseCommandLine();
            var generation = args.TryGetValue("generation", out var g) ? g
                : ContentRepoGenerationSettings.instance.Generation;

            return await RunCleanupAsync(generation, Debug.Log) == 0;
        });

        // ── Cleanup (same logic as the Lambda, runnable from editor too) ──────────

        /// <summary>Deletes the builds whose retention ran out. Returns how many deletions failed (they stay scheduled).</summary>
        public static async Task<int> RunCleanupAsync(string generation, UploadLogHandler log = null)
        {
            var failed = 0;
            var provider = ContentUploadProviderFactory.Resolve();
            var schedule = await GetDeleteScheduleAsync(generation, provider);
            var due = schedule.DueEntries();

            log?.Invoke($"[Cleanup] {due.Count} build(s) due for deletion.");
            if (due.Count == 0) return 0;

            // Fetch both manifests to safety-check references. A manifest that does not exist references nothing; one
            // that cannot be read throws here, before anything is deleted or written.
            var stgKey = $"{generation}/{ContentUploadSettings.instance.StagingPrefix}/manifest.json";
            var prdKey = $"{generation}/{ContentUploadSettings.instance.ProductionPrefix}/manifest.json";
            var live = await GetLiveBuildIdsAsync(stgKey, provider);
            live.UnionWith(await GetLiveBuildIdsAsync(prdKey, provider));
            var removed = 0;

            foreach (var entry in due)
            {
                if (live.Contains(entry.buildId ?? ""))
                {
                    log?.Invoke($"[Cleanup] Skipping buildId={entry.buildId} — still referenced by a manifest.");
                    continue;
                }

                var prefix = BuildRemotePrefix(entry.generation, entry.buildId, entry.platform, entry.contentPackage);
                try
                {
                    await DeleteS3PrefixAsync(prefix, provider, log);
                    schedule.Remove(entry.buildId, entry.generation);
                    removed++;
                    log?.Invoke($"[Cleanup] Deleted {prefix}");
                }
                catch (Exception ex)
                {
                    failed++;
                    log?.Invoke($"[Cleanup] FAILED deleting {prefix}: {ex.Message}");
                }
            }

            if (removed > 0) await SaveDeleteScheduleAsync(generation, schedule, provider, log);
            return failed;
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

            // The same build the Content Browser offers for upload: this session's last build for the active build
            // target, otherwise the newest build of that target on disk.
            if (string.IsNullOrEmpty(platform)) platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            var latest = ContentBuildApi.GetLatestBuild(pkg, platform)
                ?? throw new DirectoryNotFoundException($"No {platform} build found for '{pkg}'. Build the content package first.");
            buildId = latest.buildId;
            return ContentBuildApi.GetArtifactPath(pkg, buildId, platform);
        }

        /// <summary>
        /// The delete-schedule, or an empty one when it does not exist yet. Throws when the read fails or the file
        /// cannot be parsed, so the schedule is never rewritten from a state that was not actually read.
        /// </summary>
        private static async Task<DeleteSchedule> GetDeleteScheduleAsync(string generation, IContentUploadProvider provider)
        {
            var key = $"{generation}/delete-schedule.json";
            var json = await provider.DownloadTextAsync(key);
            if (json == null) return new DeleteSchedule();
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidDataException($"The delete-schedule at '{key}' is empty, so it was not changed. Fix or remove it first.");
            DeleteSchedule schedule;
            try { schedule = JsonUtility.FromJson<DeleteSchedule>(json); }
            catch (Exception ex) { throw new InvalidDataException($"The delete-schedule at '{key}' could not be parsed, so it was not changed: {ex.Message}"); }
            if (schedule == null) throw new InvalidDataException($"The delete-schedule at '{key}' could not be parsed, so it was not changed.");
            schedule.entries ??= new List<DeleteScheduleEntry>();
            return schedule;
        }

        /// <summary>
        /// Why a build's bundles must stay on S3 — production still serves it, or the delete-schedule retains it for
        /// rollback — or null when it may be deleted. When either file cannot be read the build is kept as well:
        /// deleting on a guess could pull the bundles out from under production.
        /// </summary>
        private static async Task<string> GetKeepReasonAsync(string buildId, string generation, IContentUploadProvider provider)
        {
            var settings = ContentUploadSettings.instance;
            try
            {
                if ((await GetLiveBuildIdsAsync($"{generation}/{settings.ProductionPrefix}/manifest.json", provider)).Contains(buildId))
                    return "still referenced by production";
                if ((await GetDeleteScheduleAsync(generation, provider)).entries.Exists(e => string.Equals(e.buildId, buildId, StringComparison.Ordinal)))
                    return "retained by the delete-schedule";
                return null;
            }
            catch (Exception ex)
            {
                return $"could not check the production manifest and delete-schedule ({ex.Message})";
            }
        }

        /// <summary>
        /// The manifest of an environment, or null when it does not exist yet. Unlike <see cref="GetManifestAsync"/>, a
        /// failed read throws, for the steps that decide from it what to delete or schedule.
        /// </summary>
        private static async Task<ContentManifest> ReadManifestAsync(string environment, string generation, IContentUploadProvider provider)
        {
            var key = $"{generation}/{environment}/manifest.json";
            var json = await provider.DownloadTextAsync(key);
            if (json == null) return null;
            return ContentManifest.FromJson(json) ?? throw new InvalidDataException($"The manifest at '{key}' could not be parsed.");
        }

        /// <summary>
        /// The build ids a manifest serves, empty when the manifest does not exist. Throws when it cannot be read or
        /// parsed: a manifest nobody can read may still be what players have cached, so it must not count as empty.
        /// </summary>
        private static async Task<HashSet<string>> GetLiveBuildIdsAsync(string manifestKey, IContentUploadProvider provider)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var json = await provider.DownloadTextAsync(manifestKey);
            if (json == null) return ids;
            var manifest = ContentManifest.FromJson(json)
                ?? throw new InvalidDataException($"The manifest at '{manifestKey}' could not be parsed.");
            foreach (var package in manifest.contentPackages ?? new List<ContentManifestEntry>())
                foreach (var platform in package?.platforms ?? new List<ContentManifestPlatformEntry>())
                    if (!string.IsNullOrEmpty(platform?.buildId)) ids.Add(platform.buildId);
            return ids;
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
                        Arguments = $"s3 rm \"{s3Uri}\" --recursive --only-show-errors --region {settings.S3Region}",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    var cmd = $"aws {psi.Arguments}";
                    Debug.Log($"[ContentRepo] > {cmd}");
                    log?.Invoke($"> {cmd}");

                    // Both streams are read while aws runs: a redirected stream that nobody reads fills its pipe
                    // once there is enough output, and aws then blocks forever.
                    var errors = new System.Text.StringBuilder();
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    p.OutputDataReceived += (_, e) => { };
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        throw new InvalidOperationException($"aws rm failed (exit {p.ExitCode}): {errors.ToString().Trim()}");
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
