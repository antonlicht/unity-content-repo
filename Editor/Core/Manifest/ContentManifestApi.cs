using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace ContentRepo.Editor
{
    public static class ContentManifestApi
    {
        private const string ManifestFileName = "manifest.json";

        /// <summary>
        /// Merges <paramref name="updatedEntries"/> into the existing manifest for
        /// <paramref name="environment"/> and uploads it, then invalidates the CDN path.
        /// </summary>
        public static async Task PublishAsync(
            string environment,
            string generation,
            IEnumerable<ContentManifestEntry> updatedEntries,
            IContentUploadProvider provider,
            UploadLogHandler log = null)
        {
            if (string.IsNullOrWhiteSpace(environment)) throw new ArgumentException("environment required.");
            if (string.IsNullOrWhiteSpace(generation)) throw new ArgumentException("generation required.");

            var remoteKey = $"{generation}/{environment}/{ManifestFileName}";
            var existingJson = await provider.DownloadTextAsync(remoteKey);
            var manifest = ContentManifest.FromJson(existingJson) ?? new ContentManifest { environment = environment };

            manifest.environment = environment;
            manifest.updatedAt = DateTime.UtcNow.ToString("O");

            if (updatedEntries != null)
                foreach (var e in updatedEntries)
                    manifest.UpsertEntry(e);

            await UploadManifestAsync(manifest, remoteKey, provider, log);
        }

        /// <summary>
        /// Upserts a single pre-built manifest entry (used by Promote).
        /// </summary>
        public static async Task UpsertEntryAsync(
            string environment,
            string generation,
            ContentManifestEntry entry,
            IContentUploadProvider provider,
            UploadLogHandler log = null)
        {
            var remoteKey = $"{generation}/{environment}/{ManifestFileName}";
            var existingJson = await provider.DownloadTextAsync(remoteKey);
            var manifest = ContentManifest.FromJson(existingJson) ?? new ContentManifest { environment = environment };

            manifest.environment = environment;
            manifest.updatedAt = DateTime.UtcNow.ToString("O");
            manifest.UpsertEntry(entry);

            await UploadManifestAsync(manifest, remoteKey, provider, log);
        }

        /// <summary>
        /// Updates only the app-version fields on an existing manifest without touching content packages.
        /// </summary>
        public static async Task SetAppVersionsAsync(
            string environment,
            string generation,
            string minAppVersion,
            string recommendedAppVersion,
            IContentUploadProvider provider,
            UploadLogHandler log = null)
        {
            var remoteKey = $"{generation}/{environment}/{ManifestFileName}";
            var existingJson = await provider.DownloadTextAsync(remoteKey);
            var manifest = ContentManifest.FromJson(existingJson) ?? new ContentManifest { environment = environment };

            manifest.minAppVersion = minAppVersion;
            manifest.recommendedAppVersion = recommendedAppVersion;
            manifest.updatedAt = DateTime.UtcNow.ToString("O");

            await UploadManifestAsync(manifest, remoteKey, provider, log);
        }

        private static async Task UploadManifestAsync(
            ContentManifest manifest, string remoteKey, IContentUploadProvider provider, UploadLogHandler log)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"content-manifest-{Guid.NewGuid():N}.json");
            try
            {
                await Task.Run(() => File.WriteAllText(tempFile, manifest.ToJson()));
                log?.Invoke($"[Manifest] Uploading {remoteKey}");
                await provider.UploadFileAsync(tempFile, remoteKey, log);
                await provider.InvalidatePathAsync($"/{remoteKey}", log);
                log?.Invoke("[Manifest] Published.");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* best-effort */ }
            }
        }
    }
}
