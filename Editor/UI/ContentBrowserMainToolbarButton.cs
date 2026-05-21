using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace ContentRepo.Editor
{
    internal static class ContentBrowserMainToolbarButton
    {
        [MainToolbarElement("Content Repo/Content Browser",
            defaultDockPosition = MainToolbarDockPosition.Left)]
        public static MainToolbarElement CreateButton()
        {
            var icon    = AssetDatabase.LoadAssetAtPath<Texture2D>(
                              "Packages/com.antonlicht.content-repo/Editor/UI/Icons/monitor-cloud.png");
            var content = new MainToolbarContent(icon) { tooltip = "Content Browser" };
            return new MainToolbarButton(content, ContentRepoWindow.ShowWindow);
        }
    }
}
