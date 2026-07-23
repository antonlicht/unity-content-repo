using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace ContentRepo.Editor
{
    public delegate void BuildLogHandler(string line);

    public sealed class ContentBuildResult
    {
        public string ContentPackageName;
        public string Platform;
        public string Generation;
        public string BuildId;
        public string ArtifactPath;
        public bool Success;
        public string ErrorMessage;
        public string GitSha;
    }

    // Written alongside every build so cross-session upload can locate artifacts.
    [Serializable]
    internal sealed class BuildMetadata
    {
        public string contentPackage;
        public string platform;
        public string generation;
        public string buildId;
        public string gitSha;
        public string unityVersion;
        public string builtAt;

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static BuildMetadata FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonUtility.FromJson<BuildMetadata>(json); }
            catch { return null; }
        }
    }

    public static class ContentBuildApi
    {
        // Placeholder baked into catalog at build time; replaced with real CDN URL on upload.
        public const string LoadPathPlaceholder = "https://content-repo-cdn-placeholder.example/";

        public static event Action<ContentBuildResult> OnBuildComplete;

        /// <summary>
        /// Raised at the very start of <see cref="BuildContentPackageAsync"/>, before the package's
        /// Addressables group is assembled. Runs synchronously so subscribers can make the group
        /// deterministically complete before it is built — e.g. the game's Naninovel voice-map
        /// relocation, which must not depend on the editor's periodic sync tick having fired.
        /// The argument is the content package name being built.
        /// </summary>
        public static event Action<string> BeforePackageBuild;

        // In-memory cache of most recent build per package (keyed by contentPackageName).
        private static readonly Dictionary<string, ContentBuildResult> LastResults = new(StringComparer.Ordinal);

        public static ContentBuildResult GetLastBuildResult(string contentPackageName) =>
            LastResults.TryGetValue(contentPackageName, out var r) ? r : null;

        public static async Task<ContentBuildResult> BuildContentPackageAsync(
            string contentPackageName, BuildLogHandler log = null)
        {
            ValidatePackageName(contentPackageName);

            // Let subscribers finalize the package's group first (e.g. relocate Naninovel voice-map
            // entries into it) so the build is deterministic rather than reliant on an editor tick.
            try { BeforePackageBuild?.Invoke(contentPackageName); }
            catch (Exception ex) { Debug.LogWarning($"[ContentRepo] BeforePackageBuild hook failed for '{contentPackageName}': {ex.Message}"); }

            var genSettings = ContentRepoGenerationSettings.instance;
            var buildSettings = ContentBuildSettings.instance;
            var generation = genSettings.Generation;

            log?.Invoke($"[Build] '{contentPackageName}'  generation={generation}  unity={Application.unityVersion}");

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                throw new InvalidOperationException(
                    "Addressables is not initialized. Open Window > Asset Management > Addressables > Groups.");

            // Content Repo requires JSON catalogs: the manifest references catalog_<pkg>.json and the
            // upload's URL rewriting is JSON-text-based, so a binary catalog can't be published or read
            // (the manifest points at a .json that was never produced → 403/404 at runtime). Addressables
            // 2.x defaults to binary, so fail loudly instead of silently shipping an unloadable package.
            if (!settings.EnableJsonCatalog)
                throw new InvalidOperationException(
                    "Content Repo requires JSON catalogs, but 'Enable Json Catalog' is disabled. The build would " +
                    "produce a binary catalog that the manifest and runtime can't load. Enable Addressable Asset " +
                    "Settings > Catalog > Enable Json Catalog, let the project recompile, then rebuild.");

            var profileId = settings.profileSettings.GetProfileId(buildSettings.AddressablesProfileName);
            if (string.IsNullOrEmpty(profileId))
                throw new InvalidOperationException(
                    $"Addressables profile '{buildSettings.AddressablesProfileName}' not found. " +
                    $"Configure it under Project Settings > Content Repo > Build.");

            var previousActiveProfile         = settings.activeProfileId;
            var previousPlayerVersion         = settings.OverridePlayerVersion;
            var previousBuildRemoteCatalog    = settings.BuildRemoteCatalog;
            var previousRemoteCatalogBuildId  = settings.RemoteCatalogBuildPath.Id;
            var previousRemoteCatalogLoadId   = settings.RemoteCatalogLoadPath.Id;
            var previousBuilderIndex          = settings.ActivePlayerDataBuilderIndex;

            // BuildPlayerContent requires the packed-mode builder (Default Build Script).
            // Play Mode Scripts (Use Asset Database / Use Existing Build) cannot produce bundles.
            var packedBuilderIndex = settings.DataBuilders.FindIndex(
                b => b != null && b.GetType().Name == "BuildScriptPackedMode");
            if (packedBuilderIndex < 0)
                throw new InvalidOperationException(
                    "Could not find 'Default Build Script' (BuildScriptPackedMode) in Addressables DataBuilders. " +
                    "Add it under Addressables > Settings > Build and Play Mode Scripts.");
            settings.ActivePlayerDataBuilderIndex = packedBuilderIndex;

            // CreateValue is required before SetValue — ensure both profile variables exist.
            // Use activeProfileId here because we haven't switched to profileId yet.
            settings.activeProfileId = profileId;
            EnsureProfileVariable(settings, buildSettings.RemoteLoadPathVariableName, LoadPathPlaceholder, log);
            EnsureProfileVariable(settings, buildSettings.RemoteBuildPathVariableName, "ServerData/[BuildTarget]", log);

            var previousLoadPath = settings.profileSettings.GetValueByName(profileId, buildSettings.RemoteLoadPathVariableName);

            // Disable all existing groups for the duration of this build — content packages
            // must never bleed into client builds and vice versa.
            var disabledStates = new Dictionary<AddressableAssetGroup, bool>();
            AddressableAssetGroup tempGroup = null;

            ContentBuildResult result = null;
            try
            {
                foreach (var g in settings.groups)
                {
                    if (g == null) continue;
                    var schema = g.GetSchema<BundledAssetGroupSchema>();
                    if (schema == null) continue;
                    disabledStates[g] = schema.IncludeInBuild;
                    schema.IncludeInBuild = false;
                }

                // Warn about groups that aren't managed by ContentRepo but were marked IncludeInBuild.
                // A ContentRepo-managed group lives inside the _groups/ folder of the content repo.
                // Others are excluded from content builds (handled above) but should be removed or
                // left with IncludeInBuild=false to avoid confusion.
                var repoLocalPath = ContentRepoSettings.instance.LocalPath.Replace('\\', '/').TrimEnd('/');
                var managedFolderMarker = $"/{ContentGitApi.GroupsFolderName}/";
                foreach (var kv in disabledStates)
                {
                    if (!kv.Value) continue;
                    var groupAssetPath = AssetDatabase.GetAssetPath(kv.Key).Replace('\\', '/');
                    var isManaged = groupAssetPath.Contains(managedFolderMarker)
                                 || kv.Key.Name.StartsWith(SharedGroupPrefix, StringComparison.Ordinal);
                    if (!isManaged)
                    {
                        log?.Invoke($"[Build] WARNING: Group '{kv.Key.Name}' has IncludeInBuild=true and is not managed " +
                                    "by ContentRepo. It is excluded from this content build. " +
                                    "Consider removing it or setting IncludeInBuild=false.");
                    }
                }

                tempGroup = CreateTemporaryGroup(settings, contentPackageName, profileId, buildSettings, log);

                // Wipe the Addressables build output so stale bundles from previous runs
                // don't mix in, keeping the buildId deterministic across identical builds.
                var buildOutputPath = ResolveBuildOutputPath(settings, profileId, buildSettings.RemoteBuildPathVariableName);
                if (Directory.Exists(buildOutputPath))
                {
                    Directory.Delete(buildOutputPath, true);
                    log?.Invoke($"[Build] Cleared previous build output at {buildOutputPath}");
                }

                settings.profileSettings.SetValue(profileId, buildSettings.RemoteLoadPathVariableName, LoadPathPlaceholder);
                settings.OverridePlayerVersion = contentPackageName;

                // Enable remote catalog generation so catalog_<pkg>.json and .hash are
                // written into buildOutputPath alongside the bundles and picked up by the
                // artifact copy step.
                settings.BuildRemoteCatalog = true;
                settings.RemoteCatalogBuildPath.SetVariableByName(settings, buildSettings.RemoteBuildPathVariableName);
                settings.RemoteCatalogLoadPath.SetVariableByName(settings, buildSettings.RemoteLoadPathVariableName);

                log?.Invoke("[Build] Load path set to placeholder. Building…");

                AddressableAssetSettings.BuildPlayerContent(out var buildResult);
                if (!string.IsNullOrEmpty(buildResult.Error))
                    throw new InvalidOperationException($"Addressables build failed: {buildResult.Error}");

                log?.Invoke($"[Build] Collecting artifacts from {buildOutputPath}");

                var bundleFiles = Directory.GetFiles(buildOutputPath, "*.bundle", SearchOption.AllDirectories);
                if (bundleFiles.Length == 0)
                    throw new InvalidOperationException(
                        $"No .bundle files found in '{buildOutputPath}'. Check the Addressables build output path.");

                var buildId = ComputeBuildId(bundleFiles);
                var platform = EditorUserBuildSettings.activeBuildTarget.ToString();
                var gitSha = await TryGetGitShaAsync();

                var artifactPath = GetArtifactPath(contentPackageName, buildId, platform);
                await Task.Run(() =>
                {
                    if (Directory.Exists(artifactPath)) Directory.Delete(artifactPath, true);
                    CopyDirectory(buildOutputPath, artifactPath);
                    WriteMetadata(artifactPath, contentPackageName, platform, generation, buildId, gitSha);
                });

                RecordBuildTimestamp(contentPackageName, platform);
                log?.Invoke($"[Build] Done. buildId={buildId}  artifacts={artifactPath}");

                result = new ContentBuildResult
                {
                    ContentPackageName = contentPackageName,
                    Platform = platform,
                    Generation = generation,
                    BuildId = buildId,
                    ArtifactPath = artifactPath,
                    Success = true,
                    GitSha = gitSha,
                };
                LastResults[contentPackageName] = result;
                return result;
            }
            catch (Exception ex)
            {
                result = new ContentBuildResult
                {
                    ContentPackageName = contentPackageName,
                    Platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                    Generation = generation,
                    Success = false,
                    ErrorMessage = ex.Message,
                };
                log?.Invoke($"[Build] FAILED: {ex.Message}");
                throw;
            }
            finally
            {
                // Deactivate rather than delete — preserving the group GUID keeps bundle
                // hashes deterministic across builds of the same content.
                DeactivateTemporaryGroup(settings, tempGroup, log);

                // Restore all previously-disabled groups.
                foreach (var kv in disabledStates)
                {
                    var schema = kv.Key.GetSchema<BundledAssetGroupSchema>();
                    if (schema != null) schema.IncludeInBuild = kv.Value;
                }
                settings.profileSettings.SetValue(profileId, buildSettings.RemoteLoadPathVariableName, previousLoadPath);
                settings.activeProfileId = previousActiveProfile;
                settings.OverridePlayerVersion = previousPlayerVersion;
                settings.BuildRemoteCatalog = previousBuildRemoteCatalog;
                settings.ActivePlayerDataBuilderIndex = previousBuilderIndex;
                if (previousRemoteCatalogBuildId != null)
                    settings.RemoteCatalogBuildPath.SetVariableById(settings, previousRemoteCatalogBuildId);
                if (previousRemoteCatalogLoadId != null)
                    settings.RemoteCatalogLoadPath.SetVariableById(settings, previousRemoteCatalogLoadId);

                AssetDatabase.SaveAssets();

                try { OnBuildComplete?.Invoke(result); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        public static async Task<List<ContentBuildResult>> BuildAllCheckedOutAsync(BuildLogHandler log = null)
        {
            var folders = await ContentGitApi.GetCheckedOutFoldersAsync();
            var results = new List<ContentBuildResult>(folders.Count);
            foreach (var f in folders)
            {
                try { results.Add(await BuildContentPackageAsync(f, log)); }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    results.Add(new ContentBuildResult
                    {
                        ContentPackageName = f,
                        Platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                        Generation = ContentRepoGenerationSettings.instance.Generation,
                        Success = false, ErrorMessage = ex.Message,
                    });
                }
            }
            return results;
        }

        public static void BuildContentPackageCLI()
        {
            try
            {
                var args = ParseCommandLine();
                if (!args.TryGetValue("contentPackage", out var pkg))
                    throw new InvalidOperationException("Missing -contentPackage <name>");

                BuildContentPackageAsync(pkg, Debug.Log).GetAwaiter().GetResult();
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogError($"[Build CLI] {ex}"); EditorApplication.Exit(1); }
        }

        public static void BuildAllCLI()
        {
            try
            {
                var results = BuildAllCheckedOutAsync(Debug.Log).GetAwaiter().GetResult();
                EditorApplication.Exit(results.All(r => r.Success) ? 0 : 1);
            }
            catch (Exception ex) { Debug.LogError($"[Build CLI] {ex}"); EditorApplication.Exit(1); }
        }

        // Rewrites placeholder URLs in catalog JSON files and updates companion .hash files.
        // Operates on files in targetDir (a temp copy, not the original artifact).
        public static void RewriteCatalogLoadPaths(string targetDir, string finalBaseUrl)
        {
            foreach (var jsonFile in Directory.GetFiles(targetDir, "*.json", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(jsonFile);
                if (!content.Contains(LoadPathPlaceholder)) continue;

                var rewritten = content.Replace(LoadPathPlaceholder, finalBaseUrl.TrimEnd('/') + "/");
                File.WriteAllText(jsonFile, rewritten);

                var hashFile = Path.ChangeExtension(jsonFile, ".hash");
                using var md5 = MD5.Create();
                var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(rewritten));
                File.WriteAllText(hashFile, BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant());
            }
        }

        public static string GetArtifactPath(string contentPackageName, string buildId, string platform)
        {
            var root = ContentBuildSettings.instance.BuildOutputRoot;
            var absRoot = Path.IsPathRooted(root) ? root : Path.Combine(ContentGitApi.ProjectRoot, root);
            return Path.Combine(absRoot, "builds", buildId, platform, contentPackageName);
        }

        public static string GetLatestBuildIdFromDisk(string contentPackageName, string platform)
        {
            var root    = ContentBuildSettings.instance.BuildOutputRoot;
            var absRoot = Path.IsPathRooted(root) ? root : Path.Combine(ContentGitApi.ProjectRoot, root);
            var buildsRoot = Path.Combine(absRoot, "builds");
            if (!Directory.Exists(buildsRoot)) return null;

            return Directory.GetDirectories(buildsRoot)
                .Select(d => ReadMetadata(Path.Combine(d, platform, contentPackageName)))
                .Where(m => m != null)
                .OrderByDescending(m => m.builtAt)
                .Select(m => m.buildId)
                .FirstOrDefault();
        }

        internal static BuildMetadata ReadMetadata(string artifactPath)
        {
            var path = Path.Combine(artifactPath, "build-metadata.json");
            if (!File.Exists(path)) return null;
            return BuildMetadata.FromJson(File.ReadAllText(path));
        }

        public static DateTime? GetLastBuildTimestamp(string contentPackageName, string platform)
        {
            var key = BuildTimestampKey(contentPackageName, platform);
            var ticks = EditorPrefs.GetString(key, null);
            if (string.IsNullOrEmpty(ticks) || !long.TryParse(ticks, out var t)) return null;
            return new DateTime(t, DateTimeKind.Utc);
        }

        private static void RecordBuildTimestamp(string contentPackageName, string platform) =>
            EditorPrefs.SetString(BuildTimestampKey(contentPackageName, platform), DateTime.UtcNow.Ticks.ToString());

        private static string BuildTimestampKey(string contentPackageName, string platform) =>
            $"ContentRepo.LastBuild.{Application.dataPath.GetHashCode():X}.{platform}.{contentPackageName}";

        private static void EnsureProfileVariable(AddressableAssetSettings settings,
            string variableName, string defaultValue, BuildLogHandler log)
        {
            // GetValueByName returns null or empty when the variable does not exist.
            var existing = settings.profileSettings.GetValueByName(settings.activeProfileId, variableName);
            if (!string.IsNullOrEmpty(existing)) return;

            // CreateValue adds the variable to every profile with the supplied default.
            settings.profileSettings.CreateValue(variableName, defaultValue);
            log?.Invoke($"[Build] Created missing profile variable '{variableName}' (default: '{defaultValue}')");
        }

        // ── Package group management ──────────────────────────────────────────

        // Prefix reserved for future shared-asset groups (engine fonts, shaders, etc.)
        // that stay enabled during content builds so their assets aren't re-bundled.
        public const string SharedGroupPrefix = "__ContentRepo_Shared_";

        private static AddressableAssetGroup CreateTemporaryGroup(
            AddressableAssetSettings settings,
            string contentPackageName,
            string profileId,
            ContentBuildSettings buildSettings,
            BuildLogHandler log)
        {
            // Resolve the Unity-relative asset path for the content package folder.
            var repoLocalPath = ContentRepoSettings.instance.LocalPath.Replace('\\', '/').TrimEnd('/');
            var contentAssetPath = $"{repoLocalPath}/{contentPackageName}";

            if (!contentAssetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Content repo local path '{repoLocalPath}' must be inside the Assets/ folder for Addressables to reference its assets.");

            if (!AssetDatabase.IsValidFolder(contentAssetPath))
                throw new InvalidOperationException(
                    $"Content package folder '{contentAssetPath}' not found in the Asset Database. " +
                    $"Make sure '{contentPackageName}' is checked out.");

            var guids = AssetDatabase.FindAssets("", new[] { contentAssetPath })
                .Where(g => !AssetDatabase.IsValidFolder(AssetDatabase.GUIDToAssetPath(g)))
                .ToArray();

            if (guids.Length == 0)
                throw new InvalidOperationException(
                    $"No assets found under '{contentAssetPath}'. Check the folder contains importable assets.");

            // Group is simply named after the content package — no prefix needed since the
            // group file lives in _groups/ which already provides the namespace.
            var groupsFolder = $"{repoLocalPath}/{ContentGitApi.GroupsFolderName}";

            // Reuse an existing group to keep its GUID stable — bundle hashes are derived from
            // the GUID, so recreating gives a different hash for identical content.
            var group = settings.FindGroup(contentPackageName);
            if (group == null)
            {
                group = settings.CreateGroup(
                    contentPackageName,
                    setAsDefaultGroup: false,
                    readOnly: false,
                    postEvent: false,
                    schemasToCopy: null,
                    typeof(BundledAssetGroupSchema));
                log?.Invoke($"[Build] Created group '{contentPackageName}'");
            }
            else
            {
                log?.Invoke($"[Build] Reusing group '{contentPackageName}' (stable GUID)");
            }

            // Always (re-)configure build/load paths. The group may have been created by
            // EnsureGroupPopulated (fast mode setup) which leaves the default Library path in
            // the schema — reusing it without this would write bundles there instead of to the
            // expected build output path, causing the "No .bundle files found" error.
            EnsureProfileVariable(settings, buildSettings.RemoteBuildPathVariableName, "ServerData/[BuildTarget]", log);
            EnsureProfileVariable(settings, buildSettings.RemoteLoadPathVariableName, LoadPathPlaceholder, log);
            // A group's BundledAssetGroupSchema is a *separate* asset (in AddressableAssetsData/…/Schemas)
            // referenced by GUID — it does NOT travel inside the content repo's group file. After a fresh
            // checkout (or if Addressables regenerated it) that reference dangles and GetSchema returns
            // null. Recreate it so the build proceeds instead of throwing a NullReferenceException below.
            var schemaCfg = group.GetSchema<BundledAssetGroupSchema>();
            if (schemaCfg == null)
            {
                schemaCfg = group.AddSchema<BundledAssetGroupSchema>();
                log?.Invoke($"[Build] Group '{contentPackageName}' had no BundledAssetGroupSchema (its schema asset was missing) — recreated it.");
            }
            schemaCfg.BuildPath.SetVariableByName(settings, buildSettings.RemoteBuildPathVariableName);
            schemaCfg.LoadPath.SetVariableByName(settings, buildSettings.RemoteLoadPathVariableName);
            schemaCfg.BundleNaming = BundledAssetGroupSchema.BundleNamingStyle.OnlyHash;

            // Ensure the group file lives in _groups/ in the content repo so it can be
            // committed and sparse-checked-out independently of the package content.
            // AssetDatabase.MoveAsset preserves the GUID, keeping bundle hashes stable.
            var targetGroupPath  = $"{groupsFolder}/{contentPackageName}.asset";
            var currentGroupPath = AssetDatabase.GetAssetPath(group);
            if (!string.Equals(currentGroupPath.Replace('\\', '/'), targetGroupPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!AssetDatabase.IsValidFolder(groupsFolder))
                    AssetDatabase.CreateFolder(repoLocalPath, ContentGitApi.GroupsFolderName);

                var moveError = AssetDatabase.MoveAsset(currentGroupPath, targetGroupPath);
                if (!string.IsNullOrEmpty(moveError))
                    log?.Invoke($"[Build] WARNING: Could not move group file to '{targetGroupPath}': {moveError}. " +
                                "Builds will still work but the file won't benefit from content-repo sparse-checkout.");
                else
                    log?.Invoke($"[Build] Group file is at '{targetGroupPath}' (in content repo, commit with other changes).");
            }

            schemaCfg.IncludeInBuild = true;

            // Sync entries without resetting addresses: remove stale entries (assets deleted from
            // the folder), add new ones, and leave existing entries untouched so any custom
            // addresses set by content authors are preserved across builds.
            var guidSet      = new HashSet<string>(guids, StringComparer.OrdinalIgnoreCase);
            var existingGuids = new HashSet<string>(group.entries.Select(e => e.guid), StringComparer.OrdinalIgnoreCase);

            foreach (var stale in group.entries.Where(e => !guidSet.Contains(e.guid)).ToList())
                group.RemoveAssetEntry(stale, postEvent: false);

            var added = 0;
            foreach (var guid in guids.Where(g => !existingGuids.Contains(g)))
            {
                settings.CreateOrMoveEntry(guid, group, readOnly: false, postEvent: false);
                added++;
            }

            AssetDatabase.SaveAssets();
            log?.Invoke($"[Build] Group '{contentPackageName}': {group.entries.Count} asset(s) ({added} added, {existingGuids.Count - (guidSet.Count - added)} removed) from {contentAssetPath}");
            return group;
        }

        private static void DeactivateTemporaryGroup(AddressableAssetSettings settings,
            AddressableAssetGroup group, BuildLogHandler log)
        {
            if (group == null) return;
            try
            {
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema != null) schema.IncludeInBuild = false;
                AssetDatabase.SaveAssets();
                log?.Invoke($"[Build] Deactivated group '{group.Name}' (IncludeInBuild = false)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ContentRepo] Failed to deactivate temp Addressables group: {ex.Message}");
            }
        }

        internal static string ComputeBuildId(string[] bundleFiles)
        {
            var names = bundleFiles
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal);
            var combined = string.Join("|", names);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        private static string ResolveBuildOutputPath(AddressableAssetSettings settings, string profileId, string varName)
        {
            var value = settings.profileSettings.GetValueByName(profileId, varName);
            if (string.IsNullOrEmpty(value))
                value = "[UnityEngine.AddressableAssets.Addressables.BuildPath]";
            var evaluated = settings.profileSettings.EvaluateString(profileId, value);
            return Path.IsPathRooted(evaluated) ? evaluated : Path.GetFullPath(Path.Combine(ContentGitApi.ProjectRoot, evaluated));
        }

        private static void WriteMetadata(string artifactPath, string pkg, string platform,
            string generation, string buildId, string gitSha)
        {
            var meta = new BuildMetadata
            {
                contentPackage = pkg,
                platform = platform,
                generation = generation,
                buildId = buildId,
                gitSha = gitSha ?? "",
                unityVersion = Application.unityVersion,
                builtAt = DateTime.UtcNow.ToString("O"),
            };
            File.WriteAllText(Path.Combine(artifactPath, "build-metadata.json"), meta.ToJson());
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, dest));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(source, dest), true);
        }

        private static async Task<string> TryGetGitShaAsync()
        {
            try
            {
                var sha = await ContentGitApi.RunGitCommandAsync("rev-parse --short HEAD", ContentGitApi.ContentAbsolutePath);
                return sha.Trim();
            }
            catch { return null; }
        }

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

        private static void ValidatePackageName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Content package name cannot be empty.", nameof(name));
            if (name.IndexOfAny(new[] { '/', '\\', '\n', '\r', '\0', '"', ' ' }) >= 0)
                throw new ArgumentException("Invalid content package name.", nameof(name));
        }
    }
}
