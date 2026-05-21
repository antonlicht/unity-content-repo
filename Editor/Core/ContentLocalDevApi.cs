using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace ContentRepo.Editor
{
    // ── Persistence helpers ───────────────────────────────────────────────────

    [Serializable]
    internal sealed class LocalDevSaveData
    {
        public List<LocalDevSaveEntry> entries = new();
    }

    [Serializable]
    internal sealed class LocalDevSaveEntry
    {
        public string packageName;
        public string mode;         // "AssetDatabase" or "LocalBundles"
        public string catalogUrl;   // only for LocalBundles
    }

    // ── Auto-restore on every domain reload (includes Play Mode enter) ────────

    [InitializeOnLoad]
    internal static class ContentLocalDevLoader
    {
        static ContentLocalDevLoader() => ContentLocalDevApi.RestoreFromPrefs();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public static class ContentLocalDevApi
    {
        private const string PrefsKey = "ContentRepo.LocalDevOverrides";

        // ── AssetDatabase (Fast Mode) ─────────────────────────────────────────

        /// <summary>
        /// Ensures the Addressables group for <paramref name="packageName"/> is created and
        /// populated with every asset from the checked-out folder, applies the package name
        /// as a label to all entries, and registers an AssetDatabase override so that
        /// <see cref="ContentRepoRuntime.LoadCatalogsAsync"/> skips remote catalog loading
        /// for this package entirely.
        ///
        /// Requires Play Mode Script = "Use Asset Database (fastest)" (index 0).
        /// Automatically switches to that script.
        /// </summary>
        public static void SetupForFastMode(string packageName, BuildLogHandler log = null)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                throw new InvalidOperationException(
                    "Addressables is not initialized. Open Window > Asset Management > Addressables > Groups.");

            var group = EnsureGroupPopulated(settings, packageName, log);

            // Ensure the label exists in the global label table, then apply it to every entry.
            settings.AddLabel(packageName, postEvent: false);
            foreach (var entry in group.entries)
                entry.SetLabel(packageName, enable: true, force: false, postEvent: false);

            AssetDatabase.SaveAssets();

            // Switch Play Mode Script to "Use Asset Database" (index 0).
            if (settings.ActivePlayerDataBuilderIndex != 0)
            {
                settings.ActivePlayerDataBuilderIndex = 0;
                log?.Invoke("[LocalDev] Switched Play Mode Script to 'Use Asset Database (fastest)'.");
            }

            ContentLocalDevOverrides.Register(packageName, LocalDevMode.AssetDatabase);
            SaveToPrefs();
            log?.Invoke($"[LocalDev] '{packageName}' → AssetDatabase override active. Enter Play Mode to test.");
        }

        public static void ClearFastMode(string packageName, BuildLogHandler log = null)
        {
            ContentLocalDevOverrides.Unregister(packageName);
            SaveToPrefs();
            log?.Invoke($"[LocalDev] '{packageName}' AssetDatabase override cleared.");
        }

        // ── LocalBundles (Use Existing Build) ─────────────────────────────────

        /// <summary>
        /// Builds the content package locally, rewrites the built catalog with <c>file://</c>
        /// load paths, and registers a LocalBundles override so that
        /// <see cref="ContentRepoRuntime.LoadCatalogsAsync"/> uses the local catalog instead of
        /// the CDN URL for this package.
        ///
        /// Requires Play Mode Script = "Use Existing Build" (index 2).
        /// Automatically switches to that script.
        /// </summary>
        public static async Task<ContentBuildResult> BuildAndRegisterLocalBundlesAsync(
            string packageName, BuildLogHandler log = null)
        {
            var result = await ContentBuildApi.BuildContentPackageAsync(packageName, log);

            var localDir     = result.ArtifactPath;
            var localBaseUrl = "file:///" + localDir.Replace('\\', '/').TrimEnd('/') + "/";

            // Rewrite the CDN placeholder inside the artifact catalog(s) to file:// paths.
            ContentBuildApi.RewriteCatalogLoadPaths(localDir, localBaseUrl);
            log?.Invoke($"[LocalDev] Catalog load paths rewritten to: {localBaseUrl}");

            // Locate the catalog JSON Addressables writes as catalog_<packageName>.json.
            var catalogFile = Directory.GetFiles(localDir, "catalog_*.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (catalogFile == null)
                throw new FileNotFoundException(
                    $"No catalog JSON found in '{localDir}'. " +
                    "Ensure BuildRemoteCatalog is enabled for this content package.");

            var catalogUrl = "file:///" + catalogFile.Replace('\\', '/');

            // Switch Play Mode Script to "Use Existing Build" (index 2).
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings != null && settings.ActivePlayerDataBuilderIndex != 2)
            {
                settings.ActivePlayerDataBuilderIndex = 2;
                log?.Invoke("[LocalDev] Switched Play Mode Script to 'Use Existing Build'.");
            }

            ContentLocalDevOverrides.Register(packageName, LocalDevMode.LocalBundles, catalogUrl);
            SaveToPrefs();
            log?.Invoke($"[LocalDev] '{packageName}' → LocalBundles override active. Catalog: {catalogUrl}");
            log?.Invoke("[LocalDev] Enter Play Mode to test with local bundles.");

            return result;
        }

        public static void ClearLocalBundles(string packageName, BuildLogHandler log = null)
        {
            ContentLocalDevOverrides.Unregister(packageName);
            SaveToPrefs();
            log?.Invoke($"[LocalDev] '{packageName}' LocalBundles override cleared.");
        }

        // ── Persistence ───────────────────────────────────────────────────────

        internal static void RestoreFromPrefs()
        {
            ContentLocalDevOverrides.Clear();
            var json = EditorPrefs.GetString(PrefsKey, null);
            if (string.IsNullOrEmpty(json)) return;
            LocalDevSaveData data;
            try { data = JsonUtility.FromJson<LocalDevSaveData>(json); }
            catch { return; }
            if (data?.entries == null) return;

            foreach (var e in data.entries)
            {
                if (string.IsNullOrEmpty(e.packageName)) continue;
                if (!Enum.TryParse<LocalDevMode>(e.mode, out var mode) || mode == LocalDevMode.None) continue;
                ContentLocalDevOverrides.Register(e.packageName, mode, e.catalogUrl);
            }
        }

        private static void SaveToPrefs()
        {
            var data = new LocalDevSaveData();
            foreach (var kv in ContentLocalDevOverrides.All)
            {
                data.entries.Add(new LocalDevSaveEntry
                {
                    packageName = kv.Key,
                    mode        = kv.Value.Mode.ToString(),
                    catalogUrl  = kv.Value.LocalCatalogUrl,
                });
            }
            EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(data));
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Creates or refreshes the Addressables group for the package, populating it with all
        /// assets from the checked-out content folder. Mirrors the logic in
        /// <see cref="ContentBuildApi.BuildContentPackageAsync"/> without triggering a full build.
        /// </summary>
        private static AddressableAssetGroup EnsureGroupPopulated(
            AddressableAssetSettings settings,
            string packageName,
            BuildLogHandler log)
        {
            var repoLocalPath    = ContentRepoSettings.instance.LocalPath.Replace('\\', '/').TrimEnd('/');
            var contentAssetPath = $"{repoLocalPath}/{packageName}";

            if (!contentAssetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Content repo local path '{repoLocalPath}' must be inside Assets/.");

            if (!AssetDatabase.IsValidFolder(contentAssetPath))
                throw new InvalidOperationException(
                    $"'{contentAssetPath}' not found in the Asset Database. " +
                    $"Make sure '{packageName}' is checked out.");

            var guids = AssetDatabase.FindAssets("", new[] { contentAssetPath })
                .Where(g => !AssetDatabase.IsValidFolder(AssetDatabase.GUIDToAssetPath(g)))
                .ToArray();

            if (guids.Length == 0)
                throw new InvalidOperationException(
                    $"No assets found under '{contentAssetPath}'. Check the folder contains importable assets.");

            var group = settings.FindGroup(packageName);
            if (group == null)
            {
                group = settings.CreateGroup(
                    packageName,
                    setAsDefaultGroup: false,
                    readOnly: false,
                    postEvent: false,
                    schemasToCopy: null,
                    typeof(BundledAssetGroupSchema));
                log?.Invoke($"[LocalDev] Created Addressables group '{packageName}'.");
            }
            else
            {
                log?.Invoke($"[LocalDev] Refreshing Addressables group '{packageName}'.");
            }

            // Sync entries: add new assets, remove stale ones, leave existing ones untouched
            // (preserves any custom addresses set by content authors).
            var guidSet       = new HashSet<string>(guids,                              StringComparer.OrdinalIgnoreCase);
            var existingGuids = new HashSet<string>(group.entries.Select(e => e.guid),  StringComparer.OrdinalIgnoreCase);

            foreach (var stale in group.entries.Where(e => !guidSet.Contains(e.guid)).ToList())
                group.RemoveAssetEntry(stale, postEvent: false);

            var added = 0;
            foreach (var guid in guids.Where(g => !existingGuids.Contains(g)))
            {
                settings.CreateOrMoveEntry(guid, group, readOnly: false, postEvent: false);
                added++;
            }

            log?.Invoke($"[LocalDev] Group '{packageName}': {group.entries.Count} asset(s) ({added} added).");
            return group;
        }
    }
}
