using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ContentRepo.Editor
{
    [FilePath("ProjectSettings/ContentManifestCodeGen.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ContentManifestCodeGenSettings : ScriptableSingleton<ContentManifestCodeGenSettings>
    {
        [SerializeField] private string generatedOutputFolder = "Assets/Generated";

        public string GeneratedOutputFolder
        {
            get => generatedOutputFolder;
            set { generatedOutputFolder = value; Save(true); }
        }

        public void Persist() => Save(true);
    }

    internal static class ContentManifestCodeGenSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Content Repo/Code Gen", SettingsScope.Project)
            {
                label = "Code Gen",
                keywords = new HashSet<string>(new[] { "content", "manifest", "codegen", "generate" }),
                activateHandler = (_, root) =>
                {
                    var settings = ContentManifestCodeGenSettings.instance;
                    settings.hideFlags &= ~HideFlags.NotEditable;

                    var so = new SerializedObject(settings);

                    var container = new VisualElement
                    {
                        style =
                        {
                            paddingLeft = 10, paddingRight = 10, paddingTop = 10, paddingBottom = 10,
                        }
                    };

                    container.Add(new Label("Content Repo → Code Gen")
                    {
                        style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 8 }
                    });

                    container.Add(new PropertyField(so.FindProperty("generatedOutputFolder"), "Generated Output Folder"));

                    var generateButton = new Button(() => ContentManifestCodeGenerator.Generate())
                    {
                        text = "Generate ContentManifestConfig",
                        style = { marginTop = 10 }
                    };
                    container.Add(generateButton);

                    container.Bind(so);
                    container.TrackSerializedObjectValue(so, _ => settings.Persist());

                    root.Add(container);
                }
            };
        }
    }
}
