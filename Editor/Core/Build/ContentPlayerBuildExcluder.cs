using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Guarantees content-repo packages never ship inside the client player build. Content is
    /// delivered over-the-air via per-package Addressables bundles, so at player-build time every
    /// content-package group — identified by its group asset living under the content repo's
    /// <see cref="ContentGitApi.GroupsFolderName"/> (<c>_groups</c>) folder — is forced to
    /// <c>IncludeInBuild = false</c>, then restored in post-process.
    ///
    /// This runs with a very low <see cref="callbackOrder"/> so it executes before Naninovel's
    /// resource build (order 100) and Unity's own Addressables player-build processor, making the
    /// exclusion deterministic regardless of each group's current IncludeInBuild state or the
    /// project's "Build Addressables on Player Build" preference. It complements the Naninovel
    /// <c>ResourcesBuilder.ExcludeAssetFromBuild</c> hook (which stops content from being
    /// re-registered into the main Naninovel group); together they make the client content-free.
    /// </summary>
    public sealed class ContentPlayerBuildExcluder : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        private static readonly List<BundledAssetGroupSchema> disabled = new();

        public void OnPreprocessBuild(BuildReport report)
        {
            disabled.Clear();

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            var marker = $"/{ContentGitApi.GroupsFolderName}/"; // "/_groups/"
            foreach (var group in settings.groups)
            {
                if (group == null) continue;
                var groupAssetPath = AssetDatabase.GetAssetPath(group).Replace('\\', '/');
                if (!groupAssetPath.Contains(marker)) continue; // not a content-repo package group

                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null || !schema.IncludeInBuild) continue;

                schema.IncludeInBuild = false;
                disabled.Add(schema);
                Debug.Log($"[ContentRepo] Excluding content group '{group.Name}' from the player build (delivered over-the-air).");
            }

            if (disabled.Count > 0) AssetDatabase.SaveAssets();
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (disabled.Count == 0) return;
            foreach (var schema in disabled)
                if (schema != null) schema.IncludeInBuild = true;
            disabled.Clear();
            AssetDatabase.SaveAssets();
        }
    }
}
