using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ContentRepo.Editor
{
    [FilePath("ProjectSettings/ContentRepo.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ContentRepoSettings : ScriptableSingleton<ContentRepoSettings>
    {
        [SerializeField] private string localPath = "Assets/Content";
        [SerializeField] private string remoteUrl = "";
        [SerializeField] private string branch = "main";

        public string LocalPath
        {
            get => localPath;
            set { localPath = value; Save(true); }
        }

        public string RemoteUrl
        {
            get => remoteUrl;
            set { remoteUrl = value; Save(true); }
        }

        public string Branch
        {
            get => branch;
            set { branch = value; Save(true); }
        }

        public void Persist() => Save(true);
    }

    internal static class ContentRepoSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Content Repo", SettingsScope.Project)
            {
                label = "Content Repo",
                keywords = new HashSet<string>(new[] { "content", "git", "sparse", "repo" }),
                activateHandler = (_, root) =>
                {
                    var settings = ContentRepoSettings.instance;
                    settings.hideFlags &= ~HideFlags.NotEditable;

                    var so = new SerializedObject(settings);

                    var container = new VisualElement
                    {
                        style =
                        {
                            paddingLeft = 10, paddingRight = 10, paddingTop = 10, paddingBottom = 10,
                        }
                    };

                    container.Add(new Label("Content Repo")
                    {
                        style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 8 }
                    });

                    var localField  = new PropertyField(so.FindProperty("localPath"),     "Local path");
                    var remoteField = new PropertyField(so.FindProperty("remoteUrl"),     "Remote URL");
                    var branchField = new PropertyField(so.FindProperty("branch"), "Branch");

                    container.Add(localField);
                    container.Add(remoteField);
                    container.Add(branchField);

                    container.Bind(so);
                    container.TrackSerializedObjectValue(so, _ => settings.Persist());

                    root.Add(container);
                }
            };
        }
    }
}
