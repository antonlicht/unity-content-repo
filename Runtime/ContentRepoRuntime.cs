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
        // Kept alive so the catalog stays registered with Addressables.
        // Call Addressables.Release(result.Handle) when this content package is unloaded.
        public AsyncOperationHandle<IResourceLocator> Handle;
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
                    // Before giving up, check whether a local dev override covers this package.
                    // This handles the case where a package is listed in the CDN manifest (e.g.
                    // for another platform) but has no entry for the current platform yet — a
                    // local AssetDatabase or LocalBundles override should still work.
                    if (ContentLocalDevOverrides.TryGet(entry.name, out var localDevFallback))
                    {
                        var fallbackItem = new CatalogLoadResult
                        {
                            ContentPackageName = entry.name,
                            Platform           = platform,
                        };
                        switch (localDevFallback.Mode)
                        {
                            case LocalDevMode.AssetDatabase:
                                Debug.Log($"[ContentRepo] LOCAL DEV (AssetDatabase, no platform entry): skipping catalog for '{entry.name}'.");
                                fallbackItem.Success = true;
                                results.Add(fallbackItem);
                                break;

                            case LocalDevMode.LocalBundles:
                                fallbackItem.CatalogUrl = localDevFallback.LocalCatalogUrl;
                                Debug.Log($"[ContentRepo] LOCAL DEV (LocalBundles, no platform entry): catalog → '{fallbackItem.CatalogUrl}' for '{entry.name}'.");
                                var fbHandle = new AsyncOperationHandle<IResourceLocator>();
                                try
                                {
                                    fbHandle = Addressables.LoadContentCatalogAsync(fallbackItem.CatalogUrl, autoReleaseHandle: false);
                                    IResourceLocator fbLocator = null;
                                    try { fbLocator = await fbHandle.Task; } catch { /* see fbHandle.OperationException */ }

                                    if (fbHandle.Status != AsyncOperationStatus.Succeeded || fbLocator == null)
                                    {
                                        var reason = fbHandle.OperationException?.Message
                                            ?? (fbLocator == null ? "Catalog load returned null locator." : "Operation did not succeed.");
                                        Addressables.Release(fbHandle);
                                        throw new InvalidOperationException(reason);
                                    }
                                    fallbackItem.Handle  = fbHandle;
                                    fallbackItem.Locator = fbLocator;
                                    fallbackItem.Success = true;
                                }
                                catch (Exception ex)
                                {
                                    fallbackItem.Success = false;
                                    fallbackItem.Error   = ex.Message;
                                    Debug.LogError($"[ContentRepo] Catalog load failed for local-override '{entry.name}' ({fallbackItem.CatalogUrl}): {ex.Message}");
                                }
                                results.Add(fallbackItem);
                                break;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"[ContentRepo] '{entry.name}' has no catalog for platform '{platform}'. Skipping.");
                    }
                    continue;
                }

                var item = new CatalogLoadResult
                {
                    ContentPackageName = entry.name,
                    Platform = platform,
                    CatalogUrl = platformEntry.catalogUrl,
                    BuildId = platformEntry.buildId,
                };

                // ── Local dev override ────────────────────────────────────────────
                // Checked-out packages registered via ContentLocalDevApi (Editor) bypass
                // CDN catalog loading entirely (AssetDatabase mode) or redirect to a local
                // file:// catalog (LocalBundles mode). Packages without an override continue
                // to use the CDN URL, providing automatic fallback for content not checked out.
                if (ContentLocalDevOverrides.TryGet(entry.name, out var localDev))
                {
                    switch (localDev.Mode)
                    {
                        case LocalDevMode.AssetDatabase:
                            // Fast Mode: Addressables' AssetDatabase provider resolves assets
                            // directly — no catalog fetch needed.
                            Debug.Log($"[ContentRepo] '{entry.name}' resolved from LOCAL AssetDatabase (Fast Mode) — not downloaded.");
                            item.Success = true;
                            results.Add(item);
                            continue;

                        case LocalDevMode.LocalBundles:
                            // Replace CDN URL with the local file:// catalog written by ContentLocalDevApi.
                            item.CatalogUrl = localDev.LocalCatalogUrl;
                            Debug.Log($"[ContentRepo] LOCAL DEV (LocalBundles): catalog → '{item.CatalogUrl}' for '{entry.name}'.");
                            break;
                    }
                }
                // ─────────────────────────────────────────────────────────────────

                // Skip if same buildId already loaded and force not requested.
                var cacheKey = $"{entry.name}/{platform}";
                if (!force
                    && LoadedVersions.TryGetValue(cacheKey, out var loadedBuildId)
                    && loadedBuildId == platformEntry.buildId
                    && !string.IsNullOrEmpty(loadedBuildId))
                {
                    item.Success = true;
                    Debug.Log($"[ContentRepo] '{entry.name}' already resolved this session (build {loadedBuildId} unchanged) — reusing.");
                    results.Add(item);
                    continue;
                }

                var handle = new AsyncOperationHandle<IResourceLocator>();
                try
                {
                    handle = Addressables.LoadContentCatalogAsync(item.CatalogUrl, autoReleaseHandle: false);

                    // Swallow the task exception — the real error is on handle.OperationException.
                    IResourceLocator locator = null;
                    try { locator = await handle.Task; } catch { /* see handle.OperationException */ }

                    if (handle.Status != AsyncOperationStatus.Succeeded || locator == null)
                    {
                        var reason = handle.OperationException?.Message
                            ?? (locator == null ? "Catalog load returned null locator." : "Operation did not succeed.");
                        Addressables.Release(handle);
                        throw new InvalidOperationException(reason);
                    }

                    // Keep the handle alive — releasing it unregisters the locator from Addressables.
                    // The caller must call Addressables.Release(result.Handle) when unloading this package.
                    item.Handle  = handle;
                    item.Locator = locator;
                    item.Success = true;
                    LoadedVersions[cacheKey] = platformEntry.buildId ?? "";

                    var fromLocalBundles = ContentLocalDevOverrides.TryGet(entry.name, out var srcOverride)
                                           && srcOverride.Mode == LocalDevMode.LocalBundles;
                    Debug.Log(fromLocalBundles
                        ? $"[ContentRepo] '{entry.name}' resolved from LOCAL BUNDLES (build {platformEntry.buildId}) — catalog: {item.CatalogUrl}"
                        : $"[ContentRepo] '{entry.name}' resolved from the ONLINE SERVER (build {platformEntry.buildId}) — catalog: {item.CatalogUrl}");
                }
                catch (Exception ex)
                {
                    item.Success = false;
                    item.Error   = ex.Message;
                    Debug.LogError(
                        $"[ContentRepo] Catalog load failed for '{entry.name}' ({platformEntry.catalogUrl}): {ex.Message}");
                }

                results.Add(item);
            }

            // ── Local-only packages (not in CDN manifest) ────────────────────────
            // A brand-new content package hasn't been built/deployed yet, so it has
            // no entry in the CDN manifest. If a developer registered it via
            // ContentLocalDevApi (Fast Mode or LocalBundles) it still needs to be
            // processed so its assets are available at runtime.
            foreach (var kv in ContentLocalDevOverrides.All)
            {
                ct.ThrowIfCancellationRequested();
                var packageName = kv.Key;

                // Already processed above via the CDN manifest loop — skip.
                if (manifest.Find(packageName) != null) continue;

                var localDev = kv.Value;
                var item = new CatalogLoadResult
                {
                    ContentPackageName = packageName,
                    Platform = platform,
                };

                switch (localDev.Mode)
                {
                    case LocalDevMode.AssetDatabase:
                        Debug.Log($"[ContentRepo] LOCAL DEV (AssetDatabase, manifest-absent): skipping catalog for '{packageName}'.");
                        item.Success = true;
                        results.Add(item);
                        break;

                    case LocalDevMode.LocalBundles:
                        item.CatalogUrl = localDev.LocalCatalogUrl;
                        Debug.Log($"[ContentRepo] LOCAL DEV (LocalBundles, manifest-absent): catalog → '{item.CatalogUrl}' for '{packageName}'.");
                        var lbHandle = new AsyncOperationHandle<IResourceLocator>();
                        try
                        {
                            lbHandle = Addressables.LoadContentCatalogAsync(item.CatalogUrl, autoReleaseHandle: false);
                            IResourceLocator lbLocator = null;
                            try { lbLocator = await lbHandle.Task; } catch { /* see lbHandle.OperationException */ }

                            if (lbHandle.Status != AsyncOperationStatus.Succeeded || lbLocator == null)
                            {
                                var reason = lbHandle.OperationException?.Message
                                    ?? (lbLocator == null ? "Catalog load returned null locator." : "Operation did not succeed.");
                                Addressables.Release(lbHandle);
                                throw new InvalidOperationException(reason);
                            }

                            item.Handle  = lbHandle;
                            item.Locator = lbLocator;
                            item.Success = true;
                        }
                        catch (Exception ex)
                        {
                            item.Success = false;
                            item.Error   = ex.Message;
                            Debug.LogError($"[ContentRepo] Catalog load failed for local-only '{packageName}' ({item.CatalogUrl}): {ex.Message}");
                        }
                        results.Add(item);
                        break;
                }
            }
            // ─────────────────────────────────────────────────────────────────────

            return results;
        }

        public static void ResetLoadedVersions() => LoadedVersions.Clear();

        /// <summary>
        /// Downloads a content package's asset bundles ahead of use — they otherwise download lazily on
        /// first access. Use this to drive a "downloading chapter…" spinner before entering it: await
        /// with an <see cref="IProgress{T}"/>, then the subsequent Naninovel load is served from cache.
        /// Requires the package's catalog to already be registered (via <see cref="InitializeAsync"/>).
        /// Returns the number of bytes that had to be downloaded (0 = already cached / nothing to fetch).
        /// Keyed on the package-name label the build stamps onto every entry.
        /// </summary>
        public static async Task<long> PreloadPackageAsync(
            string packageName, IProgress<float> progress = null, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(packageName)) throw new ArgumentException("packageName required.", nameof(packageName));

            long bytes;
            var sizeHandle = Addressables.GetDownloadSizeAsync((object)packageName);
            try { bytes = await sizeHandle.Task; }
            finally { if (sizeHandle.IsValid()) Addressables.Release(sizeHandle); }

            if (bytes <= 0)
            {
                Debug.Log($"[ContentRepo] '{packageName}' already cached — no download needed.");
                progress?.Report(1f);
                return 0;
            }

            Debug.Log($"[ContentRepo] Preloading '{packageName}' — {bytes / 1024f / 1024f:0.0} MB to download from the ONLINE server.");
            var dl = Addressables.DownloadDependenciesAsync((object)packageName, autoReleaseHandle: false);
            try
            {
                while (!dl.IsDone)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(dl.PercentComplete);
                    await Task.Yield();
                }
                if (dl.Status != AsyncOperationStatus.Succeeded)
                    throw new Exception(dl.OperationException?.Message ?? "download did not succeed");
            }
            finally { if (dl.IsValid()) Addressables.Release(dl); }

            progress?.Report(1f);
            Debug.Log($"[ContentRepo] '{packageName}' download complete ({bytes / 1024f / 1024f:0.0} MB).");
            return bytes;
        }

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
