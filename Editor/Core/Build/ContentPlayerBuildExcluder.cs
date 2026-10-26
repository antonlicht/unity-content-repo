using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Guarantees content-repo packages never ship inside the client player build. Content is
    /// delivered over-the-air via per-package Addressables bundles, so at player-build time every
    /// content-package group is forced to <c>IncludeInBuild = false</c> and restored in post-process,
    /// or on the next editor update once no player build is running (covers failed builds).
    ///
    /// A group counts as a content-package group when its asset lives under the content repo's
    /// <see cref="ContentGitApi.GroupsFolderName"/> (<c>_groups</c>) folder, or when every entry it
    /// holds lives under the content root (this also catches package groups that ended up in the main
    /// project's AssetGroups folder). Groups that mix content and project assets are left alone and
    /// reported, since excluding them would strip project assets from the client.
    ///
    /// This is a <see cref="BuildPlayerProcessor"/> so it runs in <c>PrepareForBuild</c>, before
    /// Unity's own Addressables player-build processor (order 1) builds the local content; an
    /// <c>IPreprocessBuildWithReport</c> runs after that step and would be too late.
    /// </summary>
    public sealed class ContentPlayerBuildExcluder : BuildPlayerProcessor, IPostprocessBuildWithReport
    {
        public override int callbackOrder => -1000;

        private static readonly List<BundledAssetGroupSchema> disabled = new();

        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext) => ExcludeContentGroups();

        public void OnPostprocessBuild(BuildReport report) => Restore();

        public static void ExcludeContentGroups()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;

            var contentRoot = ContentRootAssetPath();
            foreach (var group in settings.groups)
            {
                if (group == null || !IsContentGroup(group, contentRoot)) continue;

                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null || !schema.IncludeInBuild) continue;

                schema.IncludeInBuild = false;
                disabled.Add(schema);
                Debug.Log($"[ContentRepo] Excluding content group '{group.Name}' from the player build (delivered over-the-air).");
            }

            if (disabled.Count == 0) return;
            EditorApplication.update -= RestoreWhenIdle;
            EditorApplication.update += RestoreWhenIdle;
        }

        private static bool IsContentGroup(AddressableAssetGroup group, string contentRoot)
        {
            var groupAssetPath = AssetDatabase.GetAssetPath(group).Replace('\\', '/');
            if (groupAssetPath.Contains($"/{ContentGitApi.GroupsFolderName}/")) return true;
            if (string.IsNullOrEmpty(contentRoot) || group.entries.Count == 0) return false;

            var content = 0;
            foreach (var entry in group.entries)
                if (IsUnder(entry.AssetPath, contentRoot)) content++;
            if (content == group.entries.Count) return true;
            if (content > 0)
                Debug.LogWarning($"[ContentRepo] Group '{group.Name}' mixes {content} content-repo asset(s) with project assets; they will ship inside the player build. Move them into their package group.");
            return false;
        }

        private static string ContentRootAssetPath()
        {
            var relative = ContentRepoSettings.instance.LocalPath;
            if (string.IsNullOrEmpty(relative)) return null;
            return relative.Replace('\\', '/').Trim('/');
        }

        private static bool IsUnder(string assetPath, string root) =>
            !string.IsNullOrEmpty(assetPath) &&
            assetPath.Replace('\\', '/').StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

        private static void RestoreWhenIdle()
        {
            if (!BuildPipeline.isBuildingPlayer) Restore();
        }

        private static void Restore()
        {
            EditorApplication.update -= RestoreWhenIdle;
            if (disabled.Count == 0) return;
            foreach (var schema in disabled)
                if (schema != null) schema.IncludeInBuild = true;
            disabled.Clear();
            AssetDatabase.SaveAssets();
        }
    }
}
