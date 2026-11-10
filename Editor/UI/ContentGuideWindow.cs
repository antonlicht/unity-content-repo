using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using static ContentRepo.Editor.ContentGuideContent;

namespace ContentRepo.Editor
{
    /// <summary>
    /// A short, non-technical guide to working with content, split into topics picked from a sidebar. The topics come
    /// from <see cref="ContentGuideContent"/>: generic ones from the package, plus whatever the project adds or changes.
    /// Opened from the Instructions button in the Content Browser.
    /// </summary>
    internal sealed class ContentGuideWindow : EditorWindow
    {
        private const string IconFolder = "Packages/com.antonlicht.content-repo/Editor/UI/Icons/";
        private const float SidebarWidth = 210;
        private const int PagePadding = 18;
        private const float ScrollbarWidth = 14;

        [SerializeField] private string topicId;
        private Vector2 scroll;
        [NonSerialized] private Guide guide;
        [NonSerialized] private Color stageColor;
        [NonSerialized] private int stepNumber;

        [NonSerialized] private GUIStyle page, pageTitle, heading, body, tip, stepTitle;
        [NonSerialized] private GUIStyle stageHeader, number, chip, flowLabel, cell, headerCell;
        [NonSerialized] private GUIStyle sideTitle, sideItem, sideItemSelected, sideFooter, noteBox;
        private readonly Dictionary<string, GUIStyle> stageBoxes = new();
        private readonly List<Texture2D> textures = new();

        private static bool Pro => EditorGUIUtility.isProSkin;
        private float ContentWidth => position.width - SidebarWidth - PagePadding * 2 - ScrollbarWidth;

        [MenuItem("Window/Content Browser Instructions")]
        internal static void Open() => Open(null);

        internal static void Open(string topic)
        {
            var window = GetWindow<ContentGuideWindow>(false, "Content Instructions", true);
            window.titleContent = new GUIContent("Content Instructions", Icon("book-text"));
            window.minSize = new Vector2(720, 500);
            window.wantsMouseMove = true;
            window.guide = Load();
            if (topic != null) window.Select(topic);
            window.Show();
        }

        private void OnEnable() => guide = null;

        private void OnFocus()
        {
            guide = Load();
            Repaint();
        }

        private void OnDisable()
        {
            foreach (var texture in textures)
                if (texture != null) DestroyImmediate(texture);
            textures.Clear();
            stageBoxes.Clear();
            page = null;
        }

        private void Select(string id)
        {
            topicId = id;
            scroll = Vector2.zero;
            GUI.FocusControl(null);
            Repaint();
        }

        private void OnGUI()
        {
            EnsureStyles();
            guide ??= Load();
            if (Event.current.type == EventType.MouseMove) Repaint();
            var topic = guide.Find(topicId) ?? guide.Topics.FirstOrDefault();

            EditorGUI.DrawRect(new Rect(0, 0, SidebarWidth, position.height), Pro ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.82f, 0.82f, 0.82f));
            EditorGUI.DrawRect(new Rect(SidebarWidth - 1, 0, 1, position.height), Pro ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.6f, 0.6f, 0.6f));
            DrawSidebar(topic);

            GUILayout.BeginArea(new Rect(SidebarWidth, 0, position.width - SidebarWidth, position.height));
            scroll = EditorGUILayout.BeginScrollView(scroll);
            GUILayout.BeginVertical(page);
            foreach (var problem in guide.Problems) EditorGUILayout.HelpBox(problem, MessageType.Warning);
            if (topic != null) DrawTopic(topic);
            GUILayout.EndVertical();
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ── Topics ────────────────────────────────────────────────────────────

        private void DrawTopic(Topic topic)
        {
            GUILayout.Label(topic.Title, pageTitle);
            GUILayout.Space(4);
            stepNumber = 0;
            var stages = topic.Blocks.Where(block => block.Kind == BlockKind.Stage).ToList();
            Block previous = null;
            foreach (var block in topic.Blocks)
            {
                switch (block.Kind)
                {
                    case BlockKind.Paragraph:
                        Text(block.Text);
                        GUILayout.Space(8);
                        break;
                    case BlockKind.Heading:
                        Heading(block.Text);
                        break;
                    case BlockKind.Bullet:
                        Bullet(block.Text);
                        break;
                    case BlockKind.Table:
                        Table(block.Rows);
                        GUILayout.Space(8);
                        break;
                    case BlockKind.Note:
                        Note(block.Text, block.Target);
                        GUILayout.Space(8);
                        break;
                    case BlockKind.Link:
                        Link(block.Text, block.Target);
                        GUILayout.Space(6);
                        break;
                    case BlockKind.Stage:
                        if (block == stages[0] && stages.Count > 1)
                        {
                            FlowStrip(stages);
                            GUILayout.Space(14);
                        }
                        else if (previous?.Kind == BlockKind.Stage) Arrow();
                        StageBlock(block);
                        if (block == stages[stages.Count - 1]) GUILayout.Space(14);
                        break;
                }
                previous = block;
            }
        }

        // ── Building blocks ───────────────────────────────────────────────────

        private void DrawSidebar(Topic selectedTopic)
        {
            GUI.Label(new Rect(16, 14, SidebarWidth - 24, 22), "Instructions", sideTitle);
            var y = 48f;
            foreach (var topic in guide.Topics)
            {
                var row = new Rect(8, y, SidebarWidth - 16, 28);
                var selected = topic == selectedTopic;
                if (selected) EditorGUI.DrawRect(row, Pro ? new Color(0.17f, 0.36f, 0.53f) : new Color(0.23f, 0.45f, 0.69f));
                else if (row.Contains(Event.current.mousePosition)) EditorGUI.DrawRect(row, Pro ? new Color(0.24f, 0.24f, 0.24f) : new Color(0.74f, 0.74f, 0.74f));
                var image = Icon(topic.Icon);
                if (image != null)
                    GUI.DrawTexture(new Rect(row.x + 9, row.y + 6, 16, 16), image, ScaleMode.ScaleToFit, true, 0,
                        selected || Pro ? Color.white : new Color(0.2f, 0.2f, 0.2f), 0, 0);
                GUI.Label(new Rect(row.x + 33, row.y, row.width - 36, row.height), topic.Title, selected ? sideItemSelected : sideItem);
                EditorGUIUtility.AddCursorRect(row, MouseCursor.Link);
                if (GUI.Button(row, GUIContent.none, GUIStyle.none)) Select(topic.Id);
                y += 30;
            }

            if (guide.ProjectFiles.Count == 0) return;
            var footer = new Rect(16, position.height - 30, SidebarWidth - 24, 20);
            EditorGUIUtility.AddCursorRect(footer, MouseCursor.Link);
            if (GUI.Button(footer, new GUIContent("Edit this project's instructions", string.Join("\n", guide.ProjectFiles)), sideFooter))
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(guide.ProjectFiles[0]);
                EditorGUIUtility.PingObject(asset);
                AssetDatabase.OpenAsset(asset);
            }
        }

        private void Heading(string text)
        {
            GUILayout.Space(8);
            GUILayout.Label(text, heading);
            GUILayout.Space(4);
        }

        private void Text(string text) => GUILayout.Label(text, body);

        private void Bullet(string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("•", body, GUILayout.Width(12));
            GUILayout.Label(text, body);
            GUILayout.EndHorizontal();
            GUILayout.Space(3);
        }

        private void Link(string label, string target)
        {
            if (EditorGUILayout.LinkButton($"{label}  →")) Select(target);
        }

        private void Note(string text, string target)
        {
            GUILayout.BeginVertical(noteBox, GUILayout.Width(ContentWidth));
            if (text.Length > 0) GUILayout.Label(text, body);
            if (target != null)
            {
                GUILayout.Space(2);
                Link(guide.Find(target)?.Title ?? target, target);
            }
            GUILayout.EndVertical();
        }

        /// <summary>The stages in one line, with what moves a change from one to the next.</summary>
        private void FlowStrip(List<Block> stages)
        {
            const float gap = 58;
            var labels = stages.Select(stage => Colored(stage.StageName, Tint(stage.Stage))).ToList();
            var widths = labels.Select(label => chip.CalcSize(new GUIContent(label)).x).ToList();
            var total = widths.Sum() + gap * (stages.Count - 1);
            if (total > ContentWidth) return;

            var rect = GUILayoutUtility.GetRect(total, 40, GUILayout.Width(total), GUILayout.Height(40));
            var x = rect.x;
            for (var i = 0; i < stages.Count; i++)
            {
                if (i > 0)
                {
                    GUI.Label(new Rect(x, rect.y, gap, 14), stages[i].Move ?? "", flowLabel);
                    GUI.Label(new Rect(x, rect.y + 14, gap, 24), "→", flowLabel);
                    x += gap;
                }
                var color = Tint(stages[i].Stage);
                var box = new Rect(x, rect.y + 14, widths[i], 24);
                if (Event.current.type == EventType.Repaint)
                    GUI.DrawTexture(box, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(color.r, color.g, color.b, 0.22f), 0, 6);
                GUI.Label(box, labels[i], chip);
                x += widths[i];
            }
        }

        private void StageBlock(Block stage)
        {
            stageColor = Tint(stage.Stage);
            GUILayout.BeginVertical(StageBox(stage.Stage), GUILayout.Width(ContentWidth));
            GUILayout.Label($"{Colored(stage.StageName.ToUpperInvariant(), stageColor)}    {stage.Audience}", stageHeader);
            GUILayout.Space(8);
            foreach (var step in stage.Steps) DrawStep(step);
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                var rect = GUILayoutUtility.GetLastRect();
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 3, rect.height), stageColor);
            }
        }

        private void DrawStep(Step step)
        {
            stepNumber++;
            GUILayout.BeginHorizontal();
            var circle = GUILayoutUtility.GetRect(22, 22, GUILayout.Width(22), GUILayout.Height(22));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(circle, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, stageColor, 0, 11);
                number.Draw(circle, stepNumber.ToString(), false, false, false, false);
            }
            GUILayout.Space(8);
            var iconRect = GUILayoutUtility.GetRect(24, 24, GUILayout.Width(24), GUILayout.Height(24));
            var image = Icon(step.Icon);
            if (image != null && Event.current.type == EventType.Repaint)
            {
                if (step.Button)
                    GUI.DrawTexture(iconRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                        Pro ? new Color(1, 1, 1, 0.13f) : new Color(0, 0, 0, 0.12f), 0, 5);
                GUI.DrawTexture(new Rect(iconRect.x + 4, iconRect.y + 4, 16, 16), image, ScaleMode.ScaleToFit, true, 0,
                    Pro ? Color.white : new Color(0.2f, 0.2f, 0.2f), 0, 0);
            }
            GUILayout.Space(8);
            GUILayout.BeginVertical();
            GUILayout.Label(step.Title, stepTitle);
            if (step.Text.Length > 0) GUILayout.Label(step.Text, body);
            if (step.Tip != null) GUILayout.Label(step.Tip, tip);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(10);
        }

        private void Arrow()
        {
            var rect = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            var x = rect.x + 25;
            var color = Pro ? new Color(0.5f, 0.5f, 0.5f) : new Color(0.45f, 0.45f, 0.45f);
            EditorGUI.DrawRect(new Rect(x - 1, rect.y + 2, 2, rect.height - 9), color);
            GUI.Label(new Rect(x - 10, rect.yMax - 13, 20, 12), Colored("▼", color), flowLabel);
        }

        private void Table(List<string[]> rows)
        {
            var width = ContentWidth;
            var columns = rows.Max(row => row.Length);
            var weights = Enumerable.Range(0, columns)
                .Select(c => (float)Mathf.Clamp(rows.Max(row => c < row.Length ? Plain(row[c]).Length : 0), 8, 40))
                .ToArray();
            var minimums = Enumerable.Range(0, columns)
                .Select(c => rows.Max(row => c < row.Length ? LongestWord(row[c]) : 0) / width)
                .ToArray();
            var fractions = weights.Select(weight => weight / weights.Sum()).ToArray();
            var fixedColumns = Enumerable.Range(0, columns).Where(c => fractions[c] < minimums[c]).ToList();
            if (fixedColumns.Count > 0 && fixedColumns.Count < columns)
            {
                var rest = 1 - fixedColumns.Sum(c => minimums[c]);
                var flexible = Enumerable.Range(0, columns).Except(fixedColumns).Sum(c => weights[c]);
                for (var c = 0; c < columns; c++)
                    fractions[c] = fixedColumns.Contains(c) ? minimums[c] : weights[c] / flexible * rest;
            }
            for (var r = 0; r < rows.Count; r++)
            {
                var style = r == 0 ? headerCell : cell;
                var height = 0f;
                for (var c = 0; c < rows[r].Length; c++)
                    height = Mathf.Max(height, style.CalcHeight(new GUIContent(rows[r][c]), fractions[c] * width));
                var rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
                if (Event.current.type == EventType.Repaint)
                {
                    if (r == 0) EditorGUI.DrawRect(rect, Pro ? new Color(1, 1, 1, 0.08f) : new Color(0, 0, 0, 0.08f));
                    else if (r % 2 == 0) EditorGUI.DrawRect(rect, Pro ? new Color(1, 1, 1, 0.03f) : new Color(0, 0, 0, 0.03f));
                    EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), Pro ? new Color(1, 1, 1, 0.07f) : new Color(0, 0, 0, 0.1f));
                }
                var x = rect.x;
                for (var c = 0; c < rows[r].Length; c++)
                {
                    GUI.Label(new Rect(x, rect.y, fractions[c] * width, height), rows[r][c], style);
                    x += fractions[c] * width;
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string Plain(string richText) => Regex.Replace(richText, "<[^>]+>", "");

        private float LongestWord(string richText) =>
            Plain(richText).Split(' ').Select(word => headerCell.CalcSize(new GUIContent(word)).x).DefaultIfEmpty(0).Max();

        private static string Colored(string text, Color color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}><b>{text}</b></color>";

        private GUIStyle StageBox(string stage)
        {
            stage ??= "";
            if (stageBoxes.TryGetValue(stage, out var style)) return style;
            var color = Tint(stage);
            style = new GUIStyle
            {
                normal = { background = Fill(new Color(color.r, color.g, color.b, Pro ? 0.08f : 0.10f)) },
                padding = new RectOffset(14, 12, 10, 4),
            };
            stageBoxes[stage] = style;
            return style;
        }

        private Texture2D Fill(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            textures.Add(texture);
            return texture;
        }

        private void EnsureStyles()
        {
            if (page != null) return;
            page = new GUIStyle { padding = new RectOffset(PagePadding, PagePadding, 16, 20) };
            pageTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, wordWrap = true };
            heading = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, wordWrap = true, richText = true };
            body = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true, fontSize = 12 };
            tip = new GUIStyle(body) { fontSize = 11, normal = { textColor = Pro ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.38f, 0.38f, 0.38f) } };
            stepTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, richText = true };
            stageHeader = new GUIStyle(body) { fontSize = 11 };
            number = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter, fontSize = 11, padding = new RectOffset(0, 0, 0, 0),
                normal = { textColor = new Color(0.08f, 0.08f, 0.08f) },
            };
            chip = new GUIStyle(body) { alignment = TextAnchor.MiddleCenter, wordWrap = false, padding = new RectOffset(10, 10, 0, 0) };
            flowLabel = new GUIStyle(tip) { alignment = TextAnchor.MiddleCenter, wordWrap = false, richText = true };
            cell = new GUIStyle(body) { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(0, 0, 0, 0) };
            headerCell = new GUIStyle(cell) { fontStyle = FontStyle.Bold };
            sideTitle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 15 };
            sideItem = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, fontSize = 12 };
            sideItemSelected = new GUIStyle(sideItem) { normal = { textColor = Color.white }, fontStyle = FontStyle.Bold };
            sideFooter = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = LinkColor }, hover = { textColor = LinkColor } };
            noteBox = new GUIStyle
            {
                normal = { background = Fill(Pro ? new Color(1, 1, 1, 0.05f) : new Color(0, 0, 0, 0.05f)) },
                padding = new RectOffset(12, 12, 10, 10),
            };
        }

        private static Texture2D Icon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return AssetDatabase.LoadAssetAtPath<Texture2D>(name.Contains("/") ? name : $"{IconFolder}{name}.png");
        }
    }
}
