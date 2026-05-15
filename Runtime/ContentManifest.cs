using System;
using System.Collections.Generic;
using UnityEngine;

namespace ContentRepo
{
    [Serializable]
    public sealed class ContentManifestPlatformEntry
    {
        public string platform;
        public string catalogUrl;
        public string buildId;
    }

    [Serializable]
    public sealed class ContentManifestEntry
    {
        public string name;
        public string gitSha;
        public List<ContentManifestPlatformEntry> platforms = new();

        public ContentManifestPlatformEntry FindPlatform(string platform)
        {
            for (var i = 0; i < platforms.Count; i++)
                if (string.Equals(platforms[i].platform, platform, StringComparison.Ordinal))
                    return platforms[i];
            return null;
        }

        public void UpsertPlatform(ContentManifestPlatformEntry entry)
        {
            var existing = FindPlatform(entry.platform);
            if (existing == null) { platforms.Add(entry); return; }
            existing.catalogUrl = entry.catalogUrl;
            existing.buildId = entry.buildId;
        }
    }

    [Serializable]
    public sealed class ContentManifest
    {
        public int version = 1;
        public string updatedAt;
        public string environment;
        public string minAppVersion;
        public string recommendedAppVersion;
        public List<ContentManifestEntry> contentPackages = new();

        public ContentManifestEntry Find(string contentPackageName)
        {
            for (var i = 0; i < contentPackages.Count; i++)
                if (string.Equals(contentPackages[i].name, contentPackageName, StringComparison.Ordinal))
                    return contentPackages[i];
            return null;
        }

        public void UpsertEntry(ContentManifestEntry entry)
        {
            var existing = Find(entry.name);
            if (existing == null) { contentPackages.Add(entry); return; }
            existing.gitSha = entry.gitSha;
            foreach (var p in entry.platforms)
                existing.UpsertPlatform(p);
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public static ContentManifest FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonUtility.FromJson<ContentManifest>(json); }
            catch { return null; }
        }
    }

    // Simple semantic version comparison — only major.minor.patch supported.
    public static class AppVersion
    {
        public enum CompareResult { Older, Same, Newer }

        public static CompareResult Compare(string a, string b)
        {
            if (!TryParse(a, out var av) || !TryParse(b, out var bv)) return CompareResult.Same;
            if (av[0] != bv[0]) return av[0] < bv[0] ? CompareResult.Older : CompareResult.Newer;
            if (av[1] != bv[1]) return av[1] < bv[1] ? CompareResult.Older : CompareResult.Newer;
            if (av[2] != bv[2]) return av[2] < bv[2] ? CompareResult.Older : CompareResult.Newer;
            return CompareResult.Same;
        }

        private static bool TryParse(string v, out int[] parts)
        {
            parts = null;
            if (string.IsNullOrWhiteSpace(v)) return false;
            var segments = v.Split('.');
            if (segments.Length < 3) return false;
            parts = new int[3];
            for (var i = 0; i < 3; i++)
                if (!int.TryParse(segments[i], out parts[i])) return false;
            return true;
        }
    }
}
