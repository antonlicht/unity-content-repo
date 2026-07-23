using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
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
        // Top-level folder in the content repo that always stays checked out.
        // It holds Addressable group .asset files so GUID stability is preserved
        // across developers and CI without requiring the package content to be present.
        public const string GroupsFolderName = "_groups";

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
            // Always check out _groups so Addressable group GUIDs are available
            // even when no package content is checked out.
            await RunGitCommandAsync($"sparse-checkout set {Quote(GroupsFolderName)}", subPath);

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

        /// <summary>
        /// Returns the names of all direct subfolders of <see cref="ContentAbsolutePath"/>
        /// that exist on disk right now — regardless of whether they are on the remote or in
        /// the sparse-checkout list.  The <c>_groups</c> system folder and any hidden folders
        /// (starting with '.') are excluded.
        /// </summary>
        public static Task<List<string>> GetLocalFoldersOnDiskAsync()
        {
            return Task.Run(() =>
            {
                var root = ContentAbsolutePath;
                if (!Directory.Exists(root)) return new List<string>();

                var folders = new List<string>();
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.StartsWith(".")) continue;
                    if (name.Equals(GroupsFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    folders.Add(name);
                }
                folders.Sort(StringComparer.OrdinalIgnoreCase);
                return folders;
            });
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
                if (!string.IsNullOrEmpty(name) && !name.Equals(GroupsFolderName, StringComparison.OrdinalIgnoreCase))
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
                    // _groups is a system folder managed by the package, not a user content package.
                    .Where(s => !string.IsNullOrEmpty(s) && !s.Equals(GroupsFolderName, StringComparison.OrdinalIgnoreCase))
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
            
            AssetDatabase.StartAssetEditing();
            try
            {
                await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);
                await TryRemoteAsync("pull", () => RunGitCommandAsync(
                    $"pull origin {Quote(ContentRepoSettings.instance.Branch)}",
                    ContentAbsolutePath));

                // Adding a folder to the cone does not reliably clear git's skip-worktree bit on
                // files that were already materialised on disk (e.g. content moved into the repo, or
                // checked out by an older tool version). While that bit is set, `git status` hides all
                // edits and `git add` refuses to stage them — so the Content Browser shows the package
                // clean and commits silently drop changes. Clear it so the files are tracked normally.
                await ClearSkipWorktreeAsync(folder);

                await TryRemoteAsync("group restore", () => RestoreGroupAssetAsync(folder));
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                NotifyChange();
                AssetDatabase.Refresh();
                // The group asset is now imported; re-register it if a prior disconnect removed it.
                ReAddContentGroupToSettings(folder);
            }
        }

        private static async Task RestoreGroupAssetAsync(string folder)
        {
            var groupRelPath     = $"{GroupsFolderName}/{folder}.asset";
            var groupMetaRelPath = $"{GroupsFolderName}/{folder}.asset.meta";

            // git checkout HEAD -- <path> restores the file to the last committed state.
            // Silently ignore paths that don't exist in HEAD yet (e.g. first-ever checkout
            // before any build has committed the group file).
            foreach (var relPath in new[] { groupRelPath, groupMetaRelPath })
            {
                try
                {
                    await RunGitCommandAsync($"checkout HEAD -- {Quote(relPath)}", ContentAbsolutePath);
                }
                catch (InvalidOperationException ex)
                    when (ex.Message.Contains("did not match") || ex.Message.Contains("pathspec"))
                {
                    // File not yet committed — no group to restore; that's fine.
                    Debug.LogWarning($"[ContentRepo] No committed group asset found for '{folder}' in {GroupsFolderName}/; skipping group restore.");
                }
            }
        }

        public static async Task DisconnectFolderAsync(string folder)
        {
            ValidateFolderName(folder);

            var current = await GetCheckedOutFoldersAsync(); // _groups is filtered out
            var remaining = current
                .Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase))
                .Prepend(GroupsFolderName) // always keep _groups checked out
                .Select(Quote);
            await RunGitCommandAsync($"sparse-checkout set {string.Join(" ", remaining)}", ContentAbsolutePath);

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

            // Drop the package's Addressables group from the project so it doesn't orphan (its assets
            // are gone). The committed _groups/<folder>.asset stays on disk and is re-registered on
            // the next checkout, so this is reversible.
            RemoveContentGroupFromSettings(folder);

            NotifyChange();
            AssetDatabase.Refresh();
        }

        /// <summary>Removes the package's Addressables group from the active settings without deleting
        /// its committed group asset (kept in <c>_groups/</c> for re-checkout).</summary>
        private static void RemoveContentGroupFromSettings(string folder)
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                var group = settings != null ? settings.FindGroup(folder) : null;
                if (group == null) return;
                settings.groups.Remove(group);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
                Debug.Log($"[ContentRepo] Removed Addressables group '{folder}' from the project (disconnected).");
            }
            catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Could not remove Addressables group '{folder}': {ex.Message}"); }
        }

        /// <summary>Re-registers a package's committed group asset into the Addressables settings after
        /// checkout, in case a prior disconnect removed it. No-op when already present or absent.</summary>
        private static void ReAddContentGroupToSettings(string folder)
        {
            try
            {
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null || settings.FindGroup(folder) != null) return;
                var localPath = ContentRepoSettings.instance.LocalPath?.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrEmpty(localPath)) return;
                var group = AssetDatabase.LoadAssetAtPath<AddressableAssetGroup>($"{localPath}/{GroupsFolderName}/{folder}.asset");
                if (group == null) return;
                settings.groups.Add(group);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
                Debug.Log($"[ContentRepo] Re-registered Addressables group '{folder}' after checkout.");
            }
            catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Could not re-register Addressables group '{folder}': {ex.Message}"); }
        }

        public static async Task CreateFolderAsync(string folder)
        {
            ValidateFolderName(folder);

            var localFolder = Path.Combine(ContentAbsolutePath, folder);
            if (Directory.Exists(localFolder))
                throw new InvalidOperationException($"A folder named '{folder}' already exists in the content repository.");

            await EnsureSparseCheckoutAsync();

            // If this folder exists in local HEAD (e.g. from a prior creation whose deletion was
            // never pushed), `sparse-checkout add` below would restore those old files from HEAD.
            // Detect that and commit a local deletion first so the folder starts truly empty.
            var inLocalHead = !string.IsNullOrWhiteSpace(
                await RunGitCommandAsync($"ls-tree -d HEAD -- {Quote(folder)}", ContentAbsolutePath, silent: true));
            if (inLocalHead)
            {
                Debug.Log($"[ContentRepo] '{folder}' still exists in local HEAD from a prior creation — committing its removal before recreating.");
                // Add to sparse-checkout so git checks out the stale files, then delete them.
                await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);
                await ClearSkipWorktreeAsync(folder);
                await RunGitCommandAsync($"rm -rf --ignore-unmatch -- {Quote(folder)}", ContentAbsolutePath);
                foreach (var relPath in GetGroupRelativePaths(folder))
                    try { await RunGitCommandAsync($"rm -f --ignore-unmatch -- {Quote(relPath)}", ContentAbsolutePath); } catch { }
                // Only commit if something was actually staged (rm --ignore-unmatch may stage nothing).
                var staged = await RunGitCommandAsync("diff --cached --name-only", ContentAbsolutePath, silent: true);
                if (!string.IsNullOrWhiteSpace(staged))
                    await RunGitCommandAsync($"commit -m {Quote($"Remove stale {folder}")}", ContentAbsolutePath);
            }

            // Also clear any staged-but-uncommitted index entries so they can't be restored.
            await ClearSkipWorktreeAsync(folder);
            try { await RunGitCommandAsync($"rm -rf --cached -- {Quote(folder)}", ContentAbsolutePath); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("did not match") || ex.Message.Contains("pathspec")) { }

            await Task.Run(() => Directory.CreateDirectory(localFolder));
            await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);

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
            var current = await GetCheckedOutFoldersAsync(); // _groups is filtered out
            var updated = current
                .Select(f => string.Equals(f, oldFolder, StringComparison.OrdinalIgnoreCase) ? newFolder : f)
                .ToList();

            if (!wasCheckedOut)
                updated.Remove(newFolder);

            updated.Insert(0, GroupsFolderName); // always keep _groups checked out
            await RunGitCommandAsync($"sparse-checkout set {string.Join(" ", updated.Select(Quote))}", ContentAbsolutePath);

            NotifyChange();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Adds <paramref name="folder"/> to the sparse-checkout list if it is not already present,
        /// and clears git's skip-worktree bit on its files so edits are tracked. Safe and idempotent
        /// to call repeatedly.
        /// </summary>
        public static async Task EnsureFolderInSparseCheckoutAsync(string folder)
        {
            var current = await GetCheckedOutFoldersAsync();
            var alreadyInCone = current.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            if (!alreadyInCone)
            {
                await EnsureSparseCheckoutAsync();
                await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);
            }

            // A folder present on disk but outside the cone (content moved into the repo, or checked
            // out by an older tool version) keeps git's skip-worktree bit on its files, which makes
            // git ignore every edit — the Content Browser then shows it clean and commits drop
            // changes. Always clear the bit so the folder's files are tracked normally. This is the
            // repair path invoked when the window discovers an on-disk-but-not-in-cone package.
            await ClearSkipWorktreeAsync(folder);
        }

        /// <summary>
        /// Removes <paramref name="folder"/> from the sparse-checkout list if it is present.
        /// Used to prune stale entries for folders that are no longer on the remote or on disk.
        /// Always keeps <c>_groups</c> in the list.
        /// </summary>
        public static async Task RemoveFolderFromSparseCheckoutAsync(string folder)
        {
            var current = await GetCheckedOutFoldersAsync();
            if (!current.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                return; // already absent — nothing to do

            var remaining = current
                .Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase))
                .Prepend(GroupsFolderName)
                .Select(Quote);
            await RunGitCommandAsync($"sparse-checkout set {string.Join(" ", remaining)}", ContentAbsolutePath);
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

            // Defensive: if any of the folder's files still carry the skip-worktree bit, `git add`
            // silently skips them and the commit would omit real changes (e.g. edited voice maps,
            // scripts, or art). Clear it first so the whole working state of the folder is staged.
            await ClearSkipWorktreeAsync(folder);

            // Stage package content normally.
            await RunGitCommandAsync($"add -- {Quote(folder)}", ContentAbsolutePath);

            // Stage the Addressable group asset and any schema files with --sparse: files in _groups/
            // may not be part of the sparse-checkout worktree definition, so a plain git add refuses them.
            // Enumerate all matching files on disk so schemas are included if Unity writes them separately.
            var groupFiles = GetGroupRelativePaths(folder).ToList();
            if (groupFiles.Count > 0)
                await RunGitCommandAsync(
                    $"add --sparse -- {string.Join(" ", groupFiles.Select(Quote))}",
                    ContentAbsolutePath);

            var statusOut = await RunGitCommandAsync("diff --cached --name-only", ContentAbsolutePath);
            if (string.IsNullOrWhiteSpace(statusOut))
                throw new InvalidOperationException($"No staged changes in '{folder}' or its Addressable group files.");

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

            var branch        = ContentRepoSettings.instance.Branch;
            var remoteFolders = await GetRemoteFoldersAsync();
            var isOnRemote    = remoteFolders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));

            Debug.Log($"[ContentRepo] DeleteRemoteFolderAsync '{folder}': isOnRemote={isOnRemote}");

            if (isOnRemote)
            {
                // Folder is committed on the remote: check it out if needed, then git rm + commit + push.
                var checkedOut = await IsFolderCheckedOutAsync(folder);
                if (!checkedOut)
                {
                    await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);
                    await RunGitCommandAsync($"pull origin {Quote(branch)}", ContentAbsolutePath);
                }

                await ClearSkipWorktreeAsync(folder);
                await RunGitCommandAsync($"rm -rf -- {Quote(folder)}", ContentAbsolutePath);
                foreach (var relPath in GetGroupRelativePaths(folder))
                {
                    try { await RunGitCommandAsync($"rm -f -- {Quote(relPath)}", ContentAbsolutePath); }
                    catch { /* group file may not be committed — fine */ }
                }
                await RunGitCommandAsync($"commit -m {Quote("Remove " + folder)}", ContentAbsolutePath);
                await TryRemoteAsync("push", () => RunGitCommandAsync(
                    $"push origin HEAD:{Quote(branch)}",
                    ContentAbsolutePath));
            }
            else
            {
                // Folder is not on remote, but it may have been committed locally (never pushed).
                // If files exist in local HEAD, a plain --cached unstage would leave HEAD intact,
                // causing sparse-checkout to restore them the next time the folder is re-created.
                // Detect that case and commit the deletion locally (no push needed).
                var inLocalHead = !string.IsNullOrWhiteSpace(
                    await RunGitCommandAsync($"ls-tree -d HEAD -- {Quote(folder)}", ContentAbsolutePath, silent: true));

                Debug.Log($"[ContentRepo] DeleteRemoteFolderAsync '{folder}': inLocalHead={inLocalHead}");

                if (inLocalHead)
                {
                    // Ensure the folder is checked out so git rm can see the files.
                    var checkedOut = await IsFolderCheckedOutAsync(folder);
                    if (!checkedOut)
                    {
                        await RunGitCommandAsync($"sparse-checkout add {Quote(folder)}", ContentAbsolutePath);
                        await RunGitCommandAsync($"pull origin {Quote(branch)}", ContentAbsolutePath);
                    }

                    // Clear skip-worktree bits so git rm can process sparse-checkout-excluded files.
                    await ClearSkipWorktreeAsync(folder);

                    // git rm removes files from both index and working tree, then we commit locally.
                    await RunGitCommandAsync($"rm -rf -- {Quote(folder)}", ContentAbsolutePath);
                    foreach (var relPath in GetGroupRelativePaths(folder))
                    {
                        try { await RunGitCommandAsync($"rm -f -- {Quote(relPath)}", ContentAbsolutePath); }
                        catch { /* group file may not be committed — fine */ }
                    }
                    await RunGitCommandAsync($"commit -m {Quote("Remove " + folder)}", ContentAbsolutePath);
                    // No push — the branch diverges from remote but the folder content is gone from HEAD.
                }
                else
                {
                    // Files were only staged (never committed). Clear skip-worktree bits first
                    // so git rm --cached can process sparse-checkout-excluded files.
                    await ClearSkipWorktreeAsync(folder);

                    // Force-remove from index.
                    foreach (var path in new[] { folder }.Concat(GetGroupRelativePaths(folder)))
                    {
                        try { await RunGitCommandAsync($"rm -rf --cached -- {Quote(path)}", ContentAbsolutePath); }
                        catch (InvalidOperationException ex)
                            when (ex.Message.Contains("did not match") || ex.Message.Contains("pathspec"))
                        { /* nothing staged for this path — fine */ }
                    }
                }

                // Remove the folder from sparse-checkout so it won't be re-pulled.
                try
                {
                    var current = await GetCheckedOutFoldersAsync();
                    var remaining = current
                        .Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase))
                        .Prepend(GroupsFolderName)
                        .Select(Quote);
                    await RunGitCommandAsync($"sparse-checkout set {string.Join(" ", remaining)}", ContentAbsolutePath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ContentRepo] Could not update sparse-checkout while deleting '{folder}': {ex.Message}");
                }

                // Use git clean to remove any remaining untracked files from disk.
                // This covers: files never staged (invisible to git rm --cached), files
                // that git rm --cached removed from the index (making them untracked),
                // and files that System.IO couldn't delete due to Unity file locks.
                try { await RunGitCommandAsync($"clean -fdx -- {Quote(folder)}", ContentAbsolutePath); }
                catch (Exception ex) { Debug.LogWarning($"[ContentRepo] git clean failed for '{folder}': {ex.Message}"); }

                // Also clean up group files for this folder.
                foreach (var relPath in GetGroupRelativePaths(folder))
                {
                    var absPath = Path.Combine(ContentAbsolutePath, relPath);
                    if (File.Exists(absPath))
                    {
                        try { File.Delete(absPath); }
                        catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Failed to delete group file '{absPath}': {ex.Message}"); }
                    }
                }

                // Remove the .meta sidecar for the folder itself.
                var meta = Path.Combine(ContentAbsolutePath, folder) + ".meta";
                if (File.Exists(meta))
                {
                    try { File.Delete(meta); }
                    catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Failed to delete meta '{meta}': {ex.Message}"); }
                }

                Debug.Log($"[ContentRepo] Deleted local-only package '{folder}' (was never pushed to remote).");
            }

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

        // Clears git's skip-worktree bit on all indexed files under `folderOrPath`. Used both to let
        // `git rm` process sparse-excluded files and, on checkout, to make a folder's files trackable
        // so `git status` / `git add` see edits (see CheckOutFolderAsync / EnsureFolderInSparseCheckoutAsync).
        private static async Task ClearSkipWorktreeAsync(string folderOrPath)
        {
            try
            {
                var output = await RunGitCommandAsync(
                    $"ls-files -- {Quote(folderOrPath)}", ContentAbsolutePath, silent: true);
                if (string.IsNullOrWhiteSpace(output)) return;
                var files = output
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .Where(f => !string.IsNullOrEmpty(f))
                    .ToList();

                // Batch the paths so a large folder (e.g. thousands of voiceover clips) never blows
                // past the OS command-line length limit in a single update-index invocation.
                const int batchSize = 100;
                for (var i = 0; i < files.Count; i += batchSize)
                {
                    var batch = files.Skip(i).Take(batchSize);
                    await RunGitCommandAsync(
                        $"update-index --no-skip-worktree -- {string.Join(" ", batch.Select(Quote))}",
                        ContentAbsolutePath);
                }
            }
            catch { /* best-effort — if this fails, the caller's operation may still succeed */ }
        }

        // Returns repo-relative paths for all group + schema files that belong to `folder` and exist on disk.
        // Matches "<folder>.asset", "<folder>.asset.meta", and any "<folder>_schema_*" variants.
        private static IEnumerable<string> GetGroupRelativePaths(string folder)
        {
            var groupsDir = Path.Combine(ContentAbsolutePath, GroupsFolderName);
            if (!Directory.Exists(groupsDir)) yield break;

            var exactPrefix  = folder + ".";          // <folder>.asset  / <folder>.asset.meta
            var schemaPrefix = folder + "_schema_";   // <folder>_schema_*.asset (if Unity writes them separately)

            foreach (var absPath in Directory.GetFiles(groupsDir))
            {
                var name = Path.GetFileName(absPath);
                if (name.StartsWith(exactPrefix,  StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(schemaPrefix, StringComparison.OrdinalIgnoreCase))
                    yield return $"{GroupsFolderName}/{name}";
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
            if (folder.Contains("..") || folder.IndexOfAny(new[] { '\n', '\r', '\0', ' ', '"', '/', '\\' }) >= 0)
                throw new ArgumentException("Invalid folder name. Use only letters, digits, hyphens and underscores.", nameof(folder));
        }

        private static void NotifyChange()
        {
            try { OnStateChanged?.Invoke(); }
            catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
        }
    }
}
