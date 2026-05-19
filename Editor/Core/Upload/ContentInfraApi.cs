using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Deploys and tears down the AWS infrastructure (cleanup Lambda + EventBridge schedule)
    /// that automatically deletes builds past their retention window. The CloudFormation template
    /// lives at Documentation/cleanup-lambda.yaml inside this package.
    /// </summary>
    public static class ContentInfraApi
    {
        private const string StackName = "content-repo-cleanup";
        private const string TemplatePath = "Packages/com.antonlicht.content-repo/Documentation/cleanup-lambda.yaml";

        public static async Task DeployCleanupLambdaAsync(UploadLogHandler log = null)
        {
            var settings = ContentUploadSettings.instance;
            RequireSettings(settings);

            var templateAbsPath = Path.GetFullPath(Path.Combine(ContentGitApi.ProjectRoot, TemplatePath));
            if (!File.Exists(templateAbsPath))
                throw new FileNotFoundException(
                    $"CloudFormation template not found at '{templateAbsPath}'.");

            var args = $"cloudformation deploy" +
                       $" --template-file \"{templateAbsPath}\"" +
                       $" --stack-name {StackName}" +
                       $" --parameter-overrides BucketName={settings.S3BucketName} Region={settings.S3Region}" +
                       $" --capabilities CAPABILITY_NAMED_IAM" +
                       $" --region {settings.S3Region}";

            log?.Invoke("[Infra] Deploying cleanup Lambda stack…");
            await RunAwsAsync(args, log);
            log?.Invoke("[Infra] Stack deployed.");
        }

        public static async Task TeardownCleanupLambdaAsync(UploadLogHandler log = null)
        {
            var settings = ContentUploadSettings.instance;
            var args = $"cloudformation delete-stack --stack-name {StackName} --region {settings.S3Region}";

            log?.Invoke("[Infra] Deleting cleanup Lambda stack…");
            await RunAwsAsync(args, log);
            log?.Invoke("[Infra] Delete initiated (stack removal is asynchronous).");
        }

        public static async Task<string> GetStackStatusAsync(UploadLogHandler log = null)
        {
            var settings = ContentUploadSettings.instance;
            try
            {
                var args = $"cloudformation describe-stacks --stack-name {StackName}" +
                           $" --query \"Stacks[0].StackStatus\" --output text --region {settings.S3Region}";
                return (await RunAwsAsync(args, log)).Trim();
            }
            catch
            {
                return "DOES_NOT_EXIST";
            }
        }

        // ── CLI ───────────────────────────────────────────────────────────────────

        public static void DeployCleanupLambdaCLI()
        {
            try
            {
                DeployCleanupLambdaAsync(UnityEngine.Debug.Log).GetAwaiter().GetResult();
                UnityEditor.EditorApplication.Exit(0);
            }
            catch (Exception ex) { UnityEngine.Debug.LogError($"[Infra CLI] {ex}"); UnityEditor.EditorApplication.Exit(1); }
        }

        public static async Task ConfigureCredentialsAsync(string accessKeyId, string secretAccessKey, string region)
        {
            await RunAwsAsync($"configure set aws_access_key_id {accessKeyId}", null);
            await RunAwsAsync($"configure set aws_secret_access_key {secretAccessKey}", null,
                              "configure set aws_secret_access_key ***");
            await RunAwsAsync($"configure set region {region}", null);
            await RunAwsAsync("configure set output json", null);
        }

        // ── Internals ─────────────────────────────────────────────────────────────

        private static void RequireSettings(ContentUploadSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.S3BucketName))
                throw new InvalidOperationException("S3 bucket name is not configured.");
            if (string.IsNullOrWhiteSpace(s.S3Region))
                throw new InvalidOperationException("S3 region is not configured.");
        }

        private static Task<string> RunAwsAsync(string args, UploadLogHandler log, string displayArgs = null)
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

            var cmd = $"aws {displayArgs ?? args}";
            UnityEngine.Debug.Log($"[ContentRepo] > {cmd}");
            log?.Invoke($"> {cmd}");

            return Task.Run(() =>
            {
                Process process;
                try { process = new Process { StartInfo = startInfo }; process.Start(); }
                catch (Win32Exception ex)
                {
                    throw new InvalidOperationException(
                        "Failed to start 'aws'. Install the AWS CLI and ensure it is on PATH.", ex);
                }

                using (process)
                {
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); log?.Invoke(e.Data); } };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); log?.Invoke(e.Data); } };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        throw new InvalidOperationException(
                            $"aws {args} failed (exit {process.ExitCode}): {stderr.ToString().Trim()}");

                    return stdout.ToString();
                }
            });
        }
    }
}
