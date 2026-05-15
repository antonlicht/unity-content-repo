using UnityEngine;

namespace ContentRepo
{
    [CreateAssetMenu(menuName = "Content Repo/Runtime Settings", fileName = "ContentRepoRuntimeSettings")]
    public sealed class ContentRepoRuntimeSettings : ScriptableObject
    {
        [Tooltip("CDN base URL, e.g. https://xxxx.cloudfront.net (no trailing slash needed).")]
        [SerializeField] private string baseUrl = "";

        [Tooltip("Environment to load — must match what was published (staging / production / …).")]
        [SerializeField] private string environment = "production";

        [Tooltip("Generation string that matches the one baked into the editor at build time, e.g. gen/1.")]
        [SerializeField] private string generation = "gen/1";

        [Tooltip("If true, ContentRepoRuntime.InitializeAsync is called automatically before the first scene loads.")]
        [SerializeField] private bool initializeOnLoad = false;

        public string BaseUrl => baseUrl;
        public string Environment => environment;
        public string Generation => generation;
        public bool InitializeOnLoad => initializeOnLoad;

        private const string ResourcePath = "ContentRepoRuntimeSettings";
        private static ContentRepoRuntimeSettings _cached;
        private static bool _resolved;

        public static ContentRepoRuntimeSettings Load()
        {
            if (_resolved) return _cached;
            _resolved = true;
            _cached = Resources.Load<ContentRepoRuntimeSettings>(ResourcePath);
            return _cached;
        }
    }

    internal static class ContentRepoBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static async void OnBeforeSceneLoad()
        {
            var settings = ContentRepoRuntimeSettings.Load();
            if (settings == null || !settings.InitializeOnLoad) return;
            if (string.IsNullOrWhiteSpace(settings.BaseUrl)
                || string.IsNullOrWhiteSpace(settings.Environment)
                || string.IsNullOrWhiteSpace(settings.Generation))
            {
                Debug.LogWarning("[ContentRepo] InitializeOnLoad is enabled but BaseUrl, Environment, or Generation is empty. Skipping.");
                return;
            }
            try
            {
                await ContentRepoRuntime.InitializeAsync(settings.BaseUrl, settings.Environment, settings.Generation);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ContentRepo] Auto-initialize failed: {ex.Message}");
            }
        }
    }
}
