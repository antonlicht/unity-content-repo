using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ContentRepo.Editor
{
    // Stored in ProjectSettings but intentionally not surfaced in the settings UI —
    // it is managed via the Build tab in the Content Browser window.
    [FilePath("ProjectSettings/ContentRepoGeneration.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ContentRepoGenerationSettings : ScriptableSingleton<ContentRepoGenerationSettings>
    {
        [SerializeField] private string generation = "gen/1";
        [SerializeField] private string unityVersionAtGeneration = "";

        public string Generation => generation;
        public string UnityVersionAtGeneration => unityVersionAtGeneration;

        public void SetGeneration(string newGeneration)
        {
            generation = newGeneration;
            unityVersionAtGeneration = Application.unityVersion;
            Save(true);
        }

        // Called when the user confirms they've bumped Unity and wants to auto-increment.
        public void BumpGeneration()
        {
            // Parse "gen/N" -> "gen/N+1", fallback to appending _2 on unexpected formats.
            var slash = generation.LastIndexOf('/');
            if (slash >= 0 && int.TryParse(generation.Substring(slash + 1), out var n))
                SetGeneration(generation.Substring(0, slash + 1) + (n + 1));
            else
                SetGeneration(generation + "_2");
        }

        public enum VersionChangeKind { None, PatchOnly, MinorOrMajor }

        // Compare stored Unity version against the running editor version.
        // Returns None when stored version is empty (first-time setup).
        public VersionChangeKind CheckUnityVersionChange()
        {
            if (string.IsNullOrWhiteSpace(unityVersionAtGeneration))
                return VersionChangeKind.None;

            var current = Application.unityVersion;
            if (current == unityVersionAtGeneration)
                return VersionChangeKind.None;

            // Unity version format: 6000.0.23f1 — compare major.minor segments only.
            if (TryExtractMajorMinor(unityVersionAtGeneration, out var oldMajor, out var oldMinor) &&
                TryExtractMajorMinor(current, out var newMajor, out var newMinor))
            {
                if (oldMajor == newMajor && oldMinor == newMinor)
                    return VersionChangeKind.PatchOnly;
            }

            return VersionChangeKind.MinorOrMajor;
        }

        // Records the current Unity version without changing the generation number.
        // Use after a patch update to silence the mismatch warning.
        public void AcknowledgeUnityVersion()
        {
            unityVersionAtGeneration = Application.unityVersion;
            Save(true);
        }

        private static bool TryExtractMajorMinor(string version, out int major, out int minor)
        {
            major = minor = 0;
            var parts = version.Split('.');
            return parts.Length >= 2 &&
                   int.TryParse(parts[0], out major) &&
                   int.TryParse(parts[1], out minor);
        }
    }
}
