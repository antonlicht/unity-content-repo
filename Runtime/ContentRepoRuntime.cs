using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ContentRepo
{
    public enum UpdateRequiredReason { BelowMinimum, BelowRecommended }

    public sealed class ContentRepoInitResult
    {
        public ContentManifest Manifest;
        public List<CatalogLoadResult> Catalogs = new();
        public bool ManifestFromCache;
        public bool UpdateRequired;
        public bool UpdateForced;
    }

    public sealed class CatalogLoadResult
    {
        public string ContentPackageName;
        public string Platform;
        public string CatalogUrl;
        public string BuildId;
        public bool Success;
        public string Error;
        public IResourceLocator Locator;
    }

    public static class ContentRepoRuntime
    {
        private static readonly Dictionary<string, string> LoadedVersions = new(StringComparer.Ordinal);
        public static IReadOnlyDictionary<string, string> CurrentVersions => LoadedVersions;

        /// <summary>Fired when the running app is below minAppVersion (forced=true) or recommendedAppVersion.</summary>
        public static event Action<UpdateRequiredReason> OnUpdateRequired;

        public static event Action<ContentRepoInitResult> OnInitialized;

        /// <summary>
        /// Fetches the manifest for the current generation and environment, checks app version
        /// compatibility, then loads the per-platform catalog for every content package.
        /// Falls back to the locally cached manifest on CDN failures.
        /// </summary>
        public static async Task<ContentRepoInitResult> InitializeAsync(
            string baseUrl, string environment, string generation,
            bool force = false, CancellationToken ct = default)
        {
            var result = new ContentRepoInitResult();
            ContentManifest manifest;

            try
            {
                manifest = await ContentManifestClient.FetchAsync(baseUrl, environment, generation, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.LogError($"[ContentRepo] Manifest fetch failed: {ex.Message}");
                manifest = ContentManifestClient.LoadCached(environment, generation);
                result.ManifestFromCache = manifest != null;
            }

            if (manifest == null)
            {
                Debug.LogError("[ContentRepo] No manifest available — neither remote nor cached.");
                OnInitialized?.Invoke(result);
                return result;
            }

            result.Manifest = manifest;

            // Version check.
            var appVersion = Application.version;
            if (!string.IsNullOrWhiteSpace(manifest.minAppVersion) &&
                AppVersion.Compare(appVersion, manifest.minAppVersion) == AppVersion.CompareResult.Older)
            {
                result.UpdateRequired = true;
                result.UpdateForced = true;
                Debug.LogError($"[ContentRepo] App version {appVersion} is below minimum {manifest.minAppVersion}. Update required.");
                try { OnUpdateRequired?.Invoke(UpdateRequiredReason.BelowMinimum); } catch (Exception ex) { Debug.LogException(ex); }
                OnInitialized?.Invoke(result);
                return result;
            }

            if (!string.IsNullOrWhiteSpace(manifest.recommendedAppVersion) &&
                AppVersion.Compare(appVersion, manifest.recommendedAppVersion) == AppVersion.CompareResult.Older)
            {
                result.UpdateRequired = true;
                Debug.LogWarning($"[ContentRepo] App version {appVersion} is below recommended {manifest.recommendedAppVersion}.");
                try { OnUpdateRequired?.Invoke(UpdateRequiredReason.BelowRecommended); } catch (Exception ex) { Debug.LogException(ex); }
                // Soft warning — continue loading.
            }

            result.Catalogs = await LoadCatalogsAsync(manifest, force, ct);
            OnInitialized?.Invoke(result);
            return result;
        }

        public static Task<ContentRepoInitResult> RefreshAsync(
            string baseUrl, string environment, string generation,
            CancellationToken ct = default) =>
            InitializeAsync(baseUrl, environment, generation, force: false, ct);

        public static async Task<List<CatalogLoadResult>> LoadCatalogsAsync(
            ContentManifest manifest, bool force = false, CancellationToken ct = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            var platform = ResolvePlatformName();
            var results = new List<CatalogLoadResult>(manifest.contentPackages.Count);

            foreach (var entry in manifest.contentPackages)
            {
                ct.ThrowIfCancellationRequested();
                if (entry == null) continue;

                var platformEntry = entry.FindPlatform(platform);
                if (platformEntry == null)
                {
                    Debug.LogWarning($"[ContentRepo] '{entry.name}' has no catalog for platform '{platform}'. Skipping.");
                    continue;
                }

                var item = new CatalogLoadResult
                {
                    ContentPackageName = entry.name,
                    Platform = platform,
                    CatalogUrl = platformEntry.catalogUrl,
                    BuildId = platformEntry.buildId,
                };

                // Skip if same buildId already loaded and force not requested.
                var cacheKey = $"{entry.name}/{platform}";
                if (!force
                    && LoadedVersions.TryGetValue(cacheKey, out var loadedBuildId)
                    && loadedBuildId == platformEntry.buildId
                    && !string.IsNullOrEmpty(loadedBuildId))
                {
                    item.Success = true;
                    results.Add(item);
                    continue;
                }

                try
                {
                    var handle = Addressables.LoadContentCatalogAsync(platformEntry.catalogUrl, autoReleaseHandle: true);
                    var locator = await handle.Task;
                    if (handle.Status != AsyncOperationStatus.Succeeded || locator == null)
                        throw new InvalidOperationException(
                            handle.OperationException?.Message ?? "Catalog load returned null locator.");

                    item.Locator = locator;
                    item.Success = true;
                    LoadedVersions[cacheKey] = platformEntry.buildId ?? "";
                }
                catch (Exception ex)
                {
                    item.Success = false;
                    item.Error = ex.Message;
                    Debug.LogError(
                        $"[ContentRepo] Catalog load failed for '{entry.name}' ({platformEntry.catalogUrl}): {ex.Message}");
                }

                results.Add(item);
            }

            return results;
        }

        public static void ResetLoadedVersions() => LoadedVersions.Clear();

        // Maps RuntimePlatform to the Unity build target string used in CDN paths.
        private static string ResolvePlatformName()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString();
#else
            return Application.platform switch
            {
                RuntimePlatform.WindowsPlayer  => "StandaloneWindows64",
                RuntimePlatform.OSXPlayer       => "StandaloneOSX",
                RuntimePlatform.LinuxPlayer     => "StandaloneLinux64",
                RuntimePlatform.IPhonePlayer    => "iOS",
                RuntimePlatform.Android         => "Android",
                _                              => Application.platform.ToString(),
            };
#endif
        }
    }
}
