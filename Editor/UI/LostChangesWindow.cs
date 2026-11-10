using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Shows what a merge dropped from a content package: the commits whose work is missing, the files to restore
    /// grouped by folder, and the files both sides changed, with a button that restores the dropped files into this
    /// clone as uncommitted changes.
    /// </summary>
    internal sealed class LostChangesWindow : EditorWindow
    {
        private ContentLossDetails details;
        private bool checkedOut;
        private int unpulled;
        private Func<ContentLossDetails, bool> restore;
        private List<(string Folder, List<ContentLostFile> Files)> folders;
        private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
        private Vector2 scroll;

        /// <param name="restore">Starts the restore; returns false when it cannot start right now.</param>
        internal static void Show(ContentLossDetails details, bool checkedOut, int unpulled, Func<ContentLossDetails, bool> restore)
        {
            var window = GetWindow<LostChangesWindow>(true, $"Lost changes: {details.Package}", true);
            window.titleContent = new GUIContent($"Lost changes: {details.Package}");
            window.scroll = Vector2.zero;
            window.details = details;
            window.checkedOut = checkedOut;
            window.unpulled = unpulled;
            window.restore = restore;
            window.folders = details.Files
                .GroupBy(file => FolderOf(details.Package, file.Path))
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (group.Key, group.ToList()))
                .ToList();
            window.expanded.Clear();
            window.minSize = new Vector2(520, 420);
            window.Show();
        }

        private void OnGUI()
        {
            if (details == null || folders == null)
            {
                EditorGUILayout.HelpBox("Nothing to show. Open Lost changes again from the package row in the Content Browser.", MessageType.Info);
                return;
            }

            var restored = details.RestoredLocallyCount;
            var pending = details.PendingCount;

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                $"Merge {details.Merge}\n" +
                $"left out changes to '{details.Package}' that a normal merge of its parents keeps. " +
                $"{details.Files.Count + details.Skipped.Count} file(s) are still in the state that merge left them in on {ContentSyncApi.RemoteRef}." +
                (restored > 0 ? $"\n{restored} of them are already restored in this copy but not pushed yet." : ""),
                MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            Section($"Commits since the merge base on the side whose changes were dropped ({details.DroppedCommits.Count})");
            foreach (var commit in details.DroppedCommits)
                EditorGUILayout.LabelField(commit.ToString(), EditorStyles.miniLabel);

            EditorGUILayout.Space(8);
            Section($"Files to restore ({details.Files.Count})");
            foreach (var (folder, files) in folders)
            {
                var deletes = files.Count(file => file.Delete);
                var done = files.Count(file => file.RestoredLocally);
                var summary = $"{folder}   {files.Count - deletes} to restore" + (deletes > 0 ? $", {deletes} to delete" : "") +
                              (done > 0 ? $"   ({done} done)" : "");
                var open = EditorGUILayout.Foldout(expanded.Contains(folder), summary, true);
                if (open) expanded.Add(folder);
                else expanded.Remove(folder);
                if (!open) continue;
                EditorGUI.indentLevel++;
                foreach (var file in files)
                    EditorGUILayout.LabelField($"{(file.RestoredLocally ? "✓ " : "")}{(file.Delete ? "delete  " : "restore ")}{file.Path}", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            if (details.Skipped.Count > 0)
            {
                EditorGUILayout.Space(8);
                Section($"Left as they are ({details.Skipped.Count})");
                foreach (var file in details.Skipped)
                    EditorGUILayout.LabelField($"{file.Path}   ({file.Reason})", EditorStyles.miniLabel);
            }

            if (details.ChangedOnBothSides.Count > 0)
            {
                EditorGUILayout.Space(8);
                Section($"Changed on both sides, not restored ({details.ChangedOnBothSides.Count})");
                EditorGUILayout.HelpBox(
                    "Both sides of the merge changed these files and the merge kept only one side's version, so the other side's " +
                    "change to them is missing. Restoring cannot combine the two: merge them by hand in your git client, or keep " +
                    "them as they are. The package's group file is updated by Unity after a restore and by the next build, which " +
                    "adds an entry for every asset in the folder.",
                    MessageType.Info);
                foreach (var path in details.ChangedOnBothSides)
                    EditorGUILayout.LabelField(path, EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(!checkedOut ? $"Check out '{details.Package}' to restore its lost changes."
                    : !details.ContainsMerge ? $"Pull first: this copy does not contain merge {details.Merge?.Sha} yet, so its files are " +
                                               "still the versions from before the merge. After pulling, restore here."
                    : unpulled > 0 ? $"Pull first: this copy of '{details.Package}' is missing {unpulled} commit(s) from the remote, " +
                                     "so a restore would apply to an old state."
                    : "Restore writes the dropped files into your copy as uncommitted changes, and Unity then updates the " +
                      "package's group file to match. Check them (open the scenes, play through the content), then commit and push " +
                      "everything from the package row. Build, upload and promote afterwards.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy file list", GUILayout.Width(110)))
                EditorGUIUtility.systemCopyBuffer = string.Join("\n",
                    details.Files.Select(file => $"{(file.Delete ? "delete" : "restore")}\t{file.Path}")
                        .Concat(details.Skipped.Select(file => $"left as is\t{file.Path}\t{file.Reason}"))
                        .Concat(details.ChangedOnBothSides.Select(path => $"both sides\t{path}")));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(80))) Close();
            using (new EditorGUI.DisabledScope(!checkedOut || !details.ContainsMerge || unpulled > 0 || pending == 0))
            {
                var label = pending == 0 ? "All restored" : pending == details.Files.Count ? $"Restore {pending} files" : $"Restore {pending} remaining";
                if (GUILayout.Button(label, GUILayout.Width(170)) && Confirm(pending))
                {
                    if (restore != null && restore(details)) Close();
                    else EditorUtility.DisplayDialog("Restore lost changes",
                        "The Content Browser is busy with another operation. Wait until it has finished, then click Restore again.", "OK");
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        private bool Confirm(int pending) => EditorUtility.DisplayDialog("Restore lost changes",
            $"Restore {pending} file(s) of '{details.Package}' that merge {details.Merge?.Sha} dropped?\n\n" +
            "They are written into your copy and staged, but nothing is committed or pushed. They show up as changes on the " +
            "package row: check them, then commit and push.",
            "Restore", "Cancel");

        private static void Section(string title) => EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

        private static string FolderOf(string package, string path)
        {
            var parts = path.Split('/');
            if (parts.Length <= 2) return parts.Length == 2 && parts[0] != package ? parts[0] : $"{package} (top level)";
            return parts[0] == package ? string.Join("/", parts.Skip(1).Take(Math.Min(2, parts.Length - 2))) : parts[0];
        }
    }
}
