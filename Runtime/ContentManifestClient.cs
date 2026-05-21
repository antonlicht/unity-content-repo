using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ContentRepo
{
    public static class ContentManifestClient
    {
        private const string ManifestFileName = "manifest.json";
        private const string CacheSubFolder = "ContentRepo";

        public static async Task<ContentManifest> FetchAsync(
            string baseUrl, string environment, string generation, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl required.", nameof(baseUrl));
            if (string.IsNullOrWhiteSpace(environment)) throw new ArgumentException("environment required.", nameof(environment));
            if (string.IsNullOrWhiteSpace(generation)) throw new ArgumentException("generation required.", nameof(generation));

            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                baseUrl = "https://" + baseUrl;
            var url = $"{baseUrl.TrimEnd('/')}/{generation}/{environment}/{ManifestFileName}";
            try
            {
                var json = await DownloadTextAsync(url, ct);
                var manifest = ContentManifest.FromJson(json);
                if (manifest == null) throw new InvalidDataException($"Manifest at {url} could not be parsed.");
                SaveCached(environment, generation, json);
                ContentLocalDevOverrides.InjectIntoManifest(manifest);
                return manifest;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ContentRepo] Manifest fetch from {url} failed: {ex.Message}. Falling back to cache.");
                var cached = LoadCached(environment, generation);
                ContentLocalDevOverrides.InjectIntoManifest(cached);
                return cached;
            }
        }

        public static ContentManifest LoadCached(string environment, string generation)
        {
            try
            {
                var path = CachePath(environment, generation);
                if (!File.Exists(path)) return null;
                var m = ContentManifest.FromJson(File.ReadAllText(path));
                ContentLocalDevOverrides.InjectIntoManifest(m);
                return m;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ContentRepo] Cache read failed: {ex.Message}");
                return null;
            }
        }

        public static void SaveCached(string environment, string generation, string json)
        {
            try
            {
                var path = CachePath(environment, generation);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Cache write failed: {ex.Message}"); }
        }

        public static async Task<ContentManifestEntry> ResolveAsync(
            string baseUrl, string environment, string generation,
            string contentPackageName, CancellationToken ct = default)
        {
            var manifest = await FetchAsync(baseUrl, environment, generation, ct);
            return manifest?.Find(contentPackageName);
        }

        public static void ClearCache(string environment, string generation)
        {
            try
            {
                var path = CachePath(environment, generation);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Cache delete failed: {ex.Message}"); }
        }

        private static string CachePath(string environment, string generation) =>
            Path.Combine(Application.persistentDataPath, CacheSubFolder, generation, environment, ManifestFileName);

        private static Task<string> DownloadTextAsync(string url, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("Cache-Control", "no-cache");

            CancellationTokenRegistration ctReg = default;
            if (ct.CanBeCanceled)
                ctReg = ct.Register(() => { try { req.Abort(); } catch { /* ignored */ } tcs.TrySetCanceled(ct); });

            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (req.result == UnityWebRequest.Result.Success)
                        tcs.TrySetResult(req.downloadHandler.text);
                    else
                        tcs.TrySetException(new InvalidOperationException(
                            $"GET {url} failed: {req.result} {req.responseCode} {req.error}"));
                }
                finally { ctReg.Dispose(); req.Dispose(); }
            };
            return tcs.Task;
        }
    }
}
