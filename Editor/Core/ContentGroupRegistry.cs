using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace ContentRepo.Editor
{
    /// <summary>
    /// Registers content package groups per machine. Teammates check out different packages, so which
    /// groups are registered is local state and must never reach the committed
    /// <c>AddressableAssetSettings.asset</c>. On every machine the registered content groups are exactly
    /// <c>_groups/&lt;pkg&gt;.asset</c> for each package in the sparse cone; they live in memory only and
    /// <see cref="ContentSettingsSaveGuard"/> strips them whenever the settings are saved. Groups of packages
    /// that are not checked out are never registered, so Addressables never purges or rewrites them.
    /// </summary>
    [InitializeOnLoad]
    public static class ContentGroupRegistry
    {
        private const double BuildPollSeconds = 0.5;
        private const int MaxSettingsRetries = 50;

        private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
        private static bool flushScheduled;
        private static bool reconcileScheduled;
        private static int buildScopes;
        private static int settingsRetries;

        internal static bool Flushing { get; private set; }
        internal static bool Quitting { get; private set; }

        static ContentGroupRegistry()
        {
            ScheduleReconcile();
            ContentGitApi.OnStateChanged += ScheduleReconcile;
            EditorApplication.quitting += () => Quitting = true;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode) ScheduleReconcile();
            };
        }

        public static string ContentRoot =>
            ContentRepoSettings.instance.LocalPath?.Replace('\\', '/').TrimEnd('/');

        public static string GroupsFolder => $"{ContentRoot}/{ContentGitApi.GroupsFolderName}";

        public static string CanonicalGroupPath(string package) => $"{GroupsFolder}/{package}.asset";

        public static bool IsBuilding => buildScopes > 0 || BuildPipeline.isBuildingPlayer;

        /// <summary>True for any group whose asset lives inside the content repo.</summary>
        public static bool IsContentGroup(AddressableAssetGroup group)
        {
            if (group == null) return false;
            var root = ContentRoot;
            if (string.IsNullOrEmpty(root)) return false;
            var path = AssetDatabase.GetAssetPath(group).Replace('\\', '/');
            return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Packages checked out on this machine: in the sparse cone and present on disk.</summary>
        public static List<string> ActivePackages()
        {
            var root = ContentRoot;
            if (string.IsNullOrEmpty(root) || !AssetDatabase.IsValidFolder(root)) return new List<string>();
            return ContentGitApi.ReadConeFolders()
                .Where(p => AssetDatabase.IsValidFolder($"{root}/{p}"))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool IsActive(string package) =>
            ActivePackages().Any(p => p.Equals(package, StringComparison.OrdinalIgnoreCase));

        public static void ScheduleReconcile()
        {
            if (reconcileScheduled) return;
            reconcileScheduled = true;
            EditorApplication.delayCall += () =>
            {
                reconcileScheduled = false;
                Reconcile();
            };
        }

        /// <summary>
        /// Brings the in-memory registration in line with this machine's checkout: registers the canonical
        /// group of every active package and unregisters every other content group. Never dirties the
        /// settings asset.
        /// </summary>
        public static void Reconcile()
        {
            if (string.IsNullOrEmpty(ContentRoot)) return;
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                if (settingsRetries++ < MaxSettingsRetries) ScheduleReconcile();
                return;
            }
            settingsRetries = 0;

            var active = new HashSet<string>(ActivePackages(), StringComparer.OrdinalIgnoreCase);
            foreach (var group in settings.groups.ToList())
            {
                if (!IsContentGroup(group)) continue;
                var package = PackageOfCanonicalGroup(group);
                if (package != null && active.Contains(package)) continue;
                Unregister(settings, group);
            }

            foreach (var package in active)
            {
                var canonical = AssetDatabase.LoadAssetAtPath<AddressableAssetGroup>(CanonicalGroupPath(package));
                if (canonical == null || settings.groups.Contains(canonical)) continue;
                var twin = FindTwin(settings, package, canonical);
                if (twin != null)
                {
                    ReportOnce($"twin:{package}", () => Debug.LogError(TwinMessage(package, twin)));
                    continue;
                }
                if (IsDirtyInMemory(canonical)) canonical = ReloadFromDisk(canonical);
                Register(settings, canonical);
            }
        }

        /// <summary>
        /// Unregisters content groups whose package folder or group file is gone from disk. Called right after
        /// git rewrote the worktree and before Unity imports the deletions, so Addressables never strips the
        /// entries of a group that is about to become dormant.
        /// </summary>
        public static void UnregisterMissing()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var cone = ContentGitApi.ReadConeFolders();
            foreach (var group in settings.groups.ToList())
            {
                if (!IsContentGroup(group)) continue;
                var package = PackageOfCanonicalGroup(group);
                var present = package != null && cone.Contains(package) && ContentGitApi.FolderHasFiles(package) &&
                              File.Exists(Path.Combine(projectRoot, CanonicalGroupPath(package)));
                if (!present) Unregister(settings, group);
            }
        }

        /// <summary>
        /// A group that is not registered can still carry unsaved in-memory changes, e.g. entries Addressables
        /// stripped while its assets were deleted. Its saves are refused while its package is not checked out,
        /// so before it is registered again the in-memory object is reloaded from the committed file.
        /// </summary>
        private static bool IsDirtyInMemory(AddressableAssetGroup group) =>
            EditorUtility.IsDirty(group) || group.Schemas.Any(s => s != null && EditorUtility.IsDirty(s));

        private static AddressableAssetGroup ReloadFromDisk(AddressableAssetGroup group)
        {
            var path = AssetDatabase.GetAssetPath(group);
            foreach (var schema in group.Schemas.Where(s => s != null && EditorUtility.IsDirty(s)).ToList())
                Resources.UnloadAsset(schema);
            Resources.UnloadAsset(group);
            Debug.LogWarning($"[ContentRepo] Discarded unsaved changes to '{path}' made while its package was not checked out.");
            return AssetDatabase.LoadAssetAtPath<AddressableAssetGroup>(path);
        }

        /// <summary>
        /// The canonical group of <paramref name="package"/>, registered in memory. Looked up by path, never by
        /// name. With <paramref name="create"/>, a package that has no committed group gets a new one at the
        /// canonical path; any failure to place it there throws instead of leaving a group elsewhere.
        /// </summary>
        public static AddressableAssetGroup Resolve(AddressableAssetSettings settings, string package, bool create, BuildLogHandler log = null)
        {
            if (settings == null) throw new InvalidOperationException("Addressables settings are missing.");
            var path = CanonicalGroupPath(package);
            var group = AssetDatabase.LoadAssetAtPath<AddressableAssetGroup>(path);

            var twin = FindTwin(settings, package, group);
            if (twin != null) throw new InvalidOperationException(TwinMessage(package, twin));

            if (group != null)
            {
                Register(settings, group);
                return group;
            }

            if (!create) return null;

            if (ContentGitApi.IsCommitted($"{ContentGitApi.GroupsFolderName}/{package}.asset"))
                throw new InvalidOperationException(
                    $"The group for '{package}' is committed at '{path}' but missing on disk. Pull the content repo or check '{package}' out again instead of creating a new group (a new group changes the package's bundle identity).");

            group = settings.CreateGroup(package, setAsDefaultGroup: false, readOnly: false, postEvent: false,
                schemasToCopy: null, typeof(BundledAssetGroupSchema));
            var createdPath = AssetDatabase.GetAssetPath(group);

            if (!AssetDatabase.IsValidFolder(GroupsFolder))
                AssetDatabase.CreateFolder(ContentRoot, ContentGitApi.GroupsFolderName);

            var moveError = AssetDatabase.MoveAsset(createdPath, path);
            if (!string.IsNullOrEmpty(moveError))
            {
                settings.RemoveGroup(group);
                throw new InvalidOperationException($"Could not place the new group for '{package}' at '{path}': {moveError}");
            }

            var schema = group.GetSchema<BundledAssetGroupSchema>();
            if (schema != null) schema.IncludeInBuild = false;
            MoveSchemasIntoGroupsFolder(group);
            log?.Invoke($"[ContentRepo] Created group '{package}' at '{path}'.");
            return group;
        }

        /// <summary>Moves a group's schema assets next to it in <c>_groups/</c>; throws when a move fails.</summary>
        public static void MoveSchemasIntoGroupsFolder(AddressableAssetGroup group, BuildLogHandler log = null)
        {
            if (group == null) return;
            foreach (var schema in group.Schemas.ToArray())
            {
                if (schema == null) continue;
                var currentPath = AssetDatabase.GetAssetPath(schema);
                if (string.IsNullOrEmpty(currentPath)) continue;
                var targetPath = $"{GroupsFolder}/{Path.GetFileName(currentPath)}";
                if (string.Equals(currentPath.Replace('\\', '/'), targetPath, StringComparison.OrdinalIgnoreCase)) continue;
                var error = AssetDatabase.MoveAsset(currentPath, targetPath);
                if (!string.IsNullOrEmpty(error))
                    throw new InvalidOperationException($"Could not move schema '{Path.GetFileName(currentPath)}' into '{GroupsFolder}': {error}");
                log?.Invoke($"[ContentRepo] Moved schema '{Path.GetFileName(currentPath)}' into the content repo.");
            }
        }

        /// <summary>Unregisters the canonical group of <paramref name="package"/> in memory (before its files go away).</summary>
        public static void Unregister(string package)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) return;
            foreach (var group in settings.groups.ToList())
                if (IsContentGroup(group) && string.Equals(PackageOfCanonicalGroup(group), package, StringComparison.OrdinalIgnoreCase))
                    Unregister(settings, group);
        }

        /// <summary>
        /// Saves the settings without any content group. Content groups are removed for the save and put back
        /// right after, so the in-memory registration is unchanged. Deferred while a build is running, because
        /// builds read the group list after Unity's own saves.
        /// </summary>
        public static void SaveSettings(AddressableAssetSettings settings = null)
        {
            settings ??= AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null || !EditorUtility.IsDirty(settings)) return;
            if (IsBuilding)
            {
                ScheduleFlush();
                return;
            }

            var content = settings.groups.Where(IsContentGroup).ToList();
            foreach (var group in content) settings.groups.Remove(group);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, false, false);
            Flushing = true;
            try { AssetDatabase.SaveAssetIfDirty(settings); }
            finally
            {
                Flushing = false;
                foreach (var group in content)
                    if (group != null && !settings.groups.Contains(group)) settings.groups.Add(group);
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, false);
            }
        }

        /// <summary>Marks a content build in progress; settings saves wait until every scope is disposed.</summary>
        public static IDisposable BuildScope()
        {
            buildScopes++;
            return new Scope(() =>
            {
                buildScopes--;
                ScheduleFlush();
            });
        }

        internal static bool HasContentGroups(AddressableAssetSettings settings) =>
            settings != null && settings.groups.Any(IsContentGroup);

        /// <summary>On quit Unity saves dirty assets after the last editor update; strip in place, nothing to restore.</summary>
        internal static void StripForQuit(AddressableAssetSettings settings)
        {
            settings.groups.RemoveAll(IsContentGroup);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, false, false);
        }

        /// <summary>A file in <c>_groups/</c> that belongs to a package not checked out on this machine.</summary>
        internal static bool IsDormantGroupFile(string assetPath)
        {
            var groupsFolder = GroupsFolder + "/";
            var path = assetPath.Replace('\\', '/');
            if (!path.StartsWith(groupsFolder, StringComparison.OrdinalIgnoreCase)) return false;
            var package = ContentGitApi.PackageOfGroupFile(Path.GetFileName(path));
            return package != null && !IsActive(package);
        }

        internal static void ScheduleFlush()
        {
            if (flushScheduled) return;
            flushScheduled = true;
            var nextCheck = 0d;
            void Tick()
            {
                var now = EditorApplication.timeSinceStartup;
                if (IsBuilding)
                {
                    nextCheck = now + BuildPollSeconds;
                    return;
                }
                if (now < nextCheck) return;
                EditorApplication.update -= Tick;
                flushScheduled = false;
                SaveSettings();
            }
            EditorApplication.update += Tick;
        }

        internal static void ReportOnce(string key, Action report)
        {
            if (Reported.Add(key)) report();
        }

        private static void Register(AddressableAssetSettings settings, AddressableAssetGroup group)
        {
            if (settings.groups.Contains(group)) return;
            settings.groups.Add(group);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.GroupAdded, group, true, false);
        }

        private static void Unregister(AddressableAssetSettings settings, AddressableAssetGroup group)
        {
            if (!settings.groups.Remove(group)) return;
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.GroupRemoved, group, true, false);
        }

        private static string PackageOfCanonicalGroup(AddressableAssetGroup group)
        {
            var path = AssetDatabase.GetAssetPath(group).Replace('\\', '/');
            var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.Equals(folder, GroupsFolder, StringComparison.OrdinalIgnoreCase)) return null;
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return null;
            var name = Path.GetFileNameWithoutExtension(path);
            return ContentGitApi.PackageOfGroupFile(name + ".asset") == name ? name : null;
        }

        private static AddressableAssetGroup FindTwin(AddressableAssetSettings settings, string package, AddressableAssetGroup canonical) =>
            settings.groups.FirstOrDefault(g =>
                g != null && g != canonical && !IsContentGroup(g) &&
                string.Equals(g.Name, package, StringComparison.OrdinalIgnoreCase));

        private static string TwinMessage(string package, AddressableAssetGroup twin) =>
            $"[ContentRepo] Two Addressables groups are named '{package}': the content repo's '{CanonicalGroupPath(package)}' and " +
            $"'{AssetDatabase.GetAssetPath(twin)}' (m_GUID {twin.Guid}). Play Mode setup and content builds of '{package}' are blocked " +
            "until the duplicate outside the content repo is migrated into the canonical group and removed.";

        private sealed class Scope : IDisposable
        {
            private Action onDispose;
            public Scope(Action onDispose) => this.onDispose = onDispose;
            public void Dispose()
            {
                onDispose?.Invoke();
                onDispose = null;
            }
        }
    }

    /// <summary>
    /// Keeps content groups out of every save of the Addressables settings and keeps groups of packages
    /// that are not checked out from being written at all.
    /// </summary>
    internal sealed class ContentSettingsSaveGuard : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            if (ContentGroupRegistry.Flushing) return paths;
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var settingsPath = settings != null ? AssetDatabase.GetAssetPath(settings) : null;
            var hasContent = ContentGroupRegistry.HasContentGroups(settings);

            var result = new List<string>(paths.Length);
            foreach (var path in paths)
            {
                if (hasContent && path == settingsPath)
                {
                    if (ContentGroupRegistry.Quitting)
                    {
                        ContentGroupRegistry.StripForQuit(settings);
                        result.Add(path);
                    }
                    else ContentGroupRegistry.ScheduleFlush();
                    continue;
                }

                if (ContentGroupRegistry.IsDormantGroupFile(path))
                {
                    ContentGroupRegistry.ReportOnce($"dormant:{path}", () =>
                        Debug.LogWarning($"[ContentRepo] Not saving '{path}': its package is not checked out on this machine, so its group must stay as committed."));
                    continue;
                }

                result.Add(path);
            }
            return result.ToArray();
        }
    }

    /// <summary>Re-reconciles after imports that can change registration (group files, the settings asset).</summary>
    internal sealed class ContentGroupPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var root = ContentGroupRegistry.ContentRoot;
            if (string.IsNullOrEmpty(root)) return;
            var prefix = root + "/";
            bool Relevant(string p) =>
                p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("AddressableAssetSettings.asset", StringComparison.OrdinalIgnoreCase);
            if (imported.Any(Relevant) || deleted.Any(Relevant) || moved.Any(Relevant) || movedFrom.Any(Relevant))
                ContentGroupRegistry.ScheduleReconcile();
        }
    }
}
