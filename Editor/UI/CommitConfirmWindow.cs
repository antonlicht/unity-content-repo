using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ContentRepo.Editor
{
    internal sealed class CommitConfirmWindow : EditorWindow
    {
        private string _message;
        private List<string> _files;
        private Vector2 _scroll;
        private Action<string> _onConfirm;

        internal static void Show(string message, List<string> files, Action<string> onConfirm)
        {
            var w = CreateInstance<CommitConfirmWindow>();
            w.titleContent = new GUIContent("Commit and Push");
            w._message  = message;
            w._files    = files;
            w._onConfirm = onConfirm;
            w.minSize = new Vector2(380, 240);
            w.maxSize = new Vector2(600, 500);
            w.ShowModal();
        }

        private void OnGUI()
        {
            var pad = new RectOffset(12, 12, 10, 10);
            GUILayout.BeginArea(new Rect(0, 0, position.width, position.height));
            GUILayout.Space(pad.top);

            // Commit message
            GUILayout.BeginHorizontal();
            GUILayout.Space(pad.left);
            GUILayout.Label("Commit message", EditorStyles.boldLabel);
            GUILayout.Space(pad.right);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Space(pad.left);
            _message = GUILayout.TextField(_message, GUILayout.ExpandWidth(true));
            GUILayout.Space(pad.right);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // File list header
            GUILayout.BeginHorizontal();
            GUILayout.Space(pad.left);
            GUILayout.Label($"Changes ({_files.Count} files)", EditorStyles.boldLabel);
            GUILayout.Space(pad.right);
            GUILayout.EndHorizontal();

            // Scrollable file list
            GUILayout.BeginHorizontal();
            GUILayout.Space(pad.left);
            var listHeight = Mathf.Clamp(_files.Count * 18f + 8f, 60f, position.height - 130f);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(listHeight), GUILayout.ExpandWidth(true));
            foreach (var f in _files)
                GUILayout.Label(f, EditorStyles.miniLabel);
            GUILayout.EndScrollView();
            GUILayout.Space(pad.right);
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();

            // Buttons
            GUILayout.BeginHorizontal();
            GUILayout.Space(pad.left);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                Close();
            GUILayout.Space(6);
            GUI.enabled = !string.IsNullOrWhiteSpace(_message);
            if (GUILayout.Button("Commit & Push", GUILayout.Width(110)))
            {
                var msg = _message;
                Close();
                _onConfirm?.Invoke(msg);
            }
            GUI.enabled = true;
            GUILayout.Space(pad.right);
            GUILayout.EndHorizontal();

            GUILayout.Space(pad.bottom);
            GUILayout.EndArea();
        }
    }
}
