using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Explains in plain words what a merge threw away from a content package and whose work it was, and restores the
    /// lost files into this copy as uncommitted changes.
    /// </summary>
    internal sealed class LostChangesWindow : EditorWindow
    {
        /// <summary>What the buttons do. Each returns false when it cannot start right now (the Content Browser is busy).</summary>
        internal sealed class Actions
        {
            public Func<bool> Pull;
            public Func<ContentLossDetails, bool> Restore;
            public Func<ContentLossDetails, bool> Undo;
        }

        private ContentLossDetails details;
        private bool checkedOut;
        private int unpulled;
        private Actions actions;
        private List<(string Folder, List<ContentLostFile> Files)> folders;
        private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
        private bool showFiles;
        private Vector2 scroll;
        [NonSerialized] private GUIStyle headline;
        [NonSerialized] private GUIStyle body;
        private static readonly HashSet<string> Opening = new(StringComparer.Ordinal);

        internal static void Show(ContentLossDetails details, bool checkedOut, int unpulled, Actions actions)
        {
            var window = GetWindow<LostChangesWindow>(true, $"Lost work: {details.Package}", true);
            window.titleContent = new GUIContent($"Lost work: {details.Package}");
            window.scroll = Vector2.zero;
            window.details = details;
            window.checkedOut = checkedOut;
            window.unpulled = unpulled;
            window.actions = actions;
            window.folders = details.Files
                .GroupBy(file => FolderOf(details.Package, file.Path))
                .OrderByDescending(group => group.Count())
                .Select(group => (group.Key, group.ToList()))
                .ToList();
            window.expanded.Clear();
            window.showFiles = false;
            window.minSize = new Vector2(480, 360);
            window.Show();
        }

        private void OnGUI()
        {
            if (details == null || folders == null)
            {
                EditorGUILayout.HelpBox("Open this again from the package's red badge in the Content Browser.", MessageType.Info);
                return;
            }
            headline ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, wordWrap = true };
            body ??= new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };

            var total = details.Files.Count + details.Skipped.Count;
            var pending = details.PendingCount;
            var deletions = details.Files.Count(file => file.Delete);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField($"Work was lost in {details.Package}", headline);
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                $"On {Day(details.Merge?.Date)}, a merge by <b>{details.Merge?.Author}</b> replaced newer files with older copies. " +
                $"{total} files are affected. Nothing is gone for good: Restore puts the newer work back." +
                (deletions > 0 ? $" {Files(deletions)} had been deleted in that work and the merge brought them back; Restore deletes them again." : ""), body);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Whose work was lost", EditorStyles.boldLabel);
            foreach (var work in details.LostWork)
                EditorGUILayout.LabelField(
                    $"<b>{work.Commit.Author}</b>, {Day(work.Commit.Date)}: \"{work.Commit.Subject}\"   <color=#999999>{Files(work.Files)}</color>", body);
            if (details.UnattributedFiles > 0)
                EditorGUILayout.LabelField($"Other changes   <color=#999999>{Files(details.UnattributedFiles)}</color>", body);

            EditorGUILayout.Space(8);
            showFiles = EditorGUILayout.Foldout(showFiles, $"Files ({details.Files.Count})", true);
            if (showFiles)
            {
                EditorGUI.indentLevel++;
                foreach (var (folder, files) in folders)
                {
                    var done = files.Count(file => file.RestoredLocally);
                    var open = EditorGUILayout.Foldout(expanded.Contains(folder),
                        $"{folder}   {Files(files.Count)}" + (done > 0 ? $", {done} already restored" : ""), true);
                    if (open) expanded.Add(folder);
                    else expanded.Remove(folder);
                    if (!open) continue;
                    EditorGUI.indentLevel++;
                    foreach (var file in files)
                    {
                        EditorGUILayout.BeginHorizontal();
                        var note = !file.Delete ? "" : file.RestoredLocally ? "   (deleted again)" : "   (Restore deletes this again)";
                        EditorGUILayout.LabelField($"{(file.RestoredLocally ? "✓ " : "")}{FileName(details.Package, file.Path)}{note}", EditorStyles.miniLabel);
                        OpenButton(file);
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }

            var manual = details.ChangedOnBothSides.Count + details.Skipped.Count;
            if (manual > 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField($"Not restored automatically ({manual})", EditorStyles.boldLabel);
                foreach (var file in details.ChangedOnBothSides)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"{FileName(details.Package, file.Path)}   <color=#999999>" +
                        (IsGroupFile(file.Path)
                            ? "changed in both versions; Unity updates it after Restore"
                            : "both versions changed it and the merge kept only one. Open shows the one it threw away; copy anything missing by hand") +
                        "</color>", body);
                    if (!IsGroupFile(file.Path)) OpenButton(file);
                    EditorGUILayout.EndHorizontal();
                }
                foreach (var file in details.Skipped)
                    EditorGUILayout.LabelField($"{FileName(details.Package, file.Path)}   <color=#999999>{file.Reason}</color>", body);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            var (message, canRestore) = Status(pending);
            EditorGUILayout.HelpBox(message, canRestore ? MessageType.None : MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Copy list", "Copy every file and its state, for example to send to a teammate."), GUILayout.Width(90)))
                EditorGUIUtility.systemCopyBuffer = string.Join("\n",
                    details.Files.Select(file => $"{(file.Delete ? "delete again" : "restore")}\t{file.Path}")
                        .Concat(details.Skipped.Select(file => $"left as is\t{file.Path}\t{file.Reason}"))
                        .Concat(details.ChangedOnBothSides.Select(file => $"changed in both versions\t{file.Path}")));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(80))) Close();
            if (checkedOut && details.UndoableCount > 0 && details.ContainsMerge
                && GUILayout.Button(new GUIContent("Undo restore", "Take the restored files out of your copy again. Files you edited since are kept."), GUILayout.Width(110))
                && EditorUtility.DisplayDialog("Undo restore", $"Take the {Files(details.UndoableCount)} you restored out of your copy again?\n\nFiles you edited since the restore are kept.", "Undo restore", "Cancel"))
                Run(actions?.Undo != null && actions.Undo(details));
            if (checkedOut && (!details.ContainsMerge || unpulled > 0))
            {
                if (GUILayout.Button("Pull now", GUILayout.Width(170))) Run(actions?.Pull != null && actions.Pull());
            }
            else
                using (new EditorGUI.DisabledScope(!canRestore))
                {
                    var label = pending == 0 ? "All restored" : pending == details.Files.Count ? $"Restore {pending} files" : $"Restore {pending} remaining";
                    if (GUILayout.Button(label, GUILayout.Width(170)) && Confirm(pending))
                        Run(actions?.Restore != null && actions.Restore(details));
                }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        private (string Message, bool CanRestore) Status(int pending)
        {
            if (!checkedOut) return ($"Check out {details.Package} first (blue folder button on its row), then open this again. " +
                                     "Open shows a lost file without checking anything out.", false);
            if (!details.ContainsMerge || unpulled > 0) return ("Your copy is not up to date. Pull now, then you can restore.", false);
            if (pending == 0 && details.UndoableCount == 0)
                return ("Everything is restored and committed on this computer, but not pushed yet. Push it with your git client, " +
                        "or ask for help.", false);
            if (pending == 0) return ("Everything is restored in your copy. Check it in Unity, then commit and push from the package row. " +
                                      "Undo restore takes it out again.", false);
            return ("Restore only changes your copy, so you can check the files in Unity first. Nothing is shared until you commit and push, " +
                    "and Undo restore takes them out again.", true);
        }

        private bool Confirm(int pending)
        {
            var deletes = details.Files.Count(file => file.Delete && !file.RestoredLocally);
            var puts = pending - deletes;
            var what = puts > 0 && deletes > 0 ? $"Put {Files(puts)} of {details.Package} back into your copy and delete {Files(deletes)} again?"
                : deletes > 0 ? $"Delete {Files(deletes)} of {details.Package} again in your copy?"
                : $"Put {Files(puts)} of {details.Package} back into your copy?";
            return EditorUtility.DisplayDialog("Restore lost work",
                $"{what}\n\nThey show up as changes on the package row. Nothing is shared with the team until you commit and push, " +
                "and Undo restore takes them out again.",
                "Restore", "Cancel");
        }

        private void Run(bool started)
        {
            if (started) Close();
            else EditorUtility.DisplayDialog(titleContent.text,
                "The Content Browser is busy with something else. Wait until it has finished, then try again.", "OK");
        }

        private static void OpenButton(ContentLostFile file)
        {
            if (file.Delete || string.IsNullOrEmpty(file.Blob) || file.Path.EndsWith(".meta", StringComparison.Ordinal)) return;
            using (new EditorGUI.DisabledScope(Opening.Contains(file.Path)))
                if (GUILayout.Button(new GUIContent("Open", "Open a copy of the lost version. Your own files do not change."), EditorStyles.miniButton, GUILayout.Width(44)))
                    OpenLostVersion(file);
        }

        private static async void OpenLostVersion(ContentLostFile file)
        {
            if (!Opening.Add(file.Path)) return;
            try { EditorUtility.OpenWithDefaultApp(await ContentGitApi.ExtractLostVersionAsync(file)); }
            catch (Exception ex) { EditorUtility.DisplayDialog("Open lost version", ex.Message, "OK"); }
            finally { Opening.Remove(file.Path); }
        }

        private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

        private static string Day(string date) =>
            DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : date;

        private static bool IsGroupFile(string path) => path.StartsWith(ContentGitApi.GroupsFolderName + "/", StringComparison.Ordinal);

        private static string FileName(string package, string path) =>
            path.StartsWith(package + "/", StringComparison.Ordinal) ? path.Substring(package.Length + 1) : path;

        private static string FolderOf(string package, string path)
        {
            var relative = FileName(package, path);
            var slash = relative.LastIndexOf('/');
            return slash < 0 ? "(top level)" : relative.Substring(0, slash);
        }
    }
}
