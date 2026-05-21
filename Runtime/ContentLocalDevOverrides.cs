using System.Collections.Generic;

namespace ContentRepo
{
    public enum LocalDevMode { None, AssetDatabase, LocalBundles }

    /// <summary>
    /// Runtime registry of per-package local-dev overrides.
    /// Populated by ContentLocalDevApi (Editor) before/on Play Mode entry via
    /// [InitializeOnLoad] + EditorPrefs, so it survives domain reloads.
    /// In non-Editor builds this is always empty and has no effect.
    /// </summary>
    public static class ContentLocalDevOverrides
    {
        public sealed class LocalDevEntry
        {
            public LocalDevMode Mode;
            /// <summary>file:// URL to the local catalog JSON. Only used for LocalBundles mode.</summary>
            public string LocalCatalogUrl;
        }

        private static readonly Dictionary<string, LocalDevEntry> Entries =
            new(System.StringComparer.Ordinal);

        public static void Register(string packageName, LocalDevMode mode, string localCatalogUrl = null)
        {
            Entries[packageName] = new LocalDevEntry { Mode = mode, LocalCatalogUrl = localCatalogUrl };
        }

        public static void Unregister(string packageName) => Entries.Remove(packageName);

        public static void Clear() => Entries.Clear();

        public static bool TryGet(string packageName, out LocalDevEntry entry) =>
            Entries.TryGetValue(packageName, out entry);

        public static IReadOnlyDictionary<string, LocalDevEntry> All => Entries;
    }
}
