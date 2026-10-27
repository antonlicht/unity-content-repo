using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Saves only the Addressables assets the content tooling touched. A project-wide
    /// <see cref="AssetDatabase.SaveAssets"/> also flushes unrelated dirty assets (TMP glyph tables,
    /// other groups) at moments nobody chose, and every Addressables settings setter zeroes the
    /// settings hash, so restoring a value and saving still produced a diff.
    /// </summary>
    internal static class ContentAssetSaving
    {
        private static readonly Regex HashField = new("\"m_currentHash\":\\{[^}]*\\},?");

        public static void SaveGroup(AddressableAssetGroup group)
        {
            if (group == null) return;
            AssetDatabase.SaveAssetIfDirty(group);
            foreach (var schema in group.Schemas)
                if (schema != null) AssetDatabase.SaveAssetIfDirty(schema);
        }

        public static void SaveSettings(AddressableAssetSettings settings)
        {
            if (settings != null) ContentGroupRegistry.SaveSettings(settings);
        }

        /// <summary>Snapshot of the settings without the hash, to detect changes that were fully restored.</summary>
        public static string Fingerprint(AddressableAssetSettings settings) =>
            settings == null ? null : HashField.Replace(EditorJsonUtility.ToJson(settings), "");

        /// <summary>
        /// Saves the settings when their content changed since <paramref name="fingerprint"/> was taken;
        /// otherwise clears the dirty flag the temporary changes left behind (if it was clean before).
        /// </summary>
        public static void SaveSettingsIfChanged(AddressableAssetSettings settings, string fingerprint, bool wasDirty)
        {
            if (settings == null) return;
            if (!wasDirty && Fingerprint(settings) == fingerprint) EditorUtility.ClearDirty(settings);
            else ContentGroupRegistry.SaveSettings(settings);
        }

        /// <summary>
        /// Selects the Addressables Play Mode Script (a per-user setting, not saved in the project) by
        /// builder type. Writing <c>ActivePlayerDataBuilderIndex</c> instead changed the tracked
        /// settings asset and could leave a play-mode builder active for player builds.
        /// </summary>
        public static void UsePlayModeScript<T>(AddressableAssetSettings settings, BuildLogHandler log = null) where T : BuildScriptBase
        {
            if (settings == null) return;
            var index = settings.DataBuilders.FindIndex(builder => builder is T);
            if (index < 0 || settings.ActivePlayModeDataBuilderIndex == index) return;
            settings.ActivePlayModeDataBuilderIndex = index;
            log?.Invoke($"[ContentRepo] Switched Addressables Play Mode Script to '{settings.ActivePlayModeDataBuilder.Name}'.");
        }
    }
}
