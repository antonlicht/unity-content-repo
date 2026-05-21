using System;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Automatically keeps Addressables groups in sync with checked-out content packages
    /// whenever the editor is about to enter Play Mode.
    ///
    /// For every subfolder found under <see cref="ContentRepoSettings.LocalPath"/> (excluding
    /// the internal <c>_groups</c> folder) the script checks whether the corresponding
    /// Addressables group is missing or has assets that aren't registered yet, and silently
    /// fixes both conditions. Custom addresses on existing entries are preserved — only new
    /// assets are added and stale entries are removed.
    ///
    /// In addition, every checked-out package that does not already have a
    /// <see cref="LocalDevMode.LocalBundles"/> override registered is automatically registered
    /// as <see cref="LocalDevMode.AssetDatabase"/>. When at least one such package is found the
    /// Addressables Play Mode Script is switched to <em>Use Asset Database (fastest)</em>
    /// (index 0) so the assets are served directly without any bundle build.
    ///
    /// The check runs on <see cref="PlayModeStateChange.ExitingEditMode"/> so it is always
    /// up-to-date before any test or game session starts, without requiring manual action
    /// from the developer.
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

            // Enumerate direct subfolders of the content root.  Each subfolder that is not
            // the internal _groups folder is treated as a content package.
            var subFolders = AssetDatabase.GetSubFolders(repoLocalPath);
            if (subFolders == null || subFolders.Length == 0) return;

            var anyChange = false;
            var anyLocalPackage = false;
            foreach (var folderPath in subFolders)
            {
                var packageName = System.IO.Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(packageName)) continue;

                // Skip the internal _groups folder used by ContentGitApi.
                if (packageName.Equals(ContentGitApi.GroupsFolderName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (NeedsSync(addressableSettings, packageName, folderPath))
                {
                    try
                    {
                        ContentLocalDevApi.EnsureGroupPopulated(
                            addressableSettings,
                            packageName,
                            msg => Debug.Log(msg));

                        // Apply the package-name label so label-based queries in
                        // ContentLoadingTest and ContentRepoRuntime work correctly.
                        var group = addressableSettings.FindGroup(packageName);
                        if (group != null)
                        {
                            foreach (var entry in group.entries)
                                entry.SetLabel(packageName, true, true, false);
                        }

                        anyChange = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ContentRepo] Auto-setup failed for package '{packageName}': {ex.Message}");
                    }
                }

                // Register AssetDatabase override for every checked-out package that doesn't
                // already have a LocalBundles override (which requires a prior bundle build and
                // should not be silently downgraded).
                if (!ContentLocalDevOverrides.TryGet(packageName, out var existingEntry)
                    || existingEntry.Mode != LocalDevMode.LocalBundles)
                {
                    ContentLocalDevOverrides.Register(packageName, LocalDevMode.AssetDatabase);
                    anyLocalPackage = true;
                }
                else
                {
                    // Package has a LocalBundles override — keep it, but still note local presence.
                    anyLocalPackage = true;
                }
            }

            if (anyChange)
                AssetDatabase.SaveAssets();

            if (anyLocalPackage)
                ContentLocalDevApi.SaveToPrefs();

            // Switch Play Mode Script to match the dominant local-dev mode.
            // If any package uses LocalBundles we stay on "Use Existing Build" (2);
            // otherwise switch to "Use Asset Database (fastest)" (0).
            if (anyLocalPackage)
            {
                var hasLocalBundles = false;
                foreach (var folderPath in subFolders)
                {
                    var packageName = System.IO.Path.GetFileName(folderPath);
                    if (string.IsNullOrEmpty(packageName)) continue;
                    if (packageName.Equals(ContentGitApi.GroupsFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ContentLocalDevOverrides.TryGet(packageName, out var e) && e.Mode == LocalDevMode.LocalBundles)
                    {
                        hasLocalBundles = true;
                        break;
                    }
                }

                var targetIndex = hasLocalBundles ? 2 : 0;
                if (addressableSettings.ActivePlayerDataBuilderIndex != targetIndex)
                {
                    addressableSettings.ActivePlayerDataBuilderIndex = targetIndex;
                    var modeName = targetIndex == 0 ? "Use Asset Database (fastest)" : "Use Existing Build (requires built groups)";
                    Debug.Log($"[ContentRepo] Auto-setup: switched Addressables Play Mode Script to '{modeName}'.");
                }
            }
        }

        /// <summary>
        /// Returns <c>true</c> when the Addressables group for <paramref name="packageName"/>
        /// either does not exist yet, or is missing at least one asset that is present in
        /// <paramref name="folderPath"/> on disk (i.e. the group needs to be re-synced).
        /// </summary>
        private static bool NeedsSync(
            UnityEditor.AddressableAssets.Settings.AddressableAssetSettings settings,
            string packageName,
            string folderPath)
        {
            var group = settings.FindGroup(packageName);
            if (group == null) return true;  // group doesn't exist yet

            // Collect asset GUIDs that are currently on disk in the package folder.
            var diskGuids = AssetDatabase.FindAssets("", new[] { folderPath })
                .Where(g => !AssetDatabase.IsValidFolder(AssetDatabase.GUIDToAssetPath(g)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (diskGuids.Count == 0) return false;  // empty folder — nothing to sync

            var groupGuids = new System.Collections.Generic.HashSet<string>(
                group.entries.Select(e => e.guid),
                StringComparer.OrdinalIgnoreCase);

            // Sync is needed if any disk asset is absent from the group, or
            // if the group references assets that are no longer on disk.
            return diskGuids.Any(g => !groupGuids.Contains(g))
                || groupGuids.Any(g => !diskGuids.Contains(g));
        }
    }
}
