using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ContentRepo.Editor
{
    public enum ChangeKind { Added, Modified, Deleted }

    public struct FileChange
    {
        public string Path;   // relative to repo root, e.g. "FolderA/sub/file.txt"
        public ChangeKind Kind;
    }

    public struct FolderStatus
    {
        public int Staged;     // changes in the index (git add)
        public int Modified;   // worktree modifications not yet staged
        public int Deleted;    // worktree deletions not yet staged
        public int Untracked;  // files unknown to git
        public List<FileChange> Files;

        public bool IsClean => Staged == 0 && Modified == 0 && Deleted == 0 && Untracked == 0;

        public override string ToString()
        {
            if (IsClean) return "clean";
            var parts = new List<string>(4);
            if (Staged > 0) parts.Add($"↑{Staged}");
            if (Modified + Deleted > 0) parts.Add($"~{Modified + Deleted}");
            if (Untracked > 0) parts.Add($"?{Untracked}");
            return string.Join(" ", parts);
        }
    }

    public static class ContentGitApi
    {
        public static event Action OnStateChanged;

        /// <summary>
        /// Set when a remote operation (push/pull) failed but the local state was still applied.
        /// Cleared by <see cref="ClearLastWarning"/>; the editor window clears it before each operation.
        /// </summary>
        public static string LastWarning { get; private set; }

        public static void ClearLastWarning() => LastWarning = null;

        /// <summary>Local commits not yet pushed to the tracking branch.</summary>
        public static int RepositoryAhead { get; private set; }

        /// <summary>Remote commits not yet pulled into the local branch.</summary>
        public static int RepositoryBehind { get; private set; }

        private static async Task TryRemoteAsync(string action, Func<Task> op)
        {
            try
            {
                await op();
            }
            catch (Exception ex)
            {
                LastWarning = $"Remote {action} failed; local changes were still applied. {ex.Message}";
                UnityEngine.Debug.LogWarning(LastWarning);
            }
        }

        public static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)!.FullName;

        public static string ContentAbsolutePath
        {
            get
            {
                var rel = ContentRepoSettings.instance.LocalPath;
                if (string.IsNullOrEmpty(rel))
                    return ProjectRoot;
                return Path.GetFullPath(Path.Combine(ProjectRoot, rel));
            }
        }

        public static async Task InitAsync()
        {
            var settings = ContentRepoSettings.instance;
            if (string.IsNullOrWhiteSpace(settings.RemoteUrl))
                throw new InvalidOperationException("Remote URL is not configured. Set it under Project Settings > Content Repo.");
            if (string.IsNullOrWhiteSpace(settings.LocalPath))
                throw new InvalidOperationException("Local path is not configured.");
            if (string.IsNullOrWhiteSpace(settings.Branch))
                throw new InvalidOperationException("Default branch is not configured.");

            var subPath = ContentAbsolutePath;
            var gitMarker = Path.Combine(subPath, ".git");

            if (!File.Exists(gitMarker) && !Directory.Exists(gitMarker))
            {
                // Plain clone (not a git submodule) so the parent repo is never touched.
                // Any leftover directory without a .git marker is cleaned up first.
                if (Directory.Exists(subPath))
                    await Task.Run(() => Directory.Delete(subPath, true));

                await RunGitCommandAsync(
                    $"clone --no-checkout -b {Quote(settings.Branch)} -- {Quote(settings.RemoteUrl)} {Quote(subPath)}",
                    ProjectRoot);
            }

            await RunGitCommandAsync("sparse-checkout init --cone", subPath);
            await RunGitCommandAsync("sparse-checkout set", subPath);

            // Write a .gitignore next to the cloned directory so the parent repo
            // never shows the content folder or its Unity .meta file as noise.
            await WriteGitignoreNextToAsync(settings.LocalPath);

            NotifyChange();
            AssetDatabase.Refresh();
        }

        private static async Task WriteGitignoreNextToAsync(string localPath)
        {
            var normalized = localPath.Replace('\\', '/').TrimEnd('/');
            var slash = normalized.LastIndexOf('/');

            var parentAbsPath = slash > 0
                ? Path.GetFullPath(Path.Combine(ProjectRoot, normalized.Substring(0, slash)))
                : ProjectRoot;
            var dirName = slash > 0 ? normalized.Substring(slash + 1) : normalized;

            var gitignorePath = Path.Combine(parentAbsPath, ".gitignore");

            var entry1 = $"/{dirName}/";
            var entry2 = $"/{dirName}.meta";

            var existing = File.Exists(gitignorePath)
                ? await Task.Run(() => File.ReadAllText(gitignorePath))
                : "";

            var missing = new List<string>(2);
            if (!existing.Contains(entry1)) missing.Add(entry1);
            if (!existing.Contains(entry2)) missing.Add(entry2);

            if (missing.Count == 0) return;

            var prefix = existing.Length > 0 && !existing.EndsWith("\n") ? "\n" : "";
            var block = prefix +
                        "\n# Content Browser – managed by the ContentBrowser package\n" +
                        string.Join("\n", missing) + "\n";

            await Task.Run(() => File.AppendAllText(gitignorePath, block));
        }

        public static async Task<bool> IsInitializedAsync()
        {
            var path = ContentAbsolutePath;
            if (!Directory.Exists(path))
                return false;

            var gitMarker = Path.Combine(path, ".git");
            if (!Directory.Exists(gitMarker) && !File.Exists(gitMarker))
                return false;

            try
            {
                await RunGitCommandAsync("rev-parse --git-dir", path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<List<string>> GetRemoteFoldersAsync()
        {
            var path = ContentAbsolutePath;

            try
            {
                await RunGitCommandAsync("fetch origin", path);
            }
            catch
            {
                // tolerate offline / missing-remote — fall back to whatever HEAD points at locally
            }

            var branch = ContentRepoSettings.instance.Branch;
            var output = await RunGitCommandAsync($"ls-tree origin/{Quote(branch)}", path);

            var folders = new List<string>();
            foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = line.IndexOf('\t');
                if (tab < 0) continue;

                var meta = line.Substring(0, tab).Split(' ');
                if (meta.Length < 2 || meta[1] != "tree") continue;

                var name = line.Substring(tab + 1).Trim();
                if (!string.IsNullOrEmpty(name))
                    folders.Add(name);
            }

            folders.Sort(StringComparer.OrdinalIgnoreCase);
            return folders;
        }

        public static async Task<List<string>> GetCheckedOutFoldersAsync()
        {
            try
            {
                var output = await RunGitCommandAsync("sparse-checkout list", ContentAbsolutePath);
                return output
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().Trim('/'))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Runs a single <c>git status -sbu</c> over the submodule and returns
        /// the breakdown keyed by top-level folder name.  Non-throwing; returns empty on error.
        /// </summary>
        public static async Task<Dictionary<string, FolderStatus>> GetAllFolderStatusesAsync()
        {
            var result = new Dictionary<string, FolderStatus>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // -sbu: short format + branch ahead/behind + always show untracked files
                var output = await RunGitCommandAsync("status -sbu", ContentAbsolutePath, silent: true);
                RepositoryAhead = 0;
                RepositoryBehind = 0;

                foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("##"))
                    {
                        RepositoryAhead = ParseCount(line, "ahead ");
                        RepositoryBehind = ParseCount(line, "behind ");
                        continue;
                    }

                    if (line.Length < 4) continue;

                    var x = line[0];
                    var y = line[1];
                    var path = StripGitQuotes(line.Substring(3));

                    var arrowIdx = path.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrowIdx >= 0) path = StripGitQuotes(path.Substring(arrowIdx + 4));

                    var slash = path.IndexOf('/');
                    var topLevel = slash > 0 ? path.Substring(0, slash).Trim() : string.Empty;
                    if (string.IsNullOrEmpty(topLevel)) continue;

                    result.TryGetValue(topLevel, out var s);
                    if (s.Files == null) s.Files = new List<FileChange>();

                    ChangeKind kind;
                    if (x == '?' && y == '?')
                    {
                        s.Untracked++;
                        kind = ChangeKind.Added;
                    }
                    else
                    {
                        if (x != ' ') s.Staged++;
                        if (y == 'M') s.Modified++;
                        if (y == 'D') s.Deleted++;
                        if (x == 'D' || y == 'D') kind = ChangeKind.Deleted;
                        else if (x == 'A') kind = ChangeKind.Added;
                        else kind = ChangeKind.Modified;
                    }
                    s.Files.Add(new FileChange { Path = path, Kind = kind });
                    result[topLevel] = s;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"git status poll failed: {ex.Message}");
                RepositoryAhead = 0;
                RepositoryBehind = 0;
            }
            return result;
        }

        private static string StripGitQuotes(string s) =>
            s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;

        private static int ParseCount(string s, string keyword)
        {
            var i = s.IndexOf(keyword, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += keyword.Length;
            var start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            return i > start ? int.Parse(s.Substring(start, i - start)) : 0;
        }

        public static async Task CheckOutFolderAsync(string folder)
        {
            ValidateFolderName(folder);

            await EnsureSparseCheckoutAsync();
            await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);
            await TryRemoteAsync("pull", () => RunGitCommandAsync(
                $"pull origin {Quote(ContentRepoSettings.instance.Branch)}",
                ContentAbsolutePath));

            NotifyChange();
            AssetDatabase.Refresh();
        }

        public static async Task DisconnectFolderAsync(string folder)
        {
            ValidateFolderName(folder);

            var current = await GetCheckedOutFoldersAsync();
            var remaining = current
                .Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (remaining.Count == 0)
            {
                await RunGitCommandAsync("sparse-checkout set", ContentAbsolutePath);
            }
            else
            {
                var args = string.Join(" ", remaining.Select(Quote));
                await RunGitCommandAsync($"sparse-checkout set {args}", ContentAbsolutePath);
            }

            var localFolder = Path.Combine(ContentAbsolutePath, folder);
            if (Directory.Exists(localFolder))
            {
                try { Directory.Delete(localFolder, true); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"Failed to delete local folder '{localFolder}': {ex.Message}"); }
            }

            var meta = localFolder + ".meta";
            if (File.Exists(meta))
            {
                try { File.Delete(meta); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning($"Failed to delete meta '{meta}': {ex.Message}"); }
            }

            NotifyChange();
            AssetDatabase.Refresh();
        }

        public static async Task CreateFolderAsync(string folder)
        {
            ValidateFolderName(folder);

            var localFolder = Path.Combine(ContentAbsolutePath, folder);
            if (Directory.Exists(localFolder))
                throw new InvalidOperationException($"A folder named '{folder}' already exists in the content repository.");

            await Task.Run(() =>
            {
                Directory.CreateDirectory(localFolder);
                File.WriteAllText(Path.Combine(localFolder, ".gitkeep"), "");
            });

            await EnsureSparseCheckoutAsync();
            await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);
            await RunGitCommandAsync($"add -- {Quote(folder)}", ContentAbsolutePath);
            await RunGitCommandAsync($"commit -m {Quote("Add " + folder)}", ContentAbsolutePath);
            await TryRemoteAsync("push", () => RunGitCommandAsync(
                $"push origin HEAD:{Quote(ContentRepoSettings.instance.Branch)}",
                ContentAbsolutePath));

            NotifyChange();
            AssetDatabase.Refresh();
        }

        public static async Task RenameFolderAsync(string oldFolder, string newFolder)
        {
            ValidateFolderName(oldFolder);
            ValidateFolderName(newFolder);

            // Ordinal comparison so a case-only rename (e.g. "folder" -> "Folder") still proceeds.
            if (string.Equals(oldFolder, newFolder, StringComparison.Ordinal))
                return;

            var wasCheckedOut = await IsFolderCheckedOutAsync(oldFolder);

            if (!wasCheckedOut)
            {
                await EnsureSparseCheckoutAsync();
                await RunGitCommandAsync($"sparse-checkout add {Quote(oldFolder)}", ContentAbsolutePath);
                await RunGitCommandAsync(
                    $"pull origin {Quote(ContentRepoSettings.instance.Branch)}",
                    ContentAbsolutePath);
            }

            // Case-only rename on case-insensitive filesystems (Windows/macOS default) requires a
            // two-step mv through a temporary name, otherwise git refuses or silently no-ops.
            var caseOnly = string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase);
            if (caseOnly)
            {
                var tempName = $"{oldFolder}__cs_rename_{Guid.NewGuid():N}";
                await RunGitCommandAsync($"mv -f -- {Quote(oldFolder)} {Quote(tempName)}", ContentAbsolutePath);
                await RunGitCommandAsync($"mv -f -- {Quote(tempName)} {Quote(newFolder)}", ContentAbsolutePath);
            }
            else
            {
                await RunGitCommandAsync($"mv -- {Quote(oldFolder)} {Quote(newFolder)}", ContentAbsolutePath);
            }

            await RunGitCommandAsync(
                $"commit -m {Quote($"Rename {oldFolder} to {newFolder}")}",
                ContentAbsolutePath);
            await TryRemoteAsync("push", () => RunGitCommandAsync(
                $"push origin HEAD:{Quote(ContentRepoSettings.instance.Branch)}",
                ContentAbsolutePath));

            // Replace old name in sparse-checkout; if the folder wasn't checked out
            // before, restore to the state it was in (exclude the renamed folder).
            var current = await GetCheckedOutFoldersAsync();
            var updated = current
                .Select(f => string.Equals(f, oldFolder, StringComparison.OrdinalIgnoreCase) ? newFolder : f)
                .ToList();

            if (!wasCheckedOut)
                updated.Remove(newFolder);

            if (updated.Count == 0)
                await RunGitCommandAsync("sparse-checkout set", ContentAbsolutePath);
            else
                await RunGitCommandAsync($"sparse-checkout set {string.Join(" ", updated.Select(Quote))}", ContentAbsolutePath);

            NotifyChange();
            AssetDatabase.Refresh();
        }

        public static async Task<bool> IsFolderCheckedOutAsync(string folder)
        {
            ValidateFolderName(folder);
            var folders = await GetCheckedOutFoldersAsync();
            return folders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        }

        public static async Task PullFolderAsync(string folder)
        {
            ValidateFolderName(folder);
            await RunGitCommandAsync(
                $"pull origin {Quote(ContentRepoSettings.instance.Branch)}",
                ContentAbsolutePath);

            NotifyChange();
            AssetDatabase.Refresh();
        }

        public static async Task CommitAndPushFolderAsync(string folder, string commitMessage)
        {
            ValidateFolderName(folder);
            if (string.IsNullOrWhiteSpace(commitMessage))
                throw new ArgumentException("Commit message cannot be empty.", nameof(commitMessage));

            await RunGitCommandAsync($"add -- {Quote(folder)}", ContentAbsolutePath);

            var statusOut = await RunGitCommandAsync("diff --cached --name-only", ContentAbsolutePath);
            if (string.IsNullOrWhiteSpace(statusOut))
                throw new InvalidOperationException($"No staged changes inside '{folder}'.");

            await RunGitCommandAsync($"commit -m {Quote(commitMessage)}", ContentAbsolutePath);
            await TryRemoteAsync("push", () => RunGitCommandAsync(
                $"push origin HEAD:{Quote(ContentRepoSettings.instance.Branch)}",
                ContentAbsolutePath));

            NotifyChange();
        }

        /// <summary>
        /// Discards local changes for tracked files (restores to HEAD) and deletes untracked Added files.
        /// Also removes corresponding .meta files for untracked entries.
        /// </summary>
        public static async Task DiscardFilesAsync(IEnumerable<string> repoPaths, IEnumerable<ChangeKind> kinds)
        {
            var pairs = repoPaths.Zip(kinds, (p, k) => (p, k)).ToList();
            var toRestore = pairs.Where(x => x.k != ChangeKind.Added).Select(x => x.p).ToList();
            var toDelete  = pairs.Where(x => x.k == ChangeKind.Added).Select(x => x.p).ToList();

            if (toRestore.Count > 0)
            {
                var args = string.Join(" ", toRestore.Select(Quote));
                try { await RunGitCommandAsync($"restore --source=HEAD --staged --worktree -- {args}", ContentAbsolutePath); }
                catch { await RunGitCommandAsync($"checkout HEAD -- {args}", ContentAbsolutePath); }
            }

            if (toDelete.Count > 0)
                await DeleteLocalFilesAsync(toDelete);

            NotifyChange();
            AssetDatabase.Refresh();
        }

        /// <summary>Deletes untracked local files (and their .meta counterparts) from disk.</summary>
        public static async Task DeleteLocalFilesAsync(IEnumerable<string> repoPaths)
        {
            var basePath = ContentAbsolutePath;          // evaluate on main thread
            var paths    = repoPaths.ToList();           // materialise on main thread
            await Task.Run(() =>
            {
                foreach (var rel in paths)
                {
                    var full = Path.Combine(basePath, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full)) File.Delete(full);
                    var meta = full + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                }
            });
            NotifyChange();
            AssetDatabase.Refresh();
        }

        public static async Task DeleteRemoteFolderAsync(string folder)
        {
            ValidateFolderName(folder);

            var checkedOut = await IsFolderCheckedOutAsync(folder);
            if (!checkedOut)
            {
                await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);
                await RunGitCommandAsync(
                    $"pull origin {Quote(ContentRepoSettings.instance.Branch)}",
                    ContentAbsolutePath);
            }

            await RunGitCommandAsync($"rm -r -- {Quote(folder)}", ContentAbsolutePath);
            await RunGitCommandAsync(
                $"commit -m {Quote("Remove " + folder)}",
                ContentAbsolutePath);
            await TryRemoteAsync("push", () => RunGitCommandAsync(
                $"push origin HEAD:{Quote(ContentRepoSettings.instance.Branch)}",
                ContentAbsolutePath));

            NotifyChange();
            AssetDatabase.Refresh();
        }

        public static async Task PullAllAsync()
        {
            await RunGitCommandAsync(
                $"pull origin {Quote(ContentRepoSettings.instance.Branch)}",
                ContentAbsolutePath);

            NotifyChange();
            AssetDatabase.Refresh();
        }

        public static async Task<string> GetStatusAsync(string folder)
        {
            ValidateFolderName(folder);
            var output = await RunGitCommandAsync(
                $"status --porcelain -- {Quote(folder)}",
                ContentAbsolutePath);

            if (string.IsNullOrWhiteSpace(output))
                return "clean";

            var lines = output.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var modified = 0;
            var untracked = 0;
            foreach (var l in lines)
            {
                if (l.Length < 2) continue;
                if (l.StartsWith("??")) untracked++;
                else if (l[0] != ' ' || l[1] != ' ') modified++;
            }

            if (modified == 0 && untracked == 0) return "clean";
            var parts = new List<string>();
            if (modified > 0) parts.Add($"{modified} modified");
            if (untracked > 0) parts.Add($"{untracked} untracked");
            return string.Join(", ", parts);
        }

        public static Task<string> RunGitCommandAsync(string args, string workingDir = null, bool silent = true)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? ProjectRoot,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            if (!silent)
                UnityEngine.Debug.Log($"[ContentRepo] > git {args}  (in {startInfo.WorkingDirectory})");

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
                        "Failed to start 'git'. Make sure git is installed and available on PATH.", ex);
                }

                using (process)
                {
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();

                    process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        var err = stderr.ToString().Trim();
                        if (string.IsNullOrEmpty(err)) err = stdout.ToString().Trim();
                        throw new InvalidOperationException(
                            $"git {args} failed (exit {process.ExitCode}): {err}");
                    }

                    return stdout.ToString();
                }
            });
        }

        private static async Task EnsureSparseCheckoutAsync()
        {
            try
            {
                await RunGitCommandAsync("sparse-checkout list", ContentAbsolutePath);
            }
            catch
            {
                await RunGitCommandAsync("sparse-checkout init --cone", ContentAbsolutePath);
            }
        }

        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void ValidateFolderName(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("Folder name cannot be empty.", nameof(folder));
            if (folder.Contains("..") || folder.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0)
                throw new ArgumentException("Invalid folder name.", nameof(folder));
        }

        private static void NotifyChange()
        {
            try { OnStateChanged?.Invoke(); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }
    }
}
