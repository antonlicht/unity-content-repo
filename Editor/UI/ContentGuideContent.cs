using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// The topics of the Content Instructions window. The package ships generic topics in Editor/Guide/Guide.md. A
    /// project adds topics, replaces or extends the package's ones, and fills in named values with files called
    /// ContentRepoGuide.md anywhere under Assets. Both use the small Markdown dialect described in the README.
    /// </summary>
    internal static class ContentGuideContent
    {
        internal const string BuiltInPath = "Packages/com.antonlicht.content-repo/Editor/Guide/Guide.md";
        internal const string ProjectFileName = "ContentRepoGuide.md";
        private const string ValuesId = "values";

        internal enum BlockKind { Paragraph, Heading, Bullet, Table, Note, Link, Stage }

        internal sealed class Step
        {
            public string Icon;
            public bool Button;
            public string Title;
            public string Text;
            public string Tip;
        }

        internal sealed class Block
        {
            public BlockKind Kind;
            public string Text;
            public string Target;
            public List<string[]> Rows;
            public string Stage;
            public string StageName;
            public string Audience;
            public string Move;
            public List<Step> Steps;
        }

        internal sealed class Topic
        {
            public string Id;
            public string Title;
            public string Icon;
            public List<Block> Blocks = new();
        }

        internal sealed class Guide
        {
            public List<Topic> Topics = new();
            public List<string> ProjectFiles = new();
            public List<string> Problems = new();

            public Topic Find(string id) => Topics.FirstOrDefault(topic => string.Equals(topic.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class Section
        {
            public string Source;
            public Dictionary<string, string> Meta = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Lines = new();
        }

        private sealed class Draft
        {
            public string Id;
            public string Title;
            public string Icon;
            public List<string> Lines = new();
        }

        private static readonly Regex LinkLine = new(@"^\[([^\]]+)\]\(#([\w-]+)\)$");
        private static readonly Regex Value = new(@"\{\{\s*([\w-]+)\s*(?::\s*(.*?))?\}\}");
        private static readonly Regex TableSeparator = new(@"^\|[\s:\-|]+\|$");

        public static Guide Load()
        {
            var guide = new Guide();
            var sections = new List<Section>();
            var builtIn = AssetDatabase.LoadAssetAtPath<TextAsset>(BuiltInPath);
            if (builtIn != null) sections.AddRange(Split(builtIn.text, BuiltInPath));
            else guide.Problems.Add($"Could not load {BuiltInPath}.");
            foreach (var path in AssetDatabase.FindAssets("ContentRepoGuide", new[] { "Assets" })
                         .Select(AssetDatabase.GUIDToAssetPath)
                         .Where(path => path.EndsWith("/" + ProjectFileName, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) continue;
                guide.ProjectFiles.Add(path);
                sections.AddRange(Split(asset.text, path));
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var drafts = new List<Draft>();
            foreach (var section in sections)
            {
                if (!section.Meta.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
                {
                    if (section.Meta.Count > 0 || section.Lines.Any(line => line.Trim().Length > 0))
                        guide.Problems.Add($"{section.Source}: a section without an id was skipped.");
                    continue;
                }
                if (id.Equals(ValuesId, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in section.Lines)
                    {
                        var colon = line.IndexOf(':');
                        if (colon > 0) values[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                    }
                    continue;
                }
                Merge(drafts, section, id.Trim(), guide);
            }

            foreach (var draft in drafts)
                guide.Topics.Add(new Topic
                {
                    Id = draft.Id,
                    Title = string.IsNullOrWhiteSpace(draft.Title) ? draft.Id : draft.Title,
                    Icon = draft.Icon,
                    Blocks = Parse(draft.Lines.Select(line => Fill(line, values)).ToList()),
                });
            return guide;
        }

        private static void Merge(List<Draft> drafts, Section section, string id, Guide guide)
        {
            section.Meta.TryGetValue("mode", out var mode);
            mode = string.IsNullOrWhiteSpace(mode) ? "replace" : mode.Trim().ToLowerInvariant();
            var index = drafts.FindIndex(draft => draft.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var draft = index >= 0 ? drafts[index] : null;
            var hasBody = section.Lines.Any(line => line.Trim().Length > 0);

            if (mode == "remove")
            {
                if (draft != null) drafts.RemoveAt(index);
                return;
            }
            if (mode != "replace" && mode != "append" && mode != "prepend")
            {
                guide.Problems.Add($"{section.Source}: '{id}' has an unknown mode '{mode}'.");
                return;
            }
            if (draft == null)
            {
                draft = new Draft { Id = id, Lines = section.Lines.ToList() };
                drafts.Add(draft);
            }
            else if (mode == "replace" && hasBody) draft.Lines = section.Lines.ToList();
            else if (mode == "append") draft.Lines.AddRange(section.Lines.Prepend(""));
            else if (mode == "prepend") draft.Lines.InsertRange(0, section.Lines.Append(""));

            if (section.Meta.TryGetValue("title", out var title)) draft.Title = title.Trim();
            if (section.Meta.TryGetValue("icon", out var icon)) draft.Icon = icon.Trim();
            if (!section.Meta.TryGetValue("after", out var after)) return;
            var anchor = drafts.FirstOrDefault(other => other != draft && other.Id.Equals(after.Trim(), StringComparison.OrdinalIgnoreCase));
            if (anchor == null)
            {
                guide.Problems.Add($"{section.Source}: '{id}' should come after '{after.Trim()}', but there is no such topic.");
                return;
            }
            drafts.Remove(draft);
            drafts.Insert(drafts.IndexOf(anchor) + 1, draft);
        }

        private static IEnumerable<Section> Split(string text, string source)
        {
            var current = new Section { Source = source };
            var inMeta = false;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Trim() == "---")
                {
                    if (!inMeta)
                    {
                        if (current.Meta.Count > 0 || current.Lines.Any(line => line.Trim().Length > 0)) yield return current;
                        current = new Section { Source = source };
                    }
                    inMeta = !inMeta;
                    continue;
                }
                if (inMeta)
                {
                    var colon = raw.IndexOf(':');
                    if (colon > 0) current.Meta[raw.Substring(0, colon).Trim()] = raw.Substring(colon + 1).Trim();
                }
                else current.Lines.Add(raw.TrimEnd());
            }
            if (current.Meta.Count > 0 || current.Lines.Any(line => line.Trim().Length > 0)) yield return current;
        }

        private static string Fill(string line, Dictionary<string, string> values) =>
            Value.Replace(line, match => values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Groups[2].Value);

        private static List<Block> Parse(List<string> lines)
        {
            var blocks = new List<Block>();
            var paragraph = new StringBuilder();

            void Flush()
            {
                if (paragraph.Length == 0) return;
                blocks.Add(new Block { Kind = BlockKind.Paragraph, Text = Format(paragraph.ToString()) });
                paragraph.Clear();
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) { Flush(); continue; }

                if (line.StartsWith(":::", StringComparison.Ordinal))
                {
                    Flush();
                    var header = line.Substring(3).Trim();
                    if (!header.StartsWith("stage", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = header.Substring(5).Split('|').Select(part => part.Trim()).ToArray();
                    var stage = new Block
                    {
                        Kind = BlockKind.Stage,
                        Stage = parts.ElementAtOrDefault(0)?.ToLowerInvariant(),
                        StageName = parts.ElementAtOrDefault(1) ?? "",
                        Audience = Format(parts.ElementAtOrDefault(2) ?? ""),
                        Move = parts.ElementAtOrDefault(3),
                        Steps = new List<Step>(),
                    };
                    for (i++; i < lines.Count && lines[i].Trim() != ":::"; i++)
                    {
                        var stepLine = lines[i].Trim();
                        var colon = stepLine.IndexOf(':');
                        if (colon < 0) continue;
                        var kind = stepLine.Substring(0, colon).Trim().ToLowerInvariant();
                        if (kind != "button" && kind != "step") continue;
                        var fields = stepLine.Substring(colon + 1).Split('|').Select(field => field.Trim()).ToArray();
                        stage.Steps.Add(new Step
                        {
                            Button = kind == "button",
                            Icon = fields.ElementAtOrDefault(0),
                            Title = Format(fields.ElementAtOrDefault(1) ?? ""),
                            Text = Format(fields.ElementAtOrDefault(2) ?? ""),
                            Tip = string.IsNullOrEmpty(fields.ElementAtOrDefault(3)) ? null : Format(fields[3]),
                        });
                    }
                    blocks.Add(stage);
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush();
                    blocks.Add(new Block { Kind = BlockKind.Heading, Text = Format(line.Substring(3).Trim()) });
                    continue;
                }

                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                {
                    Flush();
                    var bullet = new StringBuilder(line.Substring(2).Trim());
                    while (i + 1 < lines.Count && lines[i + 1].StartsWith("  ", StringComparison.Ordinal) && lines[i + 1].Trim().Length > 0)
                        bullet.Append(' ').Append(lines[++i].Trim());
                    blocks.Add(new Block { Kind = BlockKind.Bullet, Text = Format(bullet.ToString()) });
                    continue;
                }

                if (line.StartsWith("|", StringComparison.Ordinal))
                {
                    Flush();
                    var rows = new List<string[]>();
                    for (; i < lines.Count && lines[i].Trim().StartsWith("|", StringComparison.Ordinal); i++)
                    {
                        var row = lines[i].Trim();
                        if (TableSeparator.IsMatch(row)) continue;
                        rows.Add(row.Trim('|').Split('|').Select(cell => Format(cell.Trim())).ToArray());
                    }
                    i--;
                    if (rows.Count > 0) blocks.Add(new Block { Kind = BlockKind.Table, Rows = rows });
                    continue;
                }

                if (line.StartsWith(">", StringComparison.Ordinal))
                {
                    Flush();
                    var note = new StringBuilder();
                    string target = null;
                    for (; i < lines.Count && lines[i].Trim().StartsWith(">", StringComparison.Ordinal); i++)
                    {
                        var part = lines[i].Trim().Substring(1).Trim();
                        var link = LinkLine.Match(part);
                        if (link.Success) { target = link.Groups[2].Value; continue; }
                        if (part.Length > 0) note.Append(note.Length > 0 ? " " : "").Append(part);
                    }
                    i--;
                    blocks.Add(new Block { Kind = BlockKind.Note, Text = Format(note.ToString()), Target = target });
                    continue;
                }

                var only = LinkLine.Match(line);
                if (only.Success)
                {
                    Flush();
                    blocks.Add(new Block { Kind = BlockKind.Link, Text = only.Groups[1].Value, Target = only.Groups[2].Value });
                    continue;
                }

                paragraph.Append(paragraph.Length > 0 ? " " : "").Append(line);
            }
            Flush();
            return blocks;
        }

        /// <summary>Turns the inline Markdown of the dialect into Unity rich text.</summary>
        internal static string Format(string text)
        {
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");
            text = Regex.Replace(text, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "<i>$1</i>");
            text = Regex.Replace(text, @"`(.+?)`", "<b>$1</b>");
            text = Regex.Replace(text, @"\[([^\]]+)\]\(#[\w-]+\)", match => $"<color=#{ColorUtility.ToHtmlStringRGB(LinkColor)}>{match.Groups[1].Value}</color>");
            return Regex.Replace(text, @"<(copy|team|test|live|warn|error)>(.*?)</\1>",
                match => $"<color=#{ColorUtility.ToHtmlStringRGB(Tint(match.Groups[1].Value))}><b>{match.Groups[2].Value}</b></color>");
        }

        private static bool Pro => EditorGUIUtility.isProSkin;

        internal static Color LinkColor => Pro ? new Color(0.48f, 0.68f, 1f) : new Color(0.1f, 0.35f, 0.8f);

        internal static Color Tint(string name) => name switch
        {
            "team" => Pro ? new Color(0.72f, 0.58f, 0.96f) : new Color(0.46f, 0.30f, 0.74f),
            "test" => Pro ? new Color(0.31f, 0.78f, 0.39f) : new Color(0.10f, 0.50f, 0.20f),
            "live" => Pro ? new Color(0.35f, 0.59f, 0.94f) : new Color(0.13f, 0.38f, 0.78f),
            "warn" => Pro ? new Color(1f, 0.80f, 0.43f) : new Color(0.62f, 0.40f, 0f),
            "error" => Pro ? new Color(1f, 0.55f, 0.52f) : new Color(0.75f, 0.15f, 0.12f),
            _ => Pro ? new Color(0.68f, 0.68f, 0.68f) : new Color(0.38f, 0.38f, 0.38f),
        };
    }
}
