using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Keeps the Addressables groups of this machine's checked-out content packages in sync whenever the
    /// editor is about to enter Play Mode.
    ///
    /// The packages are the ones in the sparse cone (<see cref="ContentGroupRegistry.ActivePackages"/>), not
    /// every folder on disk: Unity recreates an empty folder for every top-level package .meta. For each one
    /// the canonical group (<c>_groups/&lt;pkg&gt;.asset</c>) is registered in memory, created at that path if the
    /// package has none yet, and synced with the folder: new assets are added, stale entries removed, and
    /// custom addresses on existing entries preserved.
    ///
    /// In addition, every checked-out package that does not already have a
    /// <see cref="LocalDevMode.LocalBundles"/> override registered is automatically registered
    /// as <see cref="LocalDevMode.AssetDatabase"/>. When at least one such package is found the
    /// Addressables Play Mode Script (a per-user setting) is switched to <em>Use Asset Database
    /// (fastest)</em> so the assets are served directly without any bundle build, or to
    /// <em>Use Existing Build</em> when a package has a LocalBundles override.
    /// </summary>
    [InitializeOnLoad]
    internal static class ContentGroupAutoSetup
    {
        static ContentGroupAutoSetup()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode) return;

            ContentRepoSettings settings;
            try { settings = ContentRepoSettings.instance; }
            catch { return; }

            if (settings == null) return;

            var repoLocalPath = settings.LocalPath?.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrWhiteSpace(repoLocalPath)) return;

            if (!repoLocalPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return;

            if (!AssetDatabase.IsValidFolder(repoLocalPath)) return;

            var addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addressableSettings == null) return;

            ContentGroupRegistry.Reconcile();
            var packages = ContentGroupRegistry.ActivePackages();
            if (packages.Count == 0) return;

            var changedGroups = new List<AddressableAssetGroup>();
            foreach (var packageName in packages)
            {
                var folderPath = $"{repoLocalPath}/{packageName}";
                if (NeedsSync(packageName, folderPath))
                {
                    try
                    {
                        var group = ContentLocalDevApi.EnsureGroupPopulated(addressableSettings, packageName, msg => Debug.Log(msg));

                        // Apply the package-name label so label-based queries in
                        // ContentLoadingTest and ContentRepoRuntime work correctly.
                        foreach (var entry in group.entries)
                            entry.SetLabel(packageName, true, true, false);
                        changedGroups.Add(group);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[ContentRepo] Auto-setup failed for package '{packageName}': {ex.Message}");
                    }
                }

                // Register AssetDatabase override for every checked-out package that doesn't
                // already have a LocalBundles override (which requires a prior bundle build and
                // should not be silently downgraded).
                if (!ContentLocalDevOverrides.TryGet(packageName, out var existingEntry)
                    || existingEntry.Mode != LocalDevMode.LocalBundles)
                {
                    ContentLocalDevOverrides.Register(packageName, LocalDevMode.AssetDatabase);
                }
            }

            foreach (var group in changedGroups) ContentAssetSaving.SaveGroup(group);
            ContentAssetSaving.SaveSettings(addressableSettings);
            ContentLocalDevApi.SaveToPrefs();

            // Switch the per-user Play Mode Script to match the dominant local-dev mode:
            // "Use Existing Build" if any package uses LocalBundles, otherwise "Use Asset Database".
            var hasLocalBundles = packages.Any(p => ContentLocalDevOverrides.TryGet(p, out var e) && e.Mode == LocalDevMode.LocalBundles);
            if (hasLocalBundles) ContentAssetSaving.UsePlayModeScript<BuildScriptPackedPlayMode>(addressableSettings, Debug.Log);
            else ContentAssetSaving.UsePlayModeScript<BuildScriptFastMode>(addressableSettings, Debug.Log);
        }

        /// <summary>
        /// Returns <c>true</c> when the package's canonical group does not exist yet, is not registered, or
        /// differs from the assets on disk in <paramref name="folderPath"/>.
        /// </summary>
        private static bool NeedsSync(string packageName, string folderPath)
        {
            var group = AssetDatabase.LoadAssetAtPath<AddressableAssetGroup>(ContentGroupRegistry.CanonicalGroupPath(packageName));
            if (group == null) return true;

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || !settings.groups.Contains(group)) return true;

            var diskGuids = AssetDatabase.FindAssets("", new[] { folderPath })
                .Where(g => !AssetDatabase.IsValidFolder(AssetDatabase.GUIDToAssetPath(g)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (diskGuids.Count == 0) return false;

            var groupGuids = new HashSet<string>(group.entries.Select(e => e.guid), StringComparer.OrdinalIgnoreCase);
            return diskGuids.Any(g => !groupGuids.Contains(g)) || groupGuids.Any(g => !diskGuids.Contains(g));
        }
    }
}
