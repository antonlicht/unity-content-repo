using System;
using System.Threading.Tasks;

namespace ContentRepo.Editor
{
    public delegate void UploadLogHandler(string line);

    /// <summary>
    /// Transport abstraction between the pipeline orchestration and the underlying CDN/storage.
    /// All path construction lives in <see cref="ContentUploadApi"/>; the provider handles only
    /// raw I/O so swapping storage backends (Azure Blob, GCS, …) requires only a new implementation.
    /// </summary>
    public interface IContentUploadProvider
    {
        /// <summary>Upload every file under <paramref name="localFolder"/> to <paramref name="remotePrefix"/>.</summary>
        Task UploadFolderAsync(string localFolder, string remotePrefix, UploadLogHandler log = null);

        /// <summary>Upload a single local file to an exact remote key.</summary>
        Task UploadFileAsync(string localFilePath, string remoteKey, UploadLogHandler log = null);

        /// <summary>Download the text content of a remote key, or null when the object does not exist.</summary>
        Task<string> DownloadTextAsync(string remoteKey);

        /// <summary>Issue a CDN cache invalidation for <paramref name="path"/> (e.g. "/gen/2/production/manifest.json").</summary>
        Task InvalidatePathAsync(string path, UploadLogHandler log = null);

        /// <summary>Return the full public HTTPS URL for a remote key.</summary>
        string GetPublicUrl(string remoteKey);

        /// <summary>Verify that credentials and basic bucket access work. Returns true on success.</summary>
        Task<bool> ValidateConfigAsync(UploadLogHandler log = null);
    }

    public static class ContentUploadProviderFactory
    {
        public static IContentUploadProvider Resolve()
        {
            var t = ContentUploadSettings.instance.ProviderType;
            return t switch
            {
                UploadProviderType.AWS_S3_CloudFront => new AwsUploadProvider(),
                _ => throw new NotSupportedException($"Upload provider {t} is not registered."),
            };
        }
    }
}
