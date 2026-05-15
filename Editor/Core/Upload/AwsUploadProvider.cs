using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ContentRepo.Editor
{
    public sealed class AwsUploadProvider : IContentUploadProvider
    {
        private static readonly HttpClient Http = new HttpClient();
        private static ContentUploadSettings S => ContentUploadSettings.instance;

        public string GetPublicUrl(string remoteKey)
        {
            var domain = S.CloudFrontDomain?.TrimEnd('/');
            if (string.IsNullOrEmpty(domain))
                throw new InvalidOperationException(
                    "CloudFront domain is not configured. Set it under Project Settings > Content Repo > Upload.");
            return $"https://{domain}/{remoteKey.TrimStart('/')}";
        }

        public async Task UploadFolderAsync(string localFolder, string remotePrefix, UploadLogHandler log = null)
        {
            RequireBucket();
            if (!Directory.Exists(localFolder))
                throw new DirectoryNotFoundException($"Local folder not found: {localFolder}");

            // No --delete: old bundles must survive for players on previous builds.
            var s3Uri = $"s3://{S.S3BucketName}/{remotePrefix.TrimEnd('/')}/" ;
            await RunAwsAsync($"s3 sync \"{localFolder}\" \"{s3Uri}\" --region {S.S3Region}", log);
        }

        public async Task UploadFileAsync(string localFilePath, string remoteKey, UploadLogHandler log = null)
        {
            RequireBucket();
            if (!File.Exists(localFilePath))
                throw new FileNotFoundException("File not found.", localFilePath);

            var s3Uri = $"s3://{S.S3BucketName}/{remoteKey.TrimStart('/')}";
            await RunAwsAsync($"s3 cp \"{localFilePath}\" \"{s3Uri}\" --region {S.S3Region}", log);
        }

        public async Task<string> DownloadTextAsync(string remoteKey)
        {
            var url = GetPublicUrl(remoteKey);
            try
            {
                using var resp = await Http.GetAsync(url);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException) { return null; }
        }

        public async Task InvalidatePathAsync(string path, UploadLogHandler log = null)
        {
            if (string.IsNullOrWhiteSpace(S.CloudFrontDistributionId))
                throw new InvalidOperationException("CloudFront distribution ID is not configured.");
            var safePath = path.StartsWith("/") ? path : "/" + path;
            await RunAwsAsync(
                $"cloudfront create-invalidation --distribution-id {S.CloudFrontDistributionId} --paths \"{safePath}\"",
                log);
        }

        public async Task<bool> ValidateConfigAsync(UploadLogHandler log = null)
        {
            if (string.IsNullOrWhiteSpace(S.S3BucketName))
            {
                log?.Invoke("S3 bucket name is empty.");
                return false;
            }
            try
            {
                await RunAwsAsync($"s3 ls s3://{S.S3BucketName} --region {S.S3Region}", log);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke(ex.Message);
                Debug.LogWarning($"AWS validation failed: {ex.Message}");
                return false;
            }
        }

        private static void RequireBucket()
        {
            if (string.IsNullOrWhiteSpace(S.S3BucketName))
                throw new InvalidOperationException(
                    "S3 bucket name is not configured. Set it under Project Settings > Content Repo > Upload.");
        }

        private static Task RunAwsAsync(string args, UploadLogHandler log)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "aws",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var cmd = $"aws {args}";
            Debug.Log($"[ContentRepo] > {cmd}");
            log?.Invoke($"> {cmd}");

            return Task.Run(() =>
            {
                Process process;
                try
                {
                    process = new Process { StartInfo = startInfo };
                    process.Start();
                }
                catch (Win32Exception ex)
                {
                    throw new InvalidOperationException(
                        "Failed to start 'aws'. Install the AWS CLI and ensure it is on PATH. See Documentation/Setup-AWS.md.", ex);
                }

                using (process)
                {
                    var stderr = new StringBuilder();
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        stderr.AppendLine(e.Data);
                        log?.Invoke(e.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        var err = stderr.ToString().Trim();
                        throw new InvalidOperationException($"aws {args} failed (exit {process.ExitCode}): {err}");
                    }
                }
            });
        }
    }
}
