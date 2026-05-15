using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ContentRepo.Editor
{
    // Kept in ContentBuildSettings so the Upload tab can reference it too.
    public enum BuildEnvironment
    {
        Staging,
        Production,
    }

    [FilePath("ProjectSettings/ContentRepoBuild.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ContentBuildSettings : ScriptableSingleton<ContentBuildSettings>
    {
        [SerializeField] private string addressablesProfileName = "Default";
        [SerializeField] private string remoteLoadPathVariableName = "RemoteLoadPath";
        [SerializeField] private string remoteBuildPathVariableName = "RemoteBuildPath";
        [SerializeField] private string buildOutputRoot = "Builds/Content";

        public string AddressablesProfileName => addressablesProfileName;
        public string RemoteLoadPathVariableName => remoteLoadPathVariableName;
        public string RemoteBuildPathVariableName => remoteBuildPathVariableName;
        public string BuildOutputRoot => buildOutputRoot;

        public void Persist() => Save(true);
    }

    internal static class ContentBuildSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Content Repo/Build", SettingsScope.Project)
            {
                label = "Build",
                keywords = new HashSet<string>(new[] { "content", "build", "addressables" }),
                activateHandler = (_, root) =>
                {
                    var settings = ContentBuildSettings.instance;
                    settings.hideFlags &= ~HideFlags.NotEditable;

                    var so = new SerializedObject(settings);
                    var container = new VisualElement
                    {
                        style = { paddingLeft = 10, paddingRight = 10, paddingTop = 10, paddingBottom = 10 }
                    };

                    container.Add(new Label("Content Repo — Build")
                    {
                        style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 8 }
                    });
                    container.Add(new PropertyField(so.FindProperty("addressablesProfileName"), "Addressables profile name"));
                    container.Add(new PropertyField(so.FindProperty("remoteLoadPathVariableName"), "Remote load path variable"));
                    container.Add(new PropertyField(so.FindProperty("remoteBuildPathVariableName"), "Remote build path variable"));
                    container.Add(new PropertyField(so.FindProperty("buildOutputRoot"), "Build output root"));

                    container.Add(new Label("Generation")
                    {
                        style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 12, marginBottom = 4 }
                    });

                    var genSettings = ContentRepoGenerationSettings.instance;
                    genSettings.hideFlags &= ~HideFlags.NotEditable;

                    var genLabel = new Label($"Current generation: {genSettings.Generation}  |  Unity at generation: {genSettings.UnityVersionAtGeneration}");
                    genLabel.style.whiteSpace = WhiteSpace.Normal;
                    container.Add(genLabel);

                    var bumpBtn = new Button(() =>
                    {
                        genSettings.BumpGeneration();
                        genLabel.text = $"Current generation: {genSettings.Generation}  |  Unity at generation: {genSettings.UnityVersionAtGeneration}";
                    }) { text = "Bump generation", style = { marginTop = 4, alignSelf = Align.FlexStart } };
                    container.Add(bumpBtn);

                    container.Bind(so);
                    container.TrackSerializedObjectValue(so, _ => settings.Persist());
                    root.Add(container);
                }
            };
        }
    }
}
