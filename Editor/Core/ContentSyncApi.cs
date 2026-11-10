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
    public enum ContentSyncState { Unknown, NotDeployed, InSync, Behind, NotInRepository, Removed, RepositoryLostChanges }

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

    /// <summary>A merge commit that left out changes to a package that a normal merge of its parents keeps.</summary>
    public sealed class ContentMergeLoss
    {
        public ContentCommit Merge;
        public int DroppedCount;
        public List<string> Examples = new();
        internal Dictionary<string, string> Kept = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Expected = new(StringComparer.Ordinal);
    }

    public sealed class ContentDeployStatus
    {
        public string Package;
        public ContentSyncState State;
        public bool Verified;
        public string BuildId;
        public string SourceSha;
        public string BuiltAt;
        public bool BuiltFromLocalChanges;
        public string MatchedCommit;
        public string RemovedIn;
        public string Detail;
        public ContentMergeLoss Loss;
        public List<ContentCommit> NewerCommits = new();

        public bool NeedsAttention => State == ContentSyncState.Behind || State == ContentSyncState.NotInRepository
            || State == ContentSyncState.Unknown || State == ContentSyncState.Removed || State == ContentSyncState.RepositoryLostChanges;
    }

    /// <summary>
    /// Compares what is deployed with what is in the content repository. A package's source is identified by a
    /// fingerprint of its folder tree and its <c>_groups</c> files, so a build made from uncommitted files reads
    /// as in sync as soon as exactly those files are committed and pushed. Only commit and tree objects are
    /// read, so nothing is downloaded in a partial clone and packages need not be checked out. Results that
    /// depend only on commit ids never change and are cached for the editor session.
    /// </summary>
    public static class ContentSyncApi
    {
        private const int MaxListedCommits = 8;
        private const int MaxListedPaths = 5;
        private const int MaxCommitsSearched = 12;
        private const int MaxMergesChecked = 10;

        private static readonly Dictionary<string, string> ResolvedCommits = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<KeyValuePair<string, string>>> GroupListings = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Fingerprints = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> Ancestry = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<ContentCommit>> CommitRanges = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> TreeMatches = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, ContentMergeLoss> RangeLosses = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, ContentMergeLoss> MergeLosses = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, string>> PackageListings = new(StringComparer.Ordinal);

        public static string RemoteRef => $"origin/{ContentRepoSettings.instance.Branch}";

        public static async Task<bool> FetchAsync()
        {
            try { await Git("fetch origin"); return true; }
            catch { return false; }
        }

        /// <summary>The full id of origin/&lt;branch&gt;, or null when it does not exist locally.</summary>
        public static Task<string> ResolveRemoteAsync() => ResolveCommitAsync(RemoteRef);

        public static async Task<string> GetTreeAtAsync(string revision, string package)
        {
            var commit = await ResolveCommitAsync(revision);
            return commit == null ? null : await FingerprintAtAsync(commit, package);
        }

        /// <summary>The fingerprint the package would have if its files on disk were committed right now.</summary>
        public static async Task<ContentSourceState> GetWorkingSourceAsync(string package)
        {
            var state = new ContentSourceState();
            try { state.Sha = (await Git("rev-parse --short HEAD")).Trim(); }
            catch
            {
                state.Dirty = true;
                return state;
            }

            var committed = await GetTreeAtAsync("HEAD", package);
            var temporaryIndex = Path.Combine(Path.GetTempPath(), $"content-repo-index-{Guid.NewGuid():N}");
            try
            {
                var gitDir = (await Git("rev-parse --absolute-git-dir")).Trim();
                var index = Path.Combine(gitDir, "index");
                var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temporaryIndex };
                if (File.Exists(index)) File.Copy(index, temporaryIndex, true);
                else await Git("read-tree HEAD", environment);

                var groupPaths = ContentGitApi.GetGroupFileNamesOnDisk(package)
                    .Select(name => $"{ContentGitApi.GroupsFolderName}/{name}")
                    .ToList();
                var pathspec = string.Join(" ", groupPaths.Prepend(package).Select(Literal));
                await Git($"add --sparse -A -- {pathspec}", environment);

                var lines = new List<string> { $"tree {(await Git($"write-tree --missing-ok --prefix={Quote(package + "/")}", environment)).Trim()}" };
                if (groupPaths.Count > 0)
                {
                    var staged = await Git($"-c core.quotepath=off ls-files -s -- {string.Join(" ", groupPaths.Select(Literal))}", environment);
                    var entries = ParseIndexEntries(staged);
                    if (entries.Count != groupPaths.Count)
                        throw new InvalidOperationException($"expected {groupPaths.Count} staged group files, found {entries.Count}.");
                    lines.AddRange(entries.Select(entry => $"{entry.Key} {entry.Value}"));
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
            var fromCommit = await ResolveCommitAsync(from);
            var toCommit = await ResolveCommitAsync(to);
            if (fromCommit == null || toCommit == null) throw new InvalidOperationException($"Cannot resolve {(fromCommit == null ? from : to)}.");

            var key = $"{fromCommit}\n{toCommit}\n{package}";
            if (CommitRanges.TryGetValue(key, out var cached)) return cached;

            var commits = new List<ContentCommit>();
            var output = await Git($"-c core.quotepath=off log --format=%h%x09%ad%x09%an%x09%s --date=short {fromCommit}..{toCommit} -- {await PathSpecAsync(package, fromCommit, toCommit)}");
            foreach (var line in SplitLines(output))
            {
                var commit = ParseCommit(line);
                if (commit != null) commits.Add(commit);
            }
            CommitRanges[key] = commits;
            return commits;
        }

        /// <summary>Commits on the remote that touch the package and are not in the local branch yet.</summary>
        public static async Task<List<ContentCommit>> GetUnpulledCommitsAsync(string package, string remote = null)
        {
            try { return await GetCommitsAsync("HEAD", remote ?? RemoteRef, package); }
            catch { return new List<ContentCommit>(); }
        }

        /// <summary>
        /// The newest merge between <paramref name="from"/> and <paramref name="to"/> that left out changes to the
        /// package which a normal merge of its parents keeps (one side's changes dropped in favour of the other's),
        /// counting only files that are still in the state the merge left them in at <paramref name="to"/>.
        /// Conflicted files are not counted: resolving them either way is a decision, not a loss.
        /// </summary>
        public static async Task<ContentMergeLoss> FindMergeLossAsync(string from, string to, string package)
        {
            var fromCommit = await ResolveCommitAsync(from);
            return fromCommit == null ? null : await FindLossAsync(fromCommit, to, package);
        }

        /// <summary>
        /// A recent merge on the remote that left out changes to the package which are still missing there, so a
        /// build from the repository lacks them. Only the newest merges that touch the package are checked.
        /// </summary>
        public static Task<ContentMergeLoss> FindUnrepairedLossAsync(string package, string remote = null) =>
            FindLossAsync(null, remote ?? RemoteRef, package);

        private static async Task<ContentMergeLoss> FindLossAsync(string fromCommit, string to, string package)
        {
            var toCommit = await ResolveCommitAsync(to);
            if (toCommit == null || fromCommit == toCommit) return null;

            var key = $"{fromCommit}\n{toCommit}\n{package}";
            if (RangeLosses.TryGetValue(key, out var cached)) return cached;

            ContentMergeLoss loss = null;
            try
            {
                var range = fromCommit == null ? toCommit : $"{fromCommit}..{toCommit}";
                var pathspec = fromCommit == null ? await PathSpecAsync(package, toCommit) : await PathSpecAsync(package, fromCommit, toCommit);
                var merges = SplitLines(await Git($"log --merges --full-history -n {MaxMergesChecked} --format=%H%x20%P {range} -- {pathspec}"));
                merges.Reverse();

                // Oldest first: a later merge that rejects exactly the versions an earlier lossy merge kept is restoring
                // the lost work, not dropping something, so those files do not count against it.
                var lostVersions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                var flagged = new List<(ContentMergeLoss Merge, List<string> Paths)>();
                foreach (var line in merges)
                {
                    var ids = line.Split(' ');
                    if (ids.Length != 3) continue;
                    var dropped = await MergeLossAsync(ids[0], ids[1], ids[2], package);
                    if (dropped == null) continue;

                    var paths = dropped.Kept.Keys
                        .Where(path => !(lostVersions.TryGetValue(path, out var lost) && lost.Contains(dropped.Expected[path] ?? "")))
                        .ToList();
                    foreach (var path in paths)
                    {
                        if (!lostVersions.TryGetValue(path, out var lost)) lostVersions[path] = lost = new HashSet<string>(StringComparer.Ordinal);
                        lost.Add(dropped.Kept[path] ?? "");
                    }
                    if (paths.Count > 0) flagged.Add((dropped, paths));
                }

                Dictionary<string, string> current = null;
                for (var i = flagged.Count - 1; i >= 0 && loss == null; i--)
                {
                    current ??= await PackageEntriesAsync(toCommit, package);
                    var (dropped, paths) = flagged[i];
                    var missing = paths
                        .Where(path => current.TryGetValue(path, out var now) ? now == dropped.Kept[path] : dropped.Kept[path] == null)
                        .ToList();
                    if (missing.Count > 0)
                        loss = new ContentMergeLoss { Merge = dropped.Merge, DroppedCount = missing.Count, Examples = PickExamples(missing) };
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ContentRepo] Could not check the merges of '{package}' for dropped changes: {ex.Message}");
                return null;
            }
            RangeLosses[key] = loss;
            return loss;
        }

        public static async Task<ContentDeployStatus> GetDeployStatusAsync(string package, ContentManifestEntry entry, string platform, string remote = null)
        {
            var status = new ContentDeployStatus { Package = package };
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

            remote ??= await ResolveRemoteAsync();
            if (remote == null)
            {
                status.State = ContentSyncState.Unknown;
                status.Detail = $"{RemoteRef} was not found in the local clone, so the build cannot be compared with the content repository. Check the branch under Project Settings > Content Repo, then refresh.";
                return status;
            }

            var repositoryTree = await FingerprintAtAsync(remote, package);
            if (repositoryTree == null)
            {
                try { status.RemovedIn = ParseCommit(SplitLines(await Git($"-c core.quotepath=off log -1 --format=%h%x09%ad%x09%an%x09%s --date=short {remote} -- {Literal(package)}")).FirstOrDefault())?.ToString(); }
                catch { }
                if (status.RemovedIn != null)
                {
                    status.State = ContentSyncState.Removed;
                    return status;
                }
            }

            var tracked = !string.IsNullOrEmpty(deployed.sourceTree);
            if (tracked && deployed.sourceTree == repositoryTree)
            {
                status.State = ContentSyncState.InSync;
                status.Verified = true;
                return status;
            }

            if (!tracked && deployed.sourceDirty)
            {
                status.State = ContentSyncState.Unknown;
                status.Detail = "The build could not record which files it was made from, so it may contain local changes. Rebuild it to get a fingerprint.";
                return status;
            }

            var source = string.IsNullOrEmpty(status.SourceSha) ? null : await ResolveCommitAsync(status.SourceSha);
            var sourceOnRemote = source != null && await IsAncestorAsync(source, remote);

            string baseCommit = null;
            if (tracked)
            {
                if (sourceOnRemote && await FingerprintAtAsync(source, package) == deployed.sourceTree) baseCommit = source;
                else
                {
                    baseCommit = await FindCommitWithTreeAsync(remote, package, deployed.sourceTree);
                    if (baseCommit != null) status.MatchedCommit = baseCommit.Substring(0, Math.Min(7, baseCommit.Length));
                }
            }
            else if (sourceOnRemote) baseCommit = source;
            else
            {
                status.State = ContentSyncState.Unknown;
                status.Detail = string.IsNullOrEmpty(status.SourceSha)
                    ? "The build does not say which commit it was made from, so it cannot be compared with the content repository."
                    : $"The build was made from commit {status.SourceSha}, which is not on {RemoteRef}, so it cannot be compared with the content repository.";
                return status;
            }

            if (baseCommit == null)
            {
                status.State = ContentSyncState.NotInRepository;
                if (sourceOnRemote)
                {
                    try { status.NewerCommits = await GetCommitsAsync(source, remote, package); }
                    catch { }
                }
                return status;
            }

            try { status.NewerCommits = await GetCommitsAsync(baseCommit, remote, package); }
            catch { }
            status.Loss = await FindMergeLossAsync(baseCommit, remote, package);
            if (status.Loss != null) status.State = ContentSyncState.RepositoryLostChanges;
            else if (tracked || status.NewerCommits.Count > 0) status.State = ContentSyncState.Behind;
            else status.State = ContentSyncState.InSync;
            return status;
        }

        public static string Describe(string environment, ContentDeployStatus status, bool stagingMatches = false)
        {
            if (status == null || status.State == ContentSyncState.NotDeployed) return $"Not on {environment}.";

            var text = new StringBuilder($"Live on {environment}: build {status.BuildId}");
            var built = new List<string>();
            if (DateTime.TryParse(status.BuiltAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var builtAt))
                built.Add($"{builtAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            if (!string.IsNullOrEmpty(status.SourceSha))
                built.Add($"from commit {status.SourceSha}{(status.BuiltFromLocalChanges ? " plus local changes" : "")}");
            if (built.Count > 0) text.Append("\nBuilt ").Append(string.Join(" ", built));
            return text.Append("\n\n").Append(DescribeState(status, environment, stagingMatches)).ToString();
        }

        public static string DescribeState(ContentDeployStatus status, string environment, bool stagingMatches = false)
        {
            var production = string.Equals(environment, ContentUploadSettings.instance.ProductionPrefix, StringComparison.Ordinal);
            switch (status.State)
            {
                case ContentSyncState.InSync:
                    return status.Verified
                        ? "Matches the content repository."
                        : "No newer commits in the content repository. The build carries no source fingerprint (it was made before 0.5, " +
                          "or an older version of the tool rewrote the manifest), so local changes in it cannot be ruled out.";

                case ContentSyncState.Behind:
                    var behind = new StringBuilder();
                    if (status.MatchedCommit != null)
                        behind.Append(status.BuiltFromLocalChanges
                            ? $"Built from local changes that were committed later as {status.MatchedCommit}.\n"
                            : $"The package files in this build match commit {status.MatchedCommit} on {RemoteRef}.\n");
                    if (status.NewerCommits.Count == 0)
                        behind.Append("The content repository differs from this build, but no commit since the build touches the package.");
                    else
                        AppendCommits(behind, status.NewerCommits, status.NewerCommits.Count == 1
                            ? "1 newer commit in the content repository is not in this build:"
                            : $"{status.NewerCommits.Count} newer commits in the content repository are not in this build:");
                    return behind.Append(!production ? "\nPull, build and upload to bring it up to date."
                        : stagingMatches ? "\nThey are not live yet: staging already matches the content repository, so promote it."
                        : "\nThey are not live yet: bring staging up to date (pull, build and upload), then promote it.").ToString();

                case ContentSyncState.NotInRepository:
                    var missing = new StringBuilder(status.BuiltFromLocalChanges
                        ? "This build was made from local changes that are not in the content repository as built: not committed, not pushed, or committed with further edits. " +
                          "Commit and push them, or the next build from the repository will drop them."
                        : $"This build was made from commit {status.SourceSha}, which is not on {RemoteRef} (not pushed, or rewritten). " +
                          "Push it, or the next build from the repository will drop its changes.");
                    if (status.NewerCommits.Count > 0)
                        AppendCommits(missing.Append('\n'), status.NewerCommits, "Commits on the repository since its source commit:");
                    return missing.ToString();

                case ContentSyncState.Removed:
                    return $"'{status.Package}' no longer exists on {RemoteRef}{(status.RemovedIn != null ? $" (removed or renamed in {status.RemovedIn})" : "")}. " +
                           $"Remove it from {environment} with the ⋮ menu, or deploy it under its new name.";

                case ContentSyncState.RepositoryLostChanges:
                    return DescribeLoss(status.Loss) + "\nThis build was made before that merge and may still contain the lost work. " +
                           "Restore it in the content repository before you build from the repository and replace this build.";

                case ContentSyncState.Unknown:
                    return status.Detail ?? "The build cannot be compared with the content repository.";

                default:
                    return "";
            }
        }

        public static string DescribeLoss(ContentMergeLoss loss)
        {
            var text = new StringBuilder($"Merge {loss.Merge}\nleft out changes to this package that a normal merge of its parents keeps. " +
                                         $"{loss.DroppedCount} file(s) are still in the state that merge left them in on {RemoteRef}, for example:");
            foreach (var path in loss.Examples) text.Append("\n  ").Append(path);
            return text.ToString();
        }

        private static void AppendCommits(StringBuilder text, List<ContentCommit> commits, string header)
        {
            text.Append(header);
            foreach (var commit in commits.Take(MaxListedCommits)) text.Append("\n  ").Append(commit);
            if (commits.Count > MaxListedCommits) text.Append($"\n  and {commits.Count - MaxListedCommits} more");
        }

        private static async Task<string> FindCommitWithTreeAsync(string remote, string package, string tree)
        {
            var key = $"{remote}\n{package}\n{tree}";
            if (TreeMatches.TryGetValue(key, out var cached)) return cached;

            string match = null;
            try
            {
                foreach (var commit in SplitLines(await Git($"log -n {MaxCommitsSearched} --format=%H {remote} -- {await PathSpecAsync(package, remote)}")))
                    if (await FingerprintAtAsync(commit, package) == tree)
                    {
                        match = commit;
                        break;
                    }
            }
            catch { return null; }
            TreeMatches[key] = match;
            return match;
        }

        /// <summary>
        /// Decides from trees alone, so no file contents are needed: a file counts as dropped when only one parent
        /// changed it since the merge base (a normal merge takes that change without a conflict) and the merge
        /// kept the other parent's unchanged version instead. Files both parents changed are left out (their
        /// resolution is a decision), together with their .meta companions, and so are files whose expected
        /// content the merge kept under another path (a rename).
        /// </summary>
        private static async Task<ContentMergeLoss> MergeLossAsync(string merge, string left, string right, string package)
        {
            var key = $"{merge}\n{package}";
            if (MergeLosses.TryGetValue(key, out var cached)) return cached;

            var mergeBase = await ContentGitApi.RunGitAsync($"merge-base --all {left} {right}", ContentGitApi.ContentAbsolutePath);
            if (mergeBase.ExitCode != 0 && mergeBase.ExitCode != 1)
                throw new InvalidOperationException($"git merge-base --all {left} {right} failed (exit {mergeBase.ExitCode}): {mergeBase.Error.Trim()}");
            var bases = mergeBase.ExitCode == 0 ? SplitLines(mergeBase.Output) : new List<string>();
            if (bases.Count != 1)
            {
                MergeLosses[key] = null;
                return null;
            }

            var actual = await PackageEntriesAsync(merge, package);
            var ancestor = await PackageEntriesAsync(bases[0], package);
            var ours = await PackageEntriesAsync(left, package);
            var theirs = await PackageEntriesAsync(right, package);

            string Blob(Dictionary<string, string> entries, string path) => entries.TryGetValue(path, out var blob) ? blob : null;
            var paths = actual.Keys.Union(ancestor.Keys).Union(ours.Keys).Union(theirs.Keys).ToList();
            var excluded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var original = Blob(ancestor, path);
                var oursBlob = Blob(ours, path);
                var theirsBlob = Blob(theirs, path);
                if (oursBlob == original || theirsBlob == original || oursBlob == theirsBlob) continue;
                Exclude(path);
            }

            // A file one side moved while the other side deleted it is a rename/delete conflict: resolving it as a
            // delete is a decision, not a loss. Detected by the moved file's content still being the original's.
            var originalPaths = ancestor.GroupBy(entry => entry.Value).ToDictionary(group => group.Key, group => group.Select(entry => entry.Key).ToList());
            foreach (var path in paths)
            {
                if (Blob(ancestor, path) != null) continue;
                var oursBlob = Blob(ours, path);
                var theirsBlob = Blob(theirs, path);
                if ((oursBlob == null) == (theirsBlob == null)) continue;
                var adding = oursBlob != null ? ours : theirs;
                var other = oursBlob != null ? theirs : ours;
                if (originalPaths.TryGetValue(oursBlob ?? theirsBlob, out var sources)
                    && sources.Any(source => Blob(adding, source) == null && Blob(other, source) == null))
                    Exclude(path);
            }

            void Exclude(string path)
            {
                excluded.Add(path);
                excluded.Add(path.EndsWith(".meta", StringComparison.Ordinal) ? path.Substring(0, path.Length - 5) : path + ".meta");
            }

            var keptBlobs = new HashSet<string>(actual.Values, StringComparer.Ordinal);
            var dropped = paths
                .Where(path => !excluded.Contains(path))
                .Where(path =>
                {
                    var original = Blob(ancestor, path);
                    var oursBlob = Blob(ours, path);
                    var theirsBlob = Blob(theirs, path);
                    var kept = Blob(actual, path);
                    var expected = oursBlob == original ? theirsBlob : oursBlob;
                    if (kept == expected || kept != oursBlob && kept != theirsBlob) return false;
                    return expected == null || !keptBlobs.Contains(expected);
                })
                .ToList();

            ContentMergeLoss loss = null;
            if (dropped.Count > 0)
            {
                loss = new ContentMergeLoss
                {
                    Merge = ParseCommit(SplitLines(await Git($"log -1 --format=%h%x09%ad%x09%an%x09%s --date=short {merge}")).FirstOrDefault())
                            ?? new ContentCommit { Sha = merge.Substring(0, 7) },
                    DroppedCount = dropped.Count,
                    Examples = PickExamples(dropped),
                };
                foreach (var path in dropped)
                {
                    loss.Kept[path] = Blob(actual, path);
                    loss.Expected[path] = Blob(ours, path) == Blob(ancestor, path) ? Blob(theirs, path) : Blob(ours, path);
                }
            }
            MergeLosses[key] = loss;
            return loss;
        }

        private static List<string> PickExamples(List<string> paths) => paths
            .OrderBy(path => path.EndsWith(".meta", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(path => path, StringComparer.Ordinal)
            .Take(MaxListedPaths)
            .ToList();

        private static async Task<Dictionary<string, string>> PackageEntriesAsync(string treeish, string package)
        {
            var key = $"{treeish}\n{package}";
            if (PackageListings.TryGetValue(key, out var cached)) return cached;

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in SplitLines(await Git($"-c core.quotepath=off ls-tree -r {treeish} -- {Quote(package + "/")}")))
            {
                var entry = ParseTreeEntry(line);
                if (entry.Key != null) entries[entry.Key] = entry.Value;
            }
            foreach (var group in await GroupEntriesAtAsync(treeish, package))
                entries[$"{ContentGitApi.GroupsFolderName}/{group.Key}"] = group.Value;
            PackageListings[key] = entries;
            return entries;
        }

        private static async Task<string> FingerprintAtAsync(string commit, string package)
        {
            var key = $"{commit}\n{package}";
            if (Fingerprints.TryGetValue(key, out var cached)) return cached;

            var folder = await ContentGitApi.RunGitAsync($"rev-parse --verify --quiet {Quote($"{commit}:{package}")}", ContentGitApi.ContentAbsolutePath);
            string fingerprint = null;
            if (folder.ExitCode == 0 && folder.Output.Trim().Length > 0)
            {
                var lines = new List<string> { $"tree {folder.Output.Trim()}" };
                lines.AddRange((await GroupEntriesAtAsync(commit, package)).Select(entry => $"{entry.Key} {entry.Value}"));
                fingerprint = Fingerprint(lines);
            }
            else if (folder.ExitCode != 1) return null;

            Fingerprints[key] = fingerprint;
            return fingerprint;
        }

        private static async Task<List<KeyValuePair<string, string>>> GroupEntriesAtAsync(string treeish, string package)
        {
            if (!GroupListings.TryGetValue(treeish, out var listing))
            {
                listing = new List<KeyValuePair<string, string>>();
                var output = await Git($"-c core.quotepath=off ls-tree {treeish} -- {Quote(ContentGitApi.GroupsFolderName + "/")}");
                foreach (var line in SplitLines(output))
                {
                    var entry = ParseTreeEntry(line);
                    if (entry.Key != null) listing.Add(new KeyValuePair<string, string>(Path.GetFileName(entry.Key), entry.Value));
                }
                GroupListings[treeish] = listing;
            }
            return listing.Where(entry => ContentGitApi.IsGroupFileOf(package, entry.Key)).ToList();
        }

        private static async Task<string> PathSpecAsync(string package, params string[] commits)
        {
            var groups = ContentGitApi.GroupsFolderName;
            var names = new SortedSet<string>(StringComparer.Ordinal) { $"{package}.asset", $"{package}.asset.meta" };
            foreach (var commit in commits)
                foreach (var entry in await GroupEntriesAtAsync(commit, package))
                    names.Add(entry.Key);
            return string.Join(" ", names.Select(name => $"{groups}/{name}").Prepend(package).Select(Literal));
        }

        private static async Task<string> ResolveCommitAsync(string revision)
        {
            if (string.IsNullOrEmpty(revision)) return null;
            var stable = IsCommitId(revision);
            if (stable && ResolvedCommits.TryGetValue(revision, out var cached)) return cached;

            var result = await ContentGitApi.RunGitAsync($"rev-parse --verify --quiet {Quote(revision + "^{commit}")}", ContentGitApi.ContentAbsolutePath);
            var commit = result.ExitCode == 0 ? result.Output.Trim() : null;
            if (string.IsNullOrEmpty(commit)) return null;
            if (stable) ResolvedCommits[revision] = commit;
            return commit;
        }

        private static async Task<bool> IsAncestorAsync(string ancestor, string descendant)
        {
            var key = $"{ancestor}\n{descendant}";
            if (Ancestry.TryGetValue(key, out var cached)) return cached;

            var result = await ContentGitApi.RunGitAsync($"merge-base --is-ancestor {ancestor} {descendant}", ContentGitApi.ContentAbsolutePath);
            if (result.ExitCode != 0 && result.ExitCode != 1) return false;
            Ancestry[key] = result.ExitCode == 0;
            return result.ExitCode == 0;
        }

        private static bool IsCommitId(string revision) =>
            revision.Length >= 7 && revision.Length <= 40 && revision.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        private static ContentCommit ParseCommit(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var fields = line.Split(new[] { '\t' }, 4);
            return fields.Length < 4 ? null : new ContentCommit { Sha = fields[0], Date = fields[1], Author = fields[2], Subject = fields[3] };
        }

        private static KeyValuePair<string, string> ParseTreeEntry(string line)
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) return default;
            var fields = line.Substring(0, tab).Split(' ');
            if (fields.Length < 3 || fields[1] != "blob") return default;
            return new KeyValuePair<string, string>(line.Substring(tab + 1).Trim(), fields[2]);
        }

        private static List<KeyValuePair<string, string>> ParseIndexEntries(string output)
        {
            var entries = new List<KeyValuePair<string, string>>();
            foreach (var line in SplitLines(output))
            {
                var tab = line.IndexOf('\t');
                if (tab < 0) continue;
                var fields = line.Substring(0, tab).Split(' ');
                if (fields.Length < 3) continue;
                entries.Add(new KeyValuePair<string, string>(Path.GetFileName(line.Substring(tab + 1).Trim()), fields[1]));
            }
            return entries;
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

        private static string Literal(string path) => Quote(":(literal)" + path);

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
