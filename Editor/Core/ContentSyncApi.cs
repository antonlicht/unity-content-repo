using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace ContentRepo.Editor
{
    public enum ContentSyncState { Unknown, NotDeployed, InSync, Behind, NotInRepository }

    public sealed class ContentCommit
    {
        public string Sha;
        public string Date;
        public string Author;
        public string Subject;

        public override string ToString() => $"{Sha}  {Date}  {Author}  {Subject}";
    }

    public sealed class ContentSourceState
    {
        public string Sha;
        public string Tree;
        public bool Dirty;
    }

    public sealed class ContentDeployStatus
    {
        public ContentSyncState State;
        public bool Verified;
        public string BuildId;
        public string SourceSha;
        public string BuiltAt;
        public bool BuiltFromLocalChanges;
        public List<ContentCommit> NewerCommits = new();

        public bool NeedsAttention =>
            State == ContentSyncState.Behind || State == ContentSyncState.NotInRepository || State == ContentSyncState.Unknown;
    }

    /// <summary>
    /// Compares what is deployed with what is in the content repository. A package's source is identified by a
    /// fingerprint of its folder tree and its <c>_groups</c> files, so a build made from uncommitted files reads
    /// as in sync as soon as exactly those files are committed and pushed. Only commit and tree objects are
    /// read, so nothing is downloaded in a partial clone and packages need not be checked out.
    /// </summary>
    public static class ContentSyncApi
    {
        private const int MaxListedCommits = 8;

        public static string RemoteRef => $"origin/{ContentRepoSettings.instance.Branch}";

        public static async Task<bool> FetchAsync()
        {
            try { await Git("fetch origin"); return true; }
            catch { return false; }
        }

        public static async Task<string> GetTreeAtAsync(string revision, string package)
        {
            string folderTree;
            try { folderTree = (await Git($"rev-parse --verify --quiet {Quote($"{revision}:{package}")}")).Trim(); }
            catch { return null; }
            if (folderTree.Length == 0) return null;

            var lines = new List<string> { $"tree {folderTree}" };
            try
            {
                var listing = await Git($"-c core.quotepath=off ls-tree {Quote(revision)} {Quote(ContentGitApi.GroupsFolderName + "/")}");
                foreach (var line in SplitLines(listing))
                {
                    var tab = line.IndexOf('\t');
                    if (tab < 0) continue;
                    var name = Path.GetFileName(line.Substring(tab + 1).Trim().Trim('"'));
                    var fields = line.Substring(0, tab).Split(' ');
                    if (fields.Length >= 3 && ContentGitApi.IsGroupFileOf(package, name)) lines.Add($"{name} {fields[2]}");
                }
            }
            catch { }
            return Fingerprint(lines);
        }

        /// <summary>The fingerprint the package would have if its files on disk were committed right now.</summary>
        public static async Task<ContentSourceState> GetWorkingSourceAsync(string package)
        {
            var state = new ContentSourceState();
            try { state.Sha = (await Git("rev-parse --short HEAD")).Trim(); }
            catch { return state; }

            var committed = await GetTreeAtAsync("HEAD", package);
            var temporaryIndex = Path.Combine(Path.GetTempPath(), $"content-repo-index-{Guid.NewGuid():N}");
            try
            {
                var gitDir = (await Git("rev-parse --absolute-git-dir")).Trim();
                var index = Path.Combine(gitDir, "index");
                var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temporaryIndex };
                if (File.Exists(index)) File.Copy(index, temporaryIndex, true);
                else await Git("read-tree HEAD", environment);

                await Git($"add --sparse -A -- {Quote(package)}", environment);
                var folderTree = (await Git($"write-tree --missing-ok --prefix={Quote(package + "/")}", environment)).Trim();
                var lines = new List<string> { $"tree {folderTree}" };

                var groupFiles = ContentGitApi.GetGroupFileNamesOnDisk(package);
                if (groupFiles.Count > 0)
                {
                    var paths = string.Join(" ", groupFiles.Select(name => Quote($"{ContentGitApi.GroupsFolderName}/{name}")));
                    var hashes = SplitLines(await Git($"hash-object -- {paths}"));
                    if (hashes.Count != groupFiles.Count)
                        throw new InvalidOperationException("git hash-object returned an unexpected number of hashes.");
                    for (var i = 0; i < groupFiles.Count; i++) lines.Add($"{groupFiles[i]} {hashes[i]}");
                }

                state.Tree = Fingerprint(lines);
                state.Dirty = state.Tree != committed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ContentRepo] Could not fingerprint the files of '{package}', treating the build as made from local changes: {ex.Message}");
                state.Tree = null;
                state.Dirty = true;
            }
            finally
            {
                TryDelete(temporaryIndex);
                TryDelete(temporaryIndex + ".lock");
            }
            return state;
        }

        public static async Task<List<ContentCommit>> GetCommitsAsync(string from, string to, string package)
        {
            var commits = new List<ContentCommit>();
            var output = await Git($"-c core.quotepath=off log --format=%h%x09%ad%x09%an%x09%s --date=short {Quote($"{from}..{to}")} -- {PathSpec(package)}");
            foreach (var line in SplitLines(output))
            {
                var fields = line.Split(new[] { '\t' }, 4);
                if (fields.Length < 4) continue;
                commits.Add(new ContentCommit { Sha = fields[0], Date = fields[1], Author = fields[2], Subject = fields[3] });
            }
            return commits;
        }

        /// <summary>Commits on the remote that touch the package and are not in the local branch yet.</summary>
        public static async Task<List<ContentCommit>> GetUnpulledCommitsAsync(string package)
        {
            try { return await GetCommitsAsync("HEAD", RemoteRef, package); }
            catch { return new List<ContentCommit>(); }
        }

        public static async Task<ContentDeployStatus> GetDeployStatusAsync(string package, ContentManifestEntry entry, string platform)
        {
            var status = new ContentDeployStatus();
            var deployed = entry?.FindPlatform(platform);
            if (deployed == null)
            {
                status.State = ContentSyncState.NotDeployed;
                return status;
            }

            status.BuildId = deployed.buildId;
            status.BuiltAt = deployed.builtAt;
            status.SourceSha = string.IsNullOrEmpty(deployed.sourceSha) ? entry.gitSha : deployed.sourceSha;
            status.BuiltFromLocalChanges = deployed.sourceDirty;

            var tracked = !string.IsNullOrEmpty(deployed.sourceTree);
            var repositoryTree = await GetTreeAtAsync(RemoteRef, package);
            if (tracked && deployed.sourceTree == repositoryTree)
            {
                status.State = ContentSyncState.InSync;
                status.Verified = true;
                return status;
            }

            var sourceOnRemote = !string.IsNullOrEmpty(status.SourceSha) && await IsAncestorAsync(status.SourceSha, RemoteRef);
            if (sourceOnRemote)
            {
                try { status.NewerCommits = await GetCommitsAsync(status.SourceSha, RemoteRef, package); }
                catch { sourceOnRemote = false; }
            }

            if (status.NewerCommits.Count > 0) status.State = ContentSyncState.Behind;
            else if (tracked) status.State = ContentSyncState.NotInRepository;
            else status.State = sourceOnRemote ? ContentSyncState.InSync : ContentSyncState.Unknown;
            return status;
        }

        public static string Describe(string environment, ContentDeployStatus status)
        {
            if (status == null || status.State == ContentSyncState.NotDeployed) return $"Not on {environment}.";

            var text = new StringBuilder($"Live on {environment}: build {status.BuildId}");
            var built = new List<string>();
            if (DateTime.TryParse(status.BuiltAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var builtAt))
                built.Add($"{builtAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            if (!string.IsNullOrEmpty(status.SourceSha))
                built.Add($"from commit {status.SourceSha}{(status.BuiltFromLocalChanges ? " plus local changes" : "")}");
            if (built.Count > 0) text.Append("\nBuilt ").Append(string.Join(" ", built));
            return text.Append("\n\n").Append(DescribeState(status)).ToString();
        }

        public static string DescribeState(ContentDeployStatus status)
        {
            switch (status.State)
            {
                case ContentSyncState.InSync:
                    return status.Verified
                        ? "Matches the content repository."
                        : "No newer commits in the content repository. This build was made before source tracking, so local changes in it cannot be ruled out.";
                case ContentSyncState.Behind:
                    var behind = new StringBuilder(status.NewerCommits.Count == 1
                        ? "1 newer commit in the content repository is not in this build:"
                        : $"{status.NewerCommits.Count} newer commits in the content repository are not in this build:");
                    foreach (var commit in status.NewerCommits.Take(MaxListedCommits)) behind.Append("\n  ").Append(commit);
                    if (status.NewerCommits.Count > MaxListedCommits)
                        behind.Append($"\n  and {status.NewerCommits.Count - MaxListedCommits} more");
                    if (status.BuiltFromLocalChanges) behind.Append("\nIt was also built with local changes that were not committed.");
                    return behind.Append("\nPull, build and upload to bring it up to date.").ToString();
                case ContentSyncState.NotInRepository:
                    return "This build contains changes that are not in the content repository (uncommitted or not pushed). " +
                           "Commit and push them, or the next build from the repository will drop them.";
                case ContentSyncState.Unknown:
                    return string.IsNullOrEmpty(status.SourceSha)
                        ? "The build does not say which commit it was made from, so it cannot be compared with the content repository."
                        : $"The build was made from commit {status.SourceSha}, which is not on {RemoteRef}, so it cannot be compared with the content repository.";
                default:
                    return "";
            }
        }

        private static async Task<bool> IsAncestorAsync(string ancestor, string descendant)
        {
            try { await Git($"merge-base --is-ancestor {Quote(ancestor)} {Quote(descendant)}"); return true; }
            catch { return false; }
        }

        private static string PathSpec(string package)
        {
            var groups = ContentGitApi.GroupsFolderName;
            return string.Join(" ", new[]
            {
                package,
                $"{groups}/{package}.asset",
                $"{groups}/{package}.asset.meta",
                $":(glob){groups}/{package}_*Schema.asset",
                $":(glob){groups}/{package}_*Schema.asset.meta",
            }.Select(Quote));
        }

        private static string Fingerprint(List<string> lines)
        {
            lines.Sort(StringComparer.Ordinal);
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        private static Task<string> Git(string args, IReadOnlyDictionary<string, string> environment = null) =>
            ContentGitApi.RunGitCommandAsync(args, ContentGitApi.ContentAbsolutePath, true, environment);

        private static List<string> SplitLines(string output) =>
            output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
