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

        /// <summary>
        /// Appends a synthetic <see cref="ContentManifestEntry"/> for every local-dev package
        /// that is not already present in <paramref name="manifest"/>. This makes local-only
        /// packages (not yet deployed to CDN) visible to every caller of
        /// <see cref="ContentManifestClient.FetchAsync"/> — including UI code that builds
        /// button lists from <c>manifest.contentPackages</c>.
        /// </summary>
        public static void InjectIntoManifest(ContentManifest manifest)
        {
            if (manifest == null || Entries.Count == 0) return;
            foreach (var kv in Entries)
            {
                if (manifest.Find(kv.Key) != null) continue;   // already in manifest — skip
                manifest.contentPackages.Add(new ContentManifestEntry { name = kv.Key });
            }
        }
    }
}
