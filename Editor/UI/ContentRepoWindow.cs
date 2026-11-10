using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ContentRepo.Editor
{
    public sealed class ContentRepoWindow : EditorWindow
    {
        private const string PackageRoot = "Packages/com.antonlicht.content-repo";
        private const string UxmlPath = PackageRoot + "/Editor/UI/ContentRepoWindow.uxml";
        private const string UssPath = PackageRoot + "/Editor/UI/ContentRepoWindow.uss";
        private const string FolderRowUxmlPath = PackageRoot + "/Editor/UI/FolderRow.uxml";

        // ── UI elements (top → bottom, matching UXML) ─────────────────────────

        // Generation warning banner
        private VisualElement genWarningBanner;
        private Label         genWarningLabel;
        private Button        bumpGenerationBtn, ackUnityVersionBtn;

        // Merge / rebase in progress banner
        private VisualElement operationBanner;
        private Label         operationLabel;
        private Button        abortOperationBtn;

        // Top bar
        private Button      refreshBtn, pullAllBtn, buildAllBtn;
        private Button      uploadAllBtn, promoteAllBtn;
        private ToolbarMenu moreAllMenu;

        // New-folder row / setup banner
        private Button        newFolderBtn, initBtn;
        private VisualElement setupBanner, newFolderRow;
        private TextField     newFolderField;

        // Folder list
        private ScrollView folderList;

        // Infrastructure row
        private Label  stackStatusLabel;
        private Button deployLambdaBtn, teardownLambdaBtn;

        // Log
        private Label deployLog;

        // Status bar
        private VisualElement spinner;
        private Label         statusLabel;

        // ── State ─────────────────────────────────────────────────────────────

        private bool?          isInitialized;
        private List<string>   remoteFolders    = new();
        private HashSet<string> repoFolders     = new(StringComparer.OrdinalIgnoreCase);
        private List<string>   checkedOutFolders = new();
        private Dictionary<string, FolderStatus>       folderStatuses = new();
        private readonly Dictionary<string, Action<FolderStatus>> rowUpdaters = new();
        private VisualTreeAsset rowTemplate;
        private ContentManifest stagingManifest;
        private ContentManifest productionManifest;
        private Dictionary<string, PackageSync> packageSync = new(StringComparer.OrdinalIgnoreCase);
        private string operationInProgress;
        private bool unresolvedConflicts;
        private int syncVersion;
        private bool busy;
        private bool polling;
        private bool projectChangedPending;
        private IVisualElementScheduledItem spinnerTick, statusPoller;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        [MenuItem("Window/Content Browser")]
        public static void ShowWindow()
        {
            var w = GetWindow<ContentRepoWindow>();
            w.titleContent = MakeTitleContent();
            w.minSize = new Vector2(580, 400);
        }

        private static GUIContent MakeTitleContent() =>
            new GUIContent("Content Browser", LoadIcon("monitor-cloud"));

        private void OnEnable()
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null) { rootVisualElement.Add(new Label($"Could not load {UxmlPath}")); return; }
            uxml.CloneTree(rootVisualElement);

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            titleContent = MakeTitleContent();
            rowTemplate  = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(FolderRowUxmlPath);
            ResolveElements();
            WireFolderTab();
            WireDeployTab();

            ContentGitApi.OnStateChanged        += OnExternalStateChanged;
            EditorApplication.projectChanged    += OnProjectChanged;

            // Reset busy so the initial load is never skipped when re-enabled after
            // exiting Play mode (no domain reload, so the flag can persist).
            busy = false;
            SetSpinnerVisible(false);
            SetStatus("Ready");
            _ = RunAsync("Loading…", () => Task.CompletedTask);
            _ = RefreshManifestsAsync();
            statusPoller = rootVisualElement.schedule.Execute(() => _ = PollStatusesAsync()).Every(60_000);
        }

        private void OnDisable()
        {
            ContentGitApi.OnStateChanged     -= OnExternalStateChanged;
            EditorApplication.projectChanged -= OnProjectChanged;
            spinnerTick?.Pause();  spinnerTick  = null;
            statusPoller?.Pause(); statusPoller = null;
        }

        // ── Setup ─────────────────────────────────────────────────────────────

        private void ResolveElements()
        {
            // Generation warning banner
            genWarningBanner  = Q<VisualElement>("gen-warning-banner");
            genWarningLabel   = Q<Label>("gen-warning-label");
            bumpGenerationBtn = Q<Button>("btn-bump-generation");
            ackUnityVersionBtn = Q<Button>("btn-ack-unity-version");

            operationBanner   = Q<VisualElement>("operation-banner");
            operationLabel    = Q<Label>("operation-label");
            abortOperationBtn = Q<Button>("btn-abort-operation");

            // Top bar — buttons then their icon images, left to right
            refreshBtn          = Q<Button>("btn-refresh");
            Q<Image>("img-refresh").image   = LoadIcon("refresh-cw");
            pullAllBtn          = Q<Button>("btn-pull-all");
            Q<Image>("img-pull-all").image  = LoadIcon("folder-sync");
            buildAllBtn         = Q<Button>("btn-build-all");
            Q<Image>("img-build-all").image = LoadIcon("hammer");
            uploadAllBtn        = Q<Button>("btn-upload-all");
            Q<Image>("img-upload-all").image = LoadIcon("cloud-upload");
            promoteAllBtn = Q<Button>("btn-promote-all");
            Q<Image>("img-promote-all").image = LoadIcon("rocket");
            moreAllMenu   = Q<ToolbarMenu>("btn-more-all");

            // Hidden until data confirms they are relevant
            pullAllBtn.style.display    = DisplayStyle.None;
            buildAllBtn.style.display   = DisplayStyle.None;
            uploadAllBtn.style.display  = DisplayStyle.None;
            promoteAllBtn.style.display = DisplayStyle.None;

            // New-folder row
            newFolderBtn  = Q<Button>("btn-new-folder");
            Q<Image>("img-new-folder").image = LoadIcon("plus");

            // Setup banner
            setupBanner   = Q<VisualElement>("setup-banner");
            initBtn       = Q<Button>("btn-init");
            newFolderRow  = Q<VisualElement>("new-folder-row");
            newFolderField = Q<TextField>("new-folder-field");

            // Folder list
            folderList = Q<ScrollView>("folder-list-container");

            // Infrastructure
            stackStatusLabel = Q<Label>("stack-status-label");
            deployLambdaBtn  = Q<Button>("btn-deploy-lambda");
            teardownLambdaBtn = Q<Button>("btn-teardown-lambda");

            // Log + status bar
            deployLog   = Q<Label>("deploy-log");
            spinner     = Q<VisualElement>("progress-spinner");
            statusLabel = Q<Label>("status-label");

            newFolderRow.style.display = DisplayStyle.None;
            setupBanner.style.display  = DisplayStyle.None;
        }

        private T Q<T>(string name) where T : VisualElement => rootVisualElement.Q<T>(name);

        // ── Wiring ────────────────────────────────────────────────────────────

        private void WireFolderTab()
        {
            refreshBtn.clicked += () => _ = RunAsync("Refreshing…", () => RefreshManifestsAsync(sync: false));
            pullAllBtn.clicked += () => _ = RunAsync("Pulling all…", ContentGitApi.PullAllAsync);
            initBtn.clicked    += () => _ = RunAsync("Initializing…", ContentGitApi.InitAsync);
            abortOperationBtn.clicked += () =>
            {
                var operation = operationInProgress;
                if (operation == null) return;
                if (EditorUtility.DisplayDialog($"Abort {operation}",
                    $"Abort the {operation} and return to the state before it?\nConflict resolutions made since it started are lost.", $"Abort {operation}", "Cancel"))
                    _ = RunAsync($"Aborting {operation}…", ContentGitApi.AbortOperationAsync);
            };

            var createFolderBtn = Q<Button>("btn-create-folder");
            var newFolderLabel  = Q<Label>("new-folder-label");

            void CollapseInput()
            {
                newFolderBtn.style.display    = DisplayStyle.Flex;
                newFolderLabel.style.display  = DisplayStyle.Flex;
                newFolderField.style.display  = DisplayStyle.None;
                createFolderBtn.style.display = DisplayStyle.None;
                newFolderField.SetValueWithoutNotify("");
            }
            void ConfirmCreate()
            {
                var name = newFolderField.value?.Trim();
                if (string.IsNullOrEmpty(name)) return;
                CollapseInput();
                _ = RunAsync($"Creating '{name}'…", () => ContentGitApi.CreateFolderAsync(name));
            }

            newFolderBtn.clicked += () =>
            {
                newFolderBtn.style.display    = DisplayStyle.None;
                newFolderLabel.style.display  = DisplayStyle.None;
                newFolderField.style.display  = DisplayStyle.Flex;
                createFolderBtn.style.display = DisplayStyle.Flex;
                newFolderField.Focus();
            };
            createFolderBtn.clicked += () =>
            {
                if (string.IsNullOrEmpty(newFolderField.value?.Trim()))
                { EditorUtility.DisplayDialog("Name required", "Enter a folder name.", "OK"); return; }
                ConfirmCreate();
            };
            newFolderField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                { evt.StopPropagation(); ConfirmCreate(); }
                else if (evt.keyCode == KeyCode.Escape)
                { evt.StopPropagation(); newFolderField.Blur(); CollapseInput(); }
            }, TrickleDown.TrickleDown);
        }

        private void WireDeployTab()
        {
            bumpGenerationBtn.clicked  += () => { ContentRepoGenerationSettings.instance.BumpGeneration();          RefreshGenerationWarning(); };
            ackUnityVersionBtn.clicked += () => { ContentRepoGenerationSettings.instance.AcknowledgeUnityVersion(); RefreshGenerationWarning(); };

            buildAllBtn.clicked += () => _ = RunBuildAsync(checkedOutFolders.ToList(), "Building all…",
                async () => { await ContentBuildApi.BuildAllCheckedOutAsync(AppendDeployLog); Rebuild(); });

            uploadAllBtn.clicked += () => _ = RunPipelineAsync("Uploading all to staging…",
                async () => { await ContentUploadApi.UploadAllCheckedOutAsync(StagingKey(), AppendDeployLog); await RefreshManifestsAsync(); },
                deployLog);

            promoteAllBtn.clicked += () => _ = PromoteAsync(null);

            moreAllMenu.menu.AppendAction("Build and Deploy All", _ =>
            {
                var __ = RunBuildAsync(checkedOutFolders.ToList(), "Build and Deploy All…",
                    async () =>
                    {
                        await ContentBuildApi.BuildAllCheckedOutAsync(AppendDeployLog);
                        await ContentUploadApi.UploadAllCheckedOutAsync(StagingKey(), AppendDeployLog);
                        await RefreshManifestsAsync();
                    });
            });

            deployLambdaBtn.clicked += () => _ = RunPipelineAsync("Deploying cleanup Lambda…",
                async () => { await ContentInfraApi.DeployCleanupLambdaAsync(AppendDeployLog); await RefreshStackStatusAsync(); },
                deployLog);

            teardownLambdaBtn.clicked += () =>
            {
                if (!EditorUtility.DisplayDialog("Teardown Lambda", "Delete the content-repo-cleanup CloudFormation stack?", "Teardown", "Cancel")) return;
                _ = RunPipelineAsync("Tearing down Lambda…",
                    async () => { await ContentInfraApi.TeardownCleanupLambdaAsync(AppendDeployLog); await RefreshStackStatusAsync(); },
                    deployLog);
            };
        }

        // ── Data refresh ──────────────────────────────────────────────────────

        private async Task RefreshDataAsync()
        {
            isInitialized = await ContentGitApi.IsInitializedAsync();
            remoteFolders.Clear(); repoFolders.Clear(); checkedOutFolders.Clear(); folderStatuses.Clear();
            if (isInitialized != true) return;

            try { await ContentGitApi.RepairIndexAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Could not repair the content repo index: {ex.Message}"); }

            try { remoteFolders = await ContentGitApi.GetRemoteFoldersAsync(); }
            catch (Exception ex) { Debug.LogWarning($"Remote folders: {ex.Message}"); }
            repoFolders = new HashSet<string>(remoteFolders, StringComparer.OrdinalIgnoreCase);

            try { checkedOutFolders = await ContentGitApi.GetCheckedOutFoldersAsync(); }
            catch (Exception ex) { Debug.LogWarning($"Checked-out folders: {ex.Message}"); }

            var diskFolders = new List<string>();
            try { diskFolders = await ContentGitApi.GetLocalFoldersOnDiskAsync(); }
            catch (Exception ex) { Debug.LogWarning($"Disk folders: {ex.Message}"); }

            // Unity recreates an empty folder for every top-level package .meta (always checked out in
            // cone mode). Such a folder is not content on disk and must not pull the package into the cone.
            diskFolders = diskFolders
                .Where(f => checkedOutFolders.Any(c => c.Equals(f, StringComparison.OrdinalIgnoreCase)) || ContentGitApi.FolderHasFiles(f))
                .ToList();

            var allStatuses = await ContentGitApi.GetAllFolderStatusesAsync();
            MergeGroupsStatusInto(checkedOutFolders, allStatuses);

            // While a merge or rebase is open, files appear and disappear with the commit being applied; changing the
            // sparse cone then would hide or materialise packages behind the user's back.
            var stopped = await ContentGitApi.GetOperationInProgressAsync() != null || await ContentGitApi.HasUnresolvedConflictsAsync();

            foreach (var f in checkedOutFolders)
            {
                var isOnRemote = remoteFolders.Any(r => r.Equals(f, StringComparison.OrdinalIgnoreCase));
                var isOnDisk   = diskFolders.Any(d => d.Equals(f, StringComparison.OrdinalIgnoreCase));
                if (!isOnRemote && !isOnDisk && stopped)
                {
                    remoteFolders.Add(f);
                    allStatuses.TryGetValue(f, out var pending);
                    folderStatuses[f] = pending;
                    continue;
                }
                if (!isOnRemote && !isOnDisk)
                {
                    // Stale sparse-checkout entry — prune silently.
                    try { await ContentGitApi.RemoveFolderFromSparseCheckoutAsync(f); }
                    catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Could not prune stale sparse-checkout entry '{f}': {ex.Message}"); }
                    Debug.Log($"[ContentRepo] Pruned stale sparse-checkout entry '{f}' — not on remote or disk.");
                    continue;
                }
                if (!isOnRemote) remoteFolders.Add(f);
                allStatuses.TryGetValue(f, out var s);
                folderStatuses[f] = s;
            }

            var repairedAny = false;
            foreach (var f in diskFolders)
            {
                if (!remoteFolders.Any(r => r.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    remoteFolders.Add(f);

                // A folder present on disk but missing from the sparse cone (content moved into the
                // repo, or checked out by an older tool version) keeps its files skip-worktree'd, so
                // git hides every edit — the badge shows clean and commits drop changes. Bring it
                // into the cone and clear that bit so changes are tracked and committable. This runs
                // for on-disk packages regardless of whether they're on the remote.
                if (stopped && !checkedOutFolders.Any(c => c.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
                if (!checkedOutFolders.Any(c => c.Equals(f, StringComparison.OrdinalIgnoreCase)))
                {
                    try { await ContentGitApi.EnsureFolderInSparseCheckoutAsync(f); repairedAny = true; }
                    catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Could not repair sparse-checkout for '{f}': {ex.Message}"); }
                    checkedOutFolders.Add(f);
                }
                allStatuses.TryGetValue(f, out var s);
                folderStatuses[f] = s;
            }

            // Clearing skip-worktree above exposes changes git was previously ignoring. Re-read the
            // statuses so the badges reflect them now rather than on the next poll.
            if (repairedAny)
            {
                allStatuses = await ContentGitApi.GetAllFolderStatusesAsync();
                MergeGroupsStatusInto(checkedOutFolders, allStatuses);
                foreach (var f in checkedOutFolders)
                {
                    allStatuses.TryGetValue(f, out var s);
                    folderStatuses[f] = s;
                }
            }
            remoteFolders.Sort(StringComparer.OrdinalIgnoreCase);

            // Remove local-dev overrides for packages whose folder no longer exists on disk (not while a merge or
            // rebase has removed it for the moment).
            foreach (var pkg in stopped ? new List<string>() : ContentLocalDevOverrides.All.Keys.ToList())
            {
                if (!diskFolders.Any(f => f.Equals(pkg, StringComparison.OrdinalIgnoreCase)))
                {
                    ContentLocalDevApi.ClearOverride(pkg);
                    Debug.Log($"[ContentRepo] Cleared local dev override for '{pkg}' — folder no longer on disk.");
                }
            }

            ContentGroupRegistry.Reconcile();
        }

        private async Task RefreshManifestsAsync() => await RefreshManifestsAsync(sync: true);

        private async Task RefreshManifestsAsync(bool sync)
        {
            var settings = ContentUploadSettings.instance;
            try
            {
                stagingManifest    = await ContentUploadApi.GetManifestAsync(settings.StagingPrefix);
                productionManifest = await ContentUploadApi.GetManifestAsync(settings.ProductionPrefix);
            }
            catch { /* credentials not configured yet */ }
            _ = RefreshStackStatusAsync();
            if (sync) await RefreshSyncAsync();
            if (!busy) Rebuild();
            UpdateToolbarVisibility();
        }

        private async Task RefreshSyncAsync()
        {
            var version = ++syncVersion;
            if (isInitialized != true) return;

            var platform   = EditorUserBuildSettings.activeBuildTarget.ToString();
            var staging    = stagingManifest;
            var production = productionManifest;
            var checkedOut = new HashSet<string>(checkedOutFolders, StringComparer.OrdinalIgnoreCase);
            var packages   = new HashSet<string>(checkedOut, StringComparer.OrdinalIgnoreCase);
            packages.UnionWith(repoFolders);
            if (staging    != null) foreach (var e in staging.contentPackages)    packages.Add(e.name);
            if (production != null) foreach (var e in production.contentPackages) packages.Add(e.name);

            var result = new Dictionary<string, PackageSync>(StringComparer.OrdinalIgnoreCase);
            string operation = null;
            var conflicts = false;
            try
            {
                operation = await ContentGitApi.GetOperationInProgressAsync();
                conflicts = await ContentGitApi.HasUnresolvedConflictsAsync();
                var remote = await ContentSyncApi.ResolveRemoteAsync();
                foreach (var package in packages)
                {
                    var stagingEntry    = staging?.Find(package);
                    var productionEntry = production?.Find(package);
                    var sync = new PackageSync
                    {
                        Staging = await ContentSyncApi.GetDeployStatusAsync(package, stagingEntry, platform, remote),
                    };
                    sync.Production = IsSameBuild(stagingEntry, productionEntry, platform)
                        ? sync.Staging
                        : await ContentSyncApi.GetDeployStatusAsync(package, productionEntry, platform, remote);
                    if (remote != null && repoFolders.Contains(package))
                        sync.Loss = await ContentSyncApi.FindUnrepairedLossAsync(package, remote);
                    if (remote != null && checkedOut.Contains(package))
                        sync.Unpulled = await ContentSyncApi.GetUnpulledCommitsAsync(package, remote);
                    result[package] = sync;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Could not compare the deployed builds with the content repository: {ex.Message}"); }

            if (version != syncVersion) return;
            packageSync = result;
            operationInProgress = operation;
            unresolvedConflicts = conflicts;
        }

        private static bool IsSameBuild(ContentManifestEntry a, ContentManifestEntry b, string platform)
        {
            var x = a?.FindPlatform(platform);
            var y = b?.FindPlatform(platform);
            return x != null && y != null && a.gitSha == b.gitSha && IsSamePlatformBuild(x, y);
        }

        private static bool IsSamePlatformBuild(ContentManifestPlatformEntry x, ContentManifestPlatformEntry y) =>
            x != null && y != null && x.buildId == y.buildId
            && (x.sourceSha ?? "") == (y.sourceSha ?? "") && (x.sourceTree ?? "") == (y.sourceTree ?? "") && x.sourceDirty == y.sourceDirty;

        /// <summary>
        /// Promoting would not change what production serves or what it is known to be built from. Two builds with
        /// the same bundles and the same source fingerprint are the same deployment even when they were stamped at
        /// different commits of the repository (a rebuild after an unrelated commit).
        /// </summary>
        private static bool IsSameDeployment(ContentManifestPlatformEntry x, ContentManifestPlatformEntry y)
        {
            if (x == null || y == null || x.buildId != y.buildId || x.sourceDirty != y.sourceDirty) return false;
            return !string.IsNullOrEmpty(x.sourceTree) && !string.IsNullOrEmpty(y.sourceTree)
                ? x.sourceTree == y.sourceTree
                : (x.sourceSha ?? "") == (y.sourceSha ?? "") && (x.sourceTree ?? "") == (y.sourceTree ?? "");
        }

        /// <summary>
        /// Promote changes production for this package: some staging platform differs from production, by build
        /// or by its source stamp (an identical build re-uploaded with a newer stamp).
        /// </summary>
        private bool IsPromoteReady(string package)
        {
            var staging = stagingManifest?.Find(package);
            if (staging == null) return false;
            var production = productionManifest?.Find(package);
            return staging.platforms.Any(platform => !IsSameDeployment(platform, production?.FindPlatform(platform.platform)));
        }

        /// <summary>
        /// The local build differs from staging: another build, or the same bundles rebuilt later with a clean source
        /// stamp that differs from staging's (for example after a commit that touched only files which do not change
        /// the bundles), so staging can show that it matches the repository.
        /// </summary>
        private bool IsUploadReady(string package, string platform)
        {
            var local = ContentBuildApi.GetLatestBuild(package, platform);
            if (local == null) return false;
            var staged = stagingManifest?.Find(package)?.FindPlatform(platform);
            if (staged == null || local.buildId != staged.buildId) return true;
            return !local.sourceDirty && !string.IsNullOrEmpty(local.sourceTree)
                && (local.sourceTree != staged.sourceTree || staged.sourceDirty)
                && IsLater(local.builtAt, staged.builtAt);
        }

        private static bool IsLater(string builtAt, string than)
        {
            if (!DateTime.TryParse(builtAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var a)) return false;
            return !DateTime.TryParse(than, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var b)
                   || a.ToUniversalTime() > b.ToUniversalTime();
        }

        private async Task RefreshStackStatusAsync()
        {
            try   { stackStatusLabel.text = $"Stack: {await ContentInfraApi.GetStackStatusAsync()}"; }
            catch { stackStatusLabel.text = "Stack: unknown"; }
        }

        private void RefreshGenerationWarning()
        {
            var gen    = ContentRepoGenerationSettings.instance;
            var change = gen.CheckUnityVersionChange();
            if (change == ContentRepoGenerationSettings.VersionChangeKind.MinorOrMajor)
            {
                genWarningBanner.style.display   = DisplayStyle.Flex;
                genWarningLabel.text             = $"Unity version changed from {gen.UnityVersionAtGeneration} to {Application.unityVersion}. " +
                                                   "Bundle format may be incompatible. Bump the generation before building.";
                ackUnityVersionBtn.style.display = DisplayStyle.None;
                bumpGenerationBtn.style.display  = DisplayStyle.Flex;
            }
            else if (change == ContentRepoGenerationSettings.VersionChangeKind.PatchOnly)
            {
                genWarningBanner.style.display   = DisplayStyle.Flex;
                genWarningLabel.text             = $"Unity patch version changed ({gen.UnityVersionAtGeneration} → {Application.unityVersion}). Bundles are likely compatible.";
                ackUnityVersionBtn.style.display = DisplayStyle.Flex;
                bumpGenerationBtn.style.display  = DisplayStyle.None;
            }
            else
            {
                genWarningBanner.style.display = DisplayStyle.None;
            }
        }

        private void UpdateToolbarVisibility()
        {
            if (uploadAllBtn == null || promoteAllBtn == null) return;
            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();

            pullAllBtn.style.display  = checkedOutFolders.Any(f => repoFolders.Contains(f))
                ? DisplayStyle.Flex : DisplayStyle.None;
            buildAllBtn.style.display = checkedOutFolders.Count > 0
                ? DisplayStyle.Flex : DisplayStyle.None;

            var anyUploadNeeded = checkedOutFolders.Any(pkg => IsUploadReady(pkg, platform));
            uploadAllBtn.style.display = anyUploadNeeded ? DisplayStyle.Flex : DisplayStyle.None;

            var anyPromoteReady = checkedOutFolders.Any(IsPromoteReady);
            promoteAllBtn.style.display = anyPromoteReady ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ── List building ─────────────────────────────────────────────────────

        private void Rebuild()
        {
            setupBanner.style.display = isInitialized == false ? DisplayStyle.Flex : DisplayStyle.None;
            UpdateOperationBanner();
            rowUpdaters.Clear();
            folderList.Clear();
            if (isInitialized != true) return;

            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();

            if (remoteFolders.Count == 0)
                folderList.Add(new Label("No folders available.")
                    { style = { unityFontStyleAndWeight = FontStyle.Italic, marginTop = 8, marginLeft = 8 } });
            else
                foreach (var folder in remoteFolders.Where(f => checkedOutFolders.Any(c => c.Equals(f, StringComparison.OrdinalIgnoreCase))))
                    folderList.Add(BuildRow(folder));

            newFolderRow.style.display = DisplayStyle.Flex;
            folderList.Add(newFolderRow);

            var nonCheckedOut = GetNonCheckedOutPackages().ToList();
            if (nonCheckedOut.Count > 0)
            {
                folderList.Add(new Label("Not checked out locally")
                    { style = { opacity = 0.45f, fontSize = 10, marginTop = 6, marginLeft = 8, marginBottom = 2 } });
                foreach (var pkg in nonCheckedOut)
                    folderList.Add(BuildNonCheckedOutRow(pkg, platform));
            }
        }

        private void UpdateOperationBanner()
        {
            var visible = isInitialized == true && (operationInProgress != null || unresolvedConflicts);
            operationBanner.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            abortOperationBtn.style.display = operationInProgress != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;
            abortOperationBtn.text = $"Abort {operationInProgress}";
            operationLabel.text = operationInProgress switch
            {
                "merge" =>
                    "The content repository is in the middle of a merge, usually because a pull hit a conflict. The changes shown on the " +
                    "packages include other people's incoming work, so do not discard them. Resolve the conflicts in your git client and " +
                    "commit the merge, or abort it to return to the state before the pull.",
                "rebase" =>
                    "The content repository is in the middle of a rebase, usually because a pull with rebase hit a conflict. Your own " +
                    "commits are being replayed on top of the pulled changes, so do not discard the changes shown. Resolve the conflicts " +
                    "in your git client and continue the rebase (git rebase --continue), or abort it to return to the state before the pull.",
                null =>
                    "Files in the content repository have unresolved conflicts (for example after git stash pop). They contain conflict " +
                    "markers: resolve them in your git client and stage them before you commit or build.",
                _ =>
                    $"The content repository is in the middle of git {operationInProgress}. The changes shown are not committed on this " +
                    $"branch yet, so do not discard them. Resolve the conflicts in your git client and continue it " +
                    $"(git {operationInProgress} --continue), or abort it.",
            };
        }

        private async Task RefreshOperationStateAsync()
        {
            if (isInitialized != true) return;
            var operation = await ContentGitApi.GetOperationInProgressAsync();
            var conflicts = await ContentGitApi.HasUnresolvedConflictsAsync();
            if (operation == operationInProgress && conflicts == unresolvedConflicts) return;
            operationInProgress = operation;
            unresolvedConflicts = conflicts;
            UpdateOperationBanner();
        }

        private void OnFocus()
        {
            if (operationBanner != null && !busy) _ = RefreshOperationStateAsync();
        }

        private IEnumerable<string> GetNonCheckedOutPackages()
        {
            var local = new HashSet<string>(checkedOutFolders, StringComparer.OrdinalIgnoreCase);
            var all   = new HashSet<string>(repoFolders,       StringComparer.OrdinalIgnoreCase);
            if (stagingManifest    != null) foreach (var e in stagingManifest.contentPackages)    all.Add(e.name);
            if (productionManifest != null) foreach (var e in productionManifest.contentPackages) all.Add(e.name);
            return all.Where(p => !local.Contains(p)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        }

        private VisualElement BuildNonCheckedOutRow(string pkg, string platform)
        {
            var row = new VisualElement();
            row.AddToClassList("cs-pipeline-row");
            row.AddToClassList("cs-pipeline-row--remote");

            if (repoFolders.Contains(pkg))
            {
                var checkoutBtn = new Button(() => _ = RunAsync($"Checking out '{pkg}'…", () => ContentGitApi.CheckOutFolderAsync(pkg)));
                checkoutBtn.AddToClassList("cs-icon-btn");
                checkoutBtn.AddToClassList("cs-checkout-btn");
                checkoutBtn.tooltip = $"Check out '{pkg}'";
                var checkoutImg = new Image { pickingMode = PickingMode.Ignore, image = LoadIcon("folder-down") };
                checkoutImg.AddToClassList("cs-icon-image");
                checkoutBtn.Add(checkoutImg);
                row.Add(checkoutBtn);
            }

            var nameLabel   = new Label(pkg);
            nameLabel.AddToClassList("cs-pipeline-name");
            row.Add(nameLabel);

            var renameField = new TextField { style = { display = DisplayStyle.None, flexGrow = 1 } };
            renameField.AddToClassList("cs-folder-name");
            renameField.AddToClassList("cs-rename-field");
            row.Add(renameField);

            void EnterRenameMode()
            {
                renameField.SetValueWithoutNotify(pkg);
                nameLabel.style.display   = DisplayStyle.None;
                renameField.style.display = DisplayStyle.Flex;
                renameField.Focus();
                renameField.SelectAll();
            }
            void ExitRenameMode(bool commit)
            {
                nameLabel.style.display   = DisplayStyle.Flex;
                renameField.style.display = DisplayStyle.None;
                if (!commit) return;
                var n = renameField.value?.Trim();
                if (!string.IsNullOrEmpty(n) && n != pkg)
                    _ = RunAsync($"Renaming '{pkg}'…", () => ContentGitApi.RenameFolderAsync(pkg, n));
            }
            renameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if      (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { evt.StopPropagation(); ExitRenameMode(true);  }
                else if (evt.keyCode == KeyCode.Escape)                                       { evt.StopPropagation(); ExitRenameMode(false); }
            });

            var stgId = stagingManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
            var prdId = productionManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;

            if (stgId == null && prdId != null)
            {
                var warn = new Label("⚠")
                    { tooltip = "This package is in production but not in staging.\nUse 'Restore from production' to re-add it to staging." };
                warn.AddToClassList("cs-warn-icon");
                row.Add(warn);
            }

            var promoteReady = IsPromoteReady(pkg);

            var badges = new VisualElement();
            badges.AddToClassList("cs-badge-group");
            AddSyncBadges(badges, pkg, stgId, prdId);
            row.Add(badges);

            row.Add(new VisualElement { style = { flexGrow = 1 } });

            var staging    = ContentUploadSettings.instance.StagingPrefix;
            var production = ContentUploadSettings.instance.ProductionPrefix;

            if (promoteReady)
            {
                var promoteBtn = new Button(() => _ = PromoteAsync(pkg));
                promoteBtn.AddToClassList("cs-icon-btn");
                promoteBtn.AddToClassList("cs-icon-btn--muted");
                promoteBtn.tooltip = $"Promote '{pkg}' to production\n{stgId}";
                var promoteImg = new Image { pickingMode = PickingMode.Ignore, image = LoadIcon("rocket") };
                promoteImg.AddToClassList("cs-icon-image");
                promoteBtn.Add(promoteImg);
                row.Add(promoteBtn);
            }
            var moreBtn    = new Button { text = "⋮", tooltip = "More actions" };
            moreBtn.AddToClassList("cs-icon-btn");
            moreBtn.AddToClassList("cs-icon-btn--lg");
            moreBtn.AddToClassList("cs-menu-btn");
            moreBtn.clicked += () =>
            {
                var menu = new GenericMenu();
                if (packageSync.TryGetValue(pkg, out var lostSync) && lostSync.Loss != null)
                {
                    menu.AddItem(new GUIContent("Lost changes…"), false, () => _ = ShowLostChangesAsync(pkg));
                    menu.AddSeparator("");
                }
                if (repoFolders.Contains(pkg))
                    menu.AddItem(new GUIContent("Rename"), false, () => EnterRenameMode());
                if (prdId != null && prdId != stgId)
                    menu.AddItem(new GUIContent("Restore from production"), false, () =>
                        _ = RunPipelineAsync($"Restoring '{pkg}'…",
                            async () => { await ContentUploadApi.PromoteContentPackageAsync(pkg, production, staging, AppendDeployLog); await RefreshManifestsAsync(); },
                            deployLog));
                if (stgId != null)
                    menu.AddItem(new GUIContent("Remove from staging"), false, () =>
                    {
                        var msg = stgId == prdId
                            ? $"Remove '{pkg}' from the staging manifest?\nProduction shares this build, so its bundles are kept."
                            : $"Remove '{pkg}' from staging and delete its bundles from S3?\nThis cannot be undone.";
                        if (EditorUtility.DisplayDialog("Remove from staging", msg, "Remove", "Cancel"))
                            _ = RunPipelineAsync($"Removing '{pkg}' from staging…",
                                async () => { await ContentUploadApi.RemoveFromStagingAsync(pkg, platform, AppendDeployLog); await RefreshManifestsAsync(); },
                                deployLog);
                    });
                if (prdId != null)
                    menu.AddItem(new GUIContent("Remove from production"), false, () =>
                    {
                        if (EditorUtility.DisplayDialog("Remove from production",
                            $"Remove '{pkg}' from the production manifest?\nThis does not delete any files.", "Remove", "Cancel"))
                            _ = RunPipelineAsync($"Removing '{pkg}' from production…",
                                async () => { await ContentUploadApi.RemoveFromManifestAsync(pkg, production, AppendDeployLog); await RefreshManifestsAsync(); },
                                deployLog);
                    });
                if (repoFolders.Contains(pkg))
                {
                    menu.AddItem(new GUIContent("Delete from repository"), false, () =>
                    {
                        if (EditorUtility.DisplayDialog("Delete remote",
                            $"Permanently delete '{pkg}' from the remote?\nThis cannot be undone.", "Delete", "Cancel"))
                            _ = RunAsync($"Deleting '{pkg}'…", () => ContentGitApi.DeleteRemoteFolderAsync(pkg));
                    });
                }
                if (!repoFolders.Contains(pkg) && prdId == null && stgId == null)
                    menu.AddDisabledItem(new GUIContent("Nothing to do"));
                menu.DropDown(moreBtn.worldBound);
            };
            row.Add(moreBtn);
            return row;
        }

        private VisualElement BuildRow(string folder)
        {
            var isCheckedOut = checkedOutFolders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            var isOnRemote   = repoFolders.Contains(folder);
            folderStatuses.TryGetValue(folder, out var status);

            var row = rowTemplate.CloneTree();

            // Icons — left to right as they appear in the row
            row.Q<Image>("img-checkout").image   = LoadIcon("folder-down");
            row.Q<Image>("img-disconnect").image = LoadIcon("x");
            row.Q<Image>("img-pull").image       = LoadIcon("arrow-down-to-line");
            row.Q<Image>("img-push").image       = LoadIcon("arrow-up-from-line");

            var nameLabel     = row.Q<Label>("folder-name-label");
            var renameField   = row.Q<TextField>("folder-rename-field");
            var badge         = row.Q<VisualElement>("folder-badge-group");
            var checkoutBtn   = row.Q<Button>("btn-checkout");
            var disconnectBtn = row.Q<Button>("btn-disconnect");
            var expandBtn     = row.Q<Button>("btn-expand");
            var pullBtn       = row.Q<Button>("btn-pull");
            var pushBtn       = row.Q<Button>("btn-push");
            var moreBtn       = row.Q<Button>("btn-more");
            var fileList      = row.Q<VisualElement>("file-list");

            nameLabel.text            = folder;
            renameField.style.display = DisplayStyle.None;
            checkoutBtn.style.display = isCheckedOut ? DisplayStyle.None : DisplayStyle.Flex;

            var expanded     = false;
            var latestStatus = status;

            var selectedFiles = new Dictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase);
            var entryElements = new Dictionary<string, VisualElement>(StringComparer.OrdinalIgnoreCase);

            void ApplySelectionStyles()
            {
                foreach (var kv in entryElements)
                {
                    if (selectedFiles.ContainsKey(kv.Key)) kv.Value.AddToClassList("cs-file-entry--selected");
                    else                                    kv.Value.RemoveFromClassList("cs-file-entry--selected");
                }
            }

            void RebuildFileList()
            {
                fileList.Clear();
                entryElements.Clear();
                var validPaths = latestStatus.Files == null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(latestStatus.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var k in selectedFiles.Keys.Where(k => !validPaths.Contains(k)).ToList())
                    selectedFiles.Remove(k);
                if (latestStatus.Files == null || latestStatus.Files.Count == 0) return;

                var localPath = ContentRepoSettings.instance.LocalPath.Replace('\\', '/').TrimEnd('/');
                var mainFiles = latestStatus.Files
                    .Where(fc => !fc.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToList();
                var metaSet = new HashSet<string>(
                    latestStatus.Files
                        .Where(fc => fc.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        .Select(fc => fc.Path),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var fc in mainFiles)
                {
                    var pfx       = folder + "/";
                    var rel       = fc.Path.StartsWith(pfx, StringComparison.OrdinalIgnoreCase) ? fc.Path.Substring(pfx.Length) : fc.Path;
                    var assetPath = fc.Kind != ChangeKind.Deleted ? $"{localPath}/{fc.Path}" : null;
                    var hasMeta   = metaSet.Contains(fc.Path + ".meta");
                    var entry     = MakeFileEntry(rel, fc.Kind, fc.Path, assetPath, hasMeta);
                    entryElements[fc.Path] = entry;
                    fileList.Add(entry);
                }
                ApplySelectionStyles();
            }

            VisualElement MakeFileEntry(string displayName, ChangeKind kind, string repoPath, string assetPath, bool hasMeta)
            {
                var entry = new VisualElement();
                entry.AddToClassList("cs-file-entry");
                entry.AddToClassList(kind == ChangeKind.Added   ? "cs-file-entry--added"
                                   : kind == ChangeKind.Deleted ? "cs-file-entry--deleted"
                                                                 : "cs-file-entry--modified");

                var sym = new Label(kind == ChangeKind.Added ? "+" : kind == ChangeKind.Deleted ? "−" : "~");
                sym.AddToClassList("cs-file-prefix");
                sym.pickingMode = PickingMode.Ignore;

                var lbl = new Label(displayName);
                lbl.AddToClassList("cs-file-name");
                lbl.pickingMode = PickingMode.Ignore;

                entry.Add(sym);
                entry.Add(lbl);

                if (hasMeta)
                {
                    var metaTag = new Label("·meta");
                    metaTag.AddToClassList("cs-file-meta-indicator");
                    metaTag.pickingMode = PickingMode.Ignore;
                    entry.Add(metaTag);
                }

                entry.RegisterCallback<ClickEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    var ctrl = evt.ctrlKey || evt.commandKey;
                    if (!ctrl) selectedFiles.Clear();
                    if (selectedFiles.ContainsKey(repoPath)) selectedFiles.Remove(repoPath);
                    else                                     selectedFiles[repoPath] = kind;
                    ApplySelectionStyles();

                    if (assetPath != null && !ctrl)
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
                    }
                    evt.StopPropagation();
                });

                entry.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    if (!selectedFiles.ContainsKey(repoPath))
                    { selectedFiles.Clear(); selectedFiles[repoPath] = kind; ApplySelectionStyles(); }

                    var hasModified = selectedFiles.Values.Any(k => k != ChangeKind.Added);
                    var hasAdded    = selectedFiles.Values.Any(k => k == ChangeKind.Added);
                    var count       = selectedFiles.Count;
                    var label       = count == 1 ? $"\"{displayName}\"" : $"{count} files";

                    if (hasModified)
                        evt.menu.AppendAction($"Discard changes ({label})", _ =>
                        {
                            var paths = selectedFiles.Keys.ToList();
                            var kinds = selectedFiles.Values.ToList();
                            var __ = RunAsync("Discarding changes…", () => ContentGitApi.DiscardFilesAsync(paths, kinds));
                        });
                    if (hasAdded)
                        evt.menu.AppendAction($"Delete ({label})", _ =>
                        {
                            if (!EditorUtility.DisplayDialog("Delete files", $"Permanently delete {label} from disk?", "Delete", "Cancel")) return;
                            var paths = selectedFiles.Where(p => p.Value == ChangeKind.Added).Select(p => p.Key).ToList();
                            var __ = RunAsync("Deleting files…", () => ContentGitApi.DeleteLocalFilesAsync(paths));
                        });
                    evt.menu.AppendAction($"Commit and push ({label})…", _ => PromptAndCommit());
                }));

                return entry;
            }

            var platform     = EditorUserBuildSettings.activeBuildTarget.ToString();
            var stgId        = stagingManifest?.Find(folder)?.FindPlatform(platform)?.buildId;
            var prdId        = productionManifest?.Find(folder)?.FindPlatform(platform)?.buildId;
            var promoteReady = IsPromoteReady(folder);
            var localBuildId = ContentBuildApi.GetLatestBuild(folder, platform)?.buildId;

            void UpdateRowState(FolderStatus s)
            {
                latestStatus = s;
                ApplyBadgeState(badge, isCheckedOut, s);
                AddSyncBadges(badge, folder, stgId, prdId);
                var dirty               = isCheckedOut && !s.IsClean;
                pullBtn.style.display       = isCheckedOut && ContentGitApi.RepositoryBehind > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                pushBtn.style.display       = dirty ? DisplayStyle.Flex : DisplayStyle.None;
                expandBtn.style.display     = dirty ? DisplayStyle.Flex : DisplayStyle.None;
                disconnectBtn.style.display = isCheckedOut && !dirty && isOnRemote ? DisplayStyle.Flex : DisplayStyle.None;
                if (!dirty && expanded) { expanded = false; expandBtn.text = "▶"; fileList.style.display = DisplayStyle.None; }
                if (expanded) RebuildFileList();
            }
            rowUpdaters[folder] = UpdateRowState;
            UpdateRowState(status);

            // Button handlers — in visual left-to-right order
            checkoutBtn.clicked   += () => _ = RunAsync($"Checking out '{folder}'…",  () => ContentGitApi.CheckOutFolderAsync(folder));
            disconnectBtn.clicked += () =>
            {
                if (EditorUtility.DisplayDialog("Disconnect",
                    $"Remove '{folder}' from sparse-checkout?\nUncommitted changes will be lost.", "Disconnect", "Cancel"))
                    _ = RunAsync($"Disconnecting '{folder}'…", () => ContentGitApi.DisconnectFolderAsync(folder));
            };
            expandBtn.clicked += () =>
            {
                expanded           = !expanded;
                expandBtn.text     = expanded ? "▼" : "▶";
                fileList.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                if (expanded) RebuildFileList();
            };
            pullBtn.clicked += () => _ = RunAsync($"Pulling '{folder}'…", () => ContentGitApi.PullFolderAsync(folder));

            void EnterRenameMode()
            {
                renameField.SetValueWithoutNotify(folder);
                nameLabel.style.display   = DisplayStyle.None;
                renameField.style.display = DisplayStyle.Flex;
                renameField.Focus();
                renameField.SelectAll();
            }
            void ExitRenameMode(bool commit)
            {
                nameLabel.style.display   = DisplayStyle.Flex;
                renameField.style.display = DisplayStyle.None;
                if (!commit) return;
                var n = renameField.value?.Trim();
                if (!string.IsNullOrEmpty(n) && n != folder)
                    _ = RunAsync($"Renaming '{folder}'…", () => ContentGitApi.RenameFolderAsync(folder, n));
            }
            renameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if      (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { evt.StopPropagation(); ExitRenameMode(true);  }
                else if (evt.keyCode == KeyCode.Escape)                                       { evt.StopPropagation(); ExitRenameMode(false); }
            });

            void PromptAndCommit()
            {
                var pfx       = folder + "/";
                var fileNames = (latestStatus.Files ?? new List<FileChange>())
                    .Select(f => f.Path.StartsWith(pfx, StringComparison.OrdinalIgnoreCase) ? f.Path.Substring(pfx.Length) : f.Path)
                    .ToList();
                CommitConfirmWindow.Show($"Content Updates {folder}", fileNames,
                    msg => _ = RunAsync($"Committing '{folder}'…", () => ContentGitApi.CommitAndPushFolderAsync(folder, msg)));
            }

            pushBtn.clicked += () => PromptAndCommit();

            moreBtn.clicked += () =>
            {
                var dirty = isCheckedOut && !latestStatus.IsClean;
                var menu  = new GenericMenu();
                if (packageSync.TryGetValue(folder, out var lostSync) && lostSync.Loss != null)
                {
                    menu.AddItem(new GUIContent("Lost changes…"), false, () => _ = ShowLostChangesAsync(folder));
                    menu.AddSeparator("");
                }
                menu.AddItem(new GUIContent("Rename Package"), false, () => EnterRenameMode());
                if (dirty && isOnRemote)
                {
                    menu.AddItem(new GUIContent("Disconnect"), false, () =>
                    {
                        if (EditorUtility.DisplayDialog("Disconnect",
                            $"Remove '{folder}' from sparse-checkout?\nUncommitted changes will be lost.", "Disconnect", "Cancel"))
                            _ = RunAsync($"Disconnecting '{folder}'…", () => ContentGitApi.DisconnectFolderAsync(folder));
                    });
                }
                menu.AddItem(new GUIContent("Build and Deploy"), false, () =>
                    _ = RunBuildAsync(new[] { folder }, $"Build and Deploy '{folder}'…",
                        async () =>
                        {
                            var statusLbl = row.Q<Label>(null, "cs-pipeline-status");
                            SetPipelineStatus(statusLbl, "running");
                            try
                            {
                                await ContentBuildApi.BuildContentPackageAsync(folder, AppendDeployLog);
                                await ContentUploadApi.UploadContentPackageAsync(folder, StagingKey(), log: AppendDeployLog);
                                SetPipelineStatus(statusLbl, "ok");
                            }
                            catch { SetPipelineStatus(statusLbl, "err"); throw; }
                            finally { await RefreshManifestsAsync(); }
                        }));
                if (stgId != null)
                {
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Remove from staging"), false, () =>
                    {
                        var msg = stgId == prdId
                            ? $"Remove '{folder}' from the staging manifest?\nProduction shares this build, so its bundles are kept."
                            : $"Remove '{folder}' from staging and delete its bundles from S3?\nThis cannot be undone.";
                        if (EditorUtility.DisplayDialog("Remove from staging", msg, "Remove", "Cancel"))
                            _ = RunPipelineAsync($"Removing '{folder}' from staging…",
                                async () => { await ContentUploadApi.RemoveFromStagingAsync(folder, platform, AppendDeployLog); await RefreshManifestsAsync(); },
                                deployLog);
                    });
                }
                var production3 = ContentUploadSettings.instance.ProductionPrefix;
                if (prdId != null)
                {
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Remove from production"), false, () =>
                    {
                        if (EditorUtility.DisplayDialog("Remove from production",
                            $"Remove '{folder}' from the production manifest?\nThis does not delete any files.", "Remove", "Cancel"))
                            _ = RunPipelineAsync($"Removing '{folder}' from production…",
                                async () => { await ContentUploadApi.RemoveFromManifestAsync(folder, production3, AppendDeployLog); await RefreshManifestsAsync(); },
                                deployLog);
                    });
                }
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete from repository"), false, () =>
                {
                    if (EditorUtility.DisplayDialog("Delete remote",
                        $"Permanently delete '{folder}' from the remote?\nThis cannot be undone.", "Delete", "Cancel"))
                        _ = RunAsync($"Deleting '{folder}'…", () => ContentGitApi.DeleteRemoteFolderAsync(folder));
                });
                menu.DropDown(moreBtn.worldBound);
            };

            // ── Integrated deploy buttons (inserted before ⋮, reverse-order due to Insert semantics) ──
            // Desired visual order: commit(pushBtn) | build | upload | promote | deployStatus | ⋮
            // Each Insert(moreBtnIndex) prepends, so insert last-visible item first.
            var rowBody      = row.Q<VisualElement>("row-body");
            var moreBtnIndex = rowBody.IndexOf(moreBtn);

            var deployStatus = new Label();
            deployStatus.AddToClassList("cs-pipeline-status");
            deployStatus.AddToClassList("cs-pipeline-status--idle");
            deployStatus.style.display = DisplayStyle.None;
            rowBody.Insert(moreBtnIndex, deployStatus);   // inserted 1st → ends up rightmost

            var promoteBtn = new Button(() => _ = PromoteAsync(folder));
            promoteBtn.AddToClassList("cs-icon-btn");
            promoteBtn.AddToClassList("cs-icon-btn--lg");
            promoteBtn.tooltip = $"Promote '{folder}' to production\n{stgId}";
            promoteBtn.style.display = promoteReady ? DisplayStyle.Flex : DisplayStyle.None;
            var promoteImg = new Image { pickingMode = PickingMode.Ignore, image = LoadIcon("rocket") };
            promoteImg.AddToClassList("cs-icon-image");
            promoteBtn.Add(promoteImg);
            rowBody.Insert(moreBtnIndex, promoteBtn);     // inserted 2nd → ends up left of deployStatus

            var uploadBtn = new Button(() => _ = RunPipelineAsync($"Uploading '{folder}'…",
                async () =>
                {
                    SetPipelineStatus(deployStatus, "running");
                    try   { await ContentUploadApi.UploadContentPackageAsync(folder, StagingKey(), log: AppendDeployLog); SetPipelineStatus(deployStatus, "ok"); }
                    catch { SetPipelineStatus(deployStatus, "err"); throw; }
                    finally { await RefreshManifestsAsync(); }
                }, deployLog));
            uploadBtn.AddToClassList("cs-icon-btn");
            uploadBtn.AddToClassList("cs-icon-btn--lg");
            uploadBtn.tooltip       = $"Upload '{folder}' to staging\n{localBuildId}";
            uploadBtn.style.display = IsUploadReady(folder, platform) ? DisplayStyle.Flex : DisplayStyle.None;
            var uploadImg = new Image { pickingMode = PickingMode.Ignore, image = LoadIcon("cloud-upload") };
            uploadImg.AddToClassList("cs-icon-image");
            uploadBtn.Add(uploadImg);
            rowBody.Insert(moreBtnIndex, uploadBtn);      // inserted 3rd → ends up left of promote

            var buildBtn = new Button(() => _ = RunBuildAsync(new[] { folder }, $"Building '{folder}'…",
                async () =>
                {
                    SetPipelineStatus(deployStatus, "running");
                    try   { await ContentBuildApi.BuildContentPackageAsync(folder, AppendDeployLog); SetPipelineStatus(deployStatus, "ok"); Rebuild(); }
                    catch { SetPipelineStatus(deployStatus, "err"); throw; }
                }));
            buildBtn.AddToClassList("cs-icon-btn");
            buildBtn.AddToClassList("cs-icon-btn--lg");
            buildBtn.tooltip = $"Build '{folder}'";
            var buildImg = new Image { pickingMode = PickingMode.Ignore, image = LoadIcon("hammer") };
            buildImg.AddToClassList("cs-icon-image");
            buildBtn.Add(buildImg);
            rowBody.Insert(moreBtnIndex, buildBtn);       // inserted 4th → ends up leftmost (after pushBtn)

            return row;
        }

        // ── Row UI helpers ────────────────────────────────────────────────────

        private static void SetPipelineStatus(Label status, string state)
        {
            if (status == null) return;
            status.RemoveFromClassList("cs-pipeline-status--idle");
            status.RemoveFromClassList("cs-pipeline-status--ok");
            status.RemoveFromClassList("cs-pipeline-status--err");
            status.RemoveFromClassList("cs-pipeline-status--running");
            switch (state)
            {
                case "ok":
                    status.text = "ok";      status.AddToClassList("cs-pipeline-status--ok");      status.style.display = DisplayStyle.Flex; break;
                case "err":
                    status.text = "error";   status.AddToClassList("cs-pipeline-status--err");     status.style.display = DisplayStyle.Flex; break;
                case "running":
                    status.text = "running"; status.AddToClassList("cs-pipeline-status--running"); status.style.display = DisplayStyle.Flex; break;
                default:
                    status.text = string.Empty; status.AddToClassList("cs-pipeline-status--idle"); status.style.display = DisplayStyle.None; break;
            }
        }

        private void ApplyBadgeState(VisualElement group, bool isCheckedOut, FolderStatus status)
        {
            group.Clear();
            if (!isCheckedOut)
                group.Add(MakeBadge("not checked out", "cs-badge--off"));
            else if (status.IsClean)
                group.Add(MakeBadge("clean", "cs-badge--on"));
            else
            {
                if (status.Untracked > 0)                group.Add(MakeBadge($"+{status.Untracked}",               "cs-badge--new"));
                if (status.Modified + status.Staged > 0) group.Add(MakeBadge($"{status.Modified + status.Staged}", "cs-badge--modified"));
                if (status.Deleted > 0)                  group.Add(MakeBadge($"-{status.Deleted}",                 "cs-badge--deleted"));
            }
        }

        private static Label MakeBadge(string text, string cls, string tooltip = null)
        {
            var l = new Label(text) { tooltip = tooltip };
            l.AddToClassList("cs-badge");
            l.AddToClassList(cls);
            return l;
        }

        private void AddSyncBadges(VisualElement group, string package, string stagingBuildId, string productionBuildId)
        {
            packageSync.TryGetValue(package, out var sync);
            if (sync?.Loss != null)
            {
                var lost = MakeIconBadge("repo lost changes", "triangle-alert", "cs-badge--sync-error",
                    ContentSyncApi.DescribeLoss(sync.Loss) + "\nA build made from the repository lacks that work.\n\nClick to see all files and restore them.");
                lost.AddToClassList("cs-badge--clickable");
                lost.RegisterCallback<ClickEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    evt.StopPropagation();
                    _ = ShowLostChangesAsync(package);
                });
                group.Add(lost);
            }
            if (sync != null && sync.Unpulled.Count > 0)
                group.Add(MakeBadge($"{sync.Unpulled.Count} to pull", "cs-badge--sync-warn",
                    "Commits on the remote that are not pulled yet:\n  " + string.Join("\n  ", sync.Unpulled.Take(8)) +
                    "\nA build made now will not contain them."));
            if (stagingBuildId != null)
                group.Add(MakeDeployBadge(ContentUploadSettings.instance.StagingPrefix, stagingBuildId, sync?.Staging, "cs-badge--stg", false));
            if (productionBuildId != null)
            {
                var waitingForPromote = sync?.Staging?.State == ContentSyncState.InSync
                    && sync.Production?.State == ContentSyncState.Behind && IsPromoteReady(package);
                group.Add(MakeDeployBadge(ContentUploadSettings.instance.ProductionPrefix, productionBuildId, sync?.Production, "cs-badge--prod", waitingForPromote));
            }
        }

        private static VisualElement MakeDeployBadge(string environment, string buildId, ContentDeployStatus status, string environmentClass, bool waitingForPromote)
        {
            var text       = $"{environment}: {buildId[..Math.Min(8, buildId.Length)]}";
            var stateClass = environmentClass;
            string icon    = null;
            switch (status?.State)
            {
                case ContentSyncState.InSync when status.Verified:
                    icon = "circle-check";
                    break;
                case ContentSyncState.Behind when waitingForPromote:
                    text += $" · {status.NewerCommits.Count} behind";
                    break;
                case ContentSyncState.Behind:
                    icon = "triangle-alert";
                    text += status.NewerCommits.Count > 0 ? $" · {status.NewerCommits.Count} behind" : " · differs";
                    stateClass = "cs-badge--sync-warn";
                    break;
                case ContentSyncState.NotInRepository:
                    icon = "triangle-alert";
                    text += " · not in repo";
                    stateClass = "cs-badge--sync-warn";
                    break;
                case ContentSyncState.Removed:
                    icon = "triangle-alert";
                    text += " · removed";
                    stateClass = "cs-badge--sync-warn";
                    break;
                case ContentSyncState.RepositoryLostChanges:
                    icon = "triangle-alert";
                    text += " · repo lost changes";
                    stateClass = "cs-badge--sync-error";
                    break;
                case ContentSyncState.Unknown:
                    icon = "triangle-alert";
                    text += " · unverified";
                    stateClass = "cs-badge--sync-warn";
                    break;
            }

            var badge = new VisualElement
            {
                tooltip = status != null ? ContentSyncApi.Describe(environment, status, waitingForPromote) : $"Live on {environment}\n{buildId}",
            };
            badge.AddToClassList("cs-badge");
            badge.AddToClassList("cs-deploy-badge");
            badge.AddToClassList(stateClass);
            badge.Add(new Label(text) { pickingMode = PickingMode.Ignore });
            if (icon != null)
            {
                var image = new Image { pickingMode = PickingMode.Ignore, image = LoadIcon(icon), scaleMode = ScaleMode.StretchToFill };
                image.AddToClassList("cs-deploy-badge-icon");
                badge.Add(image);
            }
            return badge;
        }

        private async System.Threading.Tasks.Task ShowLostChangesAsync(string package)
        {
            if (busy) return;
            ContentLossDetails details = null;
            SetBusy(true, $"Collecting the lost changes of '{package}'…");
            try { details = await ContentSyncApi.GetLossDetailsAsync(package); }
            catch (Exception ex) { SetStatus($"Error: {ex.Message}"); Debug.LogException(ex); return; }
            finally { SetBusy(false); }

            if (details == null)
            {
                SetStatus($"'{package}' has no lost changes on {ContentSyncApi.RemoteRef} any more.");
                _ = RunAsync("Refreshing…", () => System.Threading.Tasks.Task.CompletedTask);
                return;
            }
            SetStatus("Ready");
            var checkedOut = checkedOutFolders.Any(f => f.Equals(package, StringComparison.OrdinalIgnoreCase));
            var unpulled = checkedOut ? (await ContentSyncApi.GetUnpulledCommitsAsync(package)).Count : 0;
            LostChangesWindow.Show(details, checkedOut, unpulled, confirmed =>
            {
                if (busy) return false;
                _ = RunAsync($"Restoring the lost changes of '{package}'…", async () =>
                {
                    // Work from the repository as it is now, not as it was when the window opened.
                    var current = await ContentSyncApi.GetLossDetailsAsync(package)
                        ?? throw new InvalidOperationException($"'{package}' has no lost changes on {ContentSyncApi.RemoteRef} any more. Nothing was restored.");
                    if (current.PendingCount != confirmed.PendingCount || current.MergeId != confirmed.MergeId)
                        throw new InvalidOperationException("The lost changes changed since the window opened. Nothing was restored; open Lost changes again.");
                    var restored = await ContentGitApi.RestoreLostChangesAsync(current);
                    AppendDeployLog($"[Restore] '{package}': {restored} file(s) restored from what merge {current.Merge?.Sha} dropped. " +
                                    "Check them, then commit and push. The red badge clears once the push reaches the remote.");
                });
                return true;
            });
        }

        private static VisualElement MakeIconBadge(string text, string icon, string stateClass, string tooltip)
        {
            var badge = new VisualElement { tooltip = tooltip };
            badge.AddToClassList("cs-badge");
            badge.AddToClassList("cs-deploy-badge");
            badge.AddToClassList(stateClass);
            badge.Add(new Label(text) { pickingMode = PickingMode.Ignore });
            var image = new Image { pickingMode = PickingMode.Ignore, image = LoadIcon(icon), scaleMode = ScaleMode.StretchToFill };
            image.AddToClassList("cs-deploy-badge-icon");
            badge.Add(image);
            return badge;
        }

        // ── Async runners ─────────────────────────────────────────────────────

        private async Task RunAsync(string statusMessage, Func<Task> op)
        {
            if (busy) return;
            ContentGitApi.ClearLastWarning();
            SetBusy(true, statusMessage);
            try { await op(); }
            catch (Exception ex) { SetStatus($"Error: {ex.Message}"); Debug.LogException(ex); SetBusy(false); return; }
            try
            {
                SetStatus("Refreshing…");
                await RefreshDataAsync();
                await RefreshSyncAsync();
                Rebuild();
                UpdateToolbarVisibility();
                SetStatus(!string.IsNullOrEmpty(ContentGitApi.LastWarning) ? $"⚠ {ContentGitApi.LastWarning}" : "Ready");
            }
            catch (Exception ex) { SetStatus($"Error: {ex.Message}"); Debug.LogException(ex); }
            finally { SetBusy(false); }
        }

        private async Task RunPipelineAsync(string statusMessage, Func<Task> op, Label log)
        {
            if (busy) return;
            SetBusy(true, statusMessage);
            AppendLog(log, $"--- {statusMessage} ---");
            try   { await op(); SetStatus("Done."); }
            catch (Exception ex) { SetStatus($"Error: {ex.Message}"); AppendLog(log, $"ERROR: {ex.Message}"); Debug.LogException(ex); }
            finally { Rebuild(); SetBusy(false); }
        }

        private async Task RunBuildAsync(IReadOnlyList<string> packages, string statusMessage, Func<Task> op)
        {
            if (busy) return;
            if (!await ConfirmBuildAsync(packages))
            {
                SetStatus("Ready");
                return;
            }
            await RunPipelineAsync(statusMessage, op, deployLog);
        }

        private async Task<bool> ConfirmBuildAsync(IReadOnlyList<string> packages)
        {
            var warnings = new List<string>();
            string operation;
            bool conflicts;
            SetBusy(true, "Checking the content repository for newer commits…");
            try
            {
                if (!await ContentSyncApi.FetchAsync())
                    warnings.Add($"Could not fetch the content repository, so commits that teammates pushed recently could not be checked. " +
                                 $"The check below uses {ContentSyncApi.RemoteRef} as last fetched.");
                operation = await ContentGitApi.GetOperationInProgressAsync();
                conflicts = await ContentGitApi.HasUnresolvedConflictsAsync();
                var remote = await ContentSyncApi.ResolveRemoteAsync();
                foreach (var package in packages)
                {
                    var loss = remote == null ? null : await ContentSyncApi.FindUnrepairedLossAsync(package, remote);
                    if (loss != null)
                        warnings.Add($"'{package}': {ContentSyncApi.DescribeLoss(loss)}\nA build made from the repository lacks that work.");
                    var commits = await ContentSyncApi.GetUnpulledCommitsAsync(package, remote);
                    if (commits.Count == 0) continue;
                    warnings.Add($"'{package}' has {(commits.Count == 1 ? "1 commit" : $"{commits.Count} commits")} on the remote that you have not pulled:\n  " +
                                 string.Join("\n  ", commits.Take(8)) + "\nA build made now will not contain them.");
                }
            }
            finally { SetBusy(false); }

            if (operation != null || conflicts)
            {
                EditorUtility.DisplayDialog("Build", operation != null
                    ? $"The content repository is in the middle of a {operation}. Finish or abort it before building."
                    : "The content repository has files with unresolved conflicts. Resolve them in your git client before building.", "OK");
                return false;
            }
            return warnings.Count == 0 || EditorUtility.DisplayDialog("Check before building",
                string.Join("\n\n", warnings) + "\n\nBuild anyway?", "Build anyway", "Cancel");
        }

        /// <summary>Promotes one package, or every checked-out package when <paramref name="package"/> is null.</summary>
        private async Task PromoteAsync(string package)
        {
            if (busy) return;
            var staging    = ContentUploadSettings.instance.StagingPrefix;
            var production = ContentUploadSettings.instance.ProductionPrefix;

            var warnings = new List<string>();
            SetBusy(true, "Comparing staging with the content repository…");
            try
            {
                if (!await ContentSyncApi.FetchAsync())
                    warnings.Add($"Could not fetch the content repository, so this compares with {ContentSyncApi.RemoteRef} as last fetched " +
                                 "and may miss commits that teammates pushed recently.");
                stagingManifest    = await ContentUploadApi.GetManifestAsync(staging) ?? stagingManifest;
                productionManifest = await ContentUploadApi.GetManifestAsync(production) ?? productionManifest;
                await RefreshSyncAsync();

                var remote = await ContentSyncApi.ResolveRemoteAsync();
                foreach (var candidate in package != null ? new List<string> { package } : checkedOutFolders.ToList())
                {
                    var stagingEntry = stagingManifest?.Find(candidate);
                    if (stagingEntry == null) continue;
                    var productionEntry = productionManifest?.Find(candidate);
                    var changing = stagingEntry.platforms
                        .Where(entry => !IsSameDeployment(entry, productionEntry?.FindPlatform(entry.platform)))
                        .ToList();
                    if (changing.Count == 0) continue;

                    if (packageSync.TryGetValue(candidate, out var sync) && sync.Loss != null)
                        warnings.Add($"'{candidate}': {ContentSyncApi.DescribeLoss(sync.Loss)}");
                    foreach (var entry in changing)
                    {
                        var status = await ContentSyncApi.GetDeployStatusAsync(candidate, stagingEntry, entry.platform, remote);
                        if (status.NeedsAttention)
                            warnings.Add($"'{candidate}' ({entry.platform}) on {staging}: {ContentSyncApi.DescribeState(status, staging)}");
                    }
                }
            }
            finally { SetBusy(false); }

            var subject = package != null ? $"'{package}'" : "all checked-out packages";
            var confirmed = warnings.Count > 0
                ? EditorUtility.DisplayDialog("Check before promoting",
                    string.Join("\n\n", warnings) + $"\n\nPromote {subject} from {staging} → {production} anyway?",
                    "Promote anyway", "Cancel")
                : package != null
                    ? EditorUtility.DisplayDialog("Promote", $"Promote '{package}' from {staging} → {production}?", "Promote", "Cancel")
                    : EditorUtility.DisplayDialog("Promote all to production",
                        $"Promote all checked-out packages from {staging} → {production}?\nOnly the manifest is updated — no files move.",
                        "Promote", "Cancel");
            if (!confirmed)
            {
                SetStatus("Ready");
                Rebuild();
                return;
            }

            if (warnings.Count > 0)
                AppendDeployLog($"[Promote] WARNING: promoting despite these warnings:\n{string.Join("\n", warnings)}");
            await RunPipelineAsync(package != null ? $"Promoting '{package}'…" : "Promoting all → production…",
                async () =>
                {
                    if (package != null) await ContentUploadApi.PromoteContentPackageAsync(package, staging, production, AppendDeployLog);
                    else await ContentUploadApi.PromoteAllAsync(staging, production, AppendDeployLog);
                    await RefreshManifestsAsync();
                }, deployLog);
        }

        private async Task PollStatusesAsync()
        {
            if (busy || isInitialized != true || polling) return;
            polling = true;
            try
            {
                await RefreshOperationStateAsync();
                if (checkedOutFolders.Count == 0) return;
                var statuses = await ContentGitApi.GetAllFolderStatusesAsync();
                MergeGroupsStatusInto(checkedOutFolders, statuses);
                foreach (var kvp in rowUpdaters)
                {
                    statuses.TryGetValue(kvp.Key, out var s);
                    kvp.Value(s);
                    if (checkedOutFolders.Any(c => c.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)))
                        folderStatuses[kvp.Key] = s;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"Status poll: {ex.Message}"); }
            finally { polling = false; }
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void OnExternalStateChanged() =>
            EditorApplication.delayCall += () =>
            {
                if (this == null || busy) return;
                _ = RunAsync("Refreshing…", () => Task.CompletedTask);
            };

        private void OnProjectChanged()
        {
            if (projectChangedPending) return;
            projectChangedPending = true;
            EditorApplication.delayCall += () =>
            {
                projectChangedPending = false;
                if (this == null || busy) return;
                _ = RunAsync("Refreshing…", () => Task.CompletedTask);
            };
        }

        // ── Logging ───────────────────────────────────────────────────────────

        private void AppendDeployLog(string line) => AppendLog(deployLog, line);

        private void AppendLog(Label log, string line)
        {
            if (log == null) return;
            EditorApplication.delayCall += () =>
            {
                if (log == null) return;
                var t = log.text ?? "";
                if (t.Length > 8000) t = t.Substring(t.Length - 6000);
                log.text = t + line + "\n";
            };
        }

        // ── Busy / spinner ────────────────────────────────────────────────────

        private void SetBusy(bool b, string message = null)
        {
            busy = b;
            SetSpinnerVisible(b);
            SetButtonsEnabled(!b);
            if (message != null) SetStatus(message);
        }

        private void SetSpinnerVisible(bool visible)
        {
            if (spinner == null) return;
            if (visible)
            {
                spinner.AddToClassList("cs-spinner--visible");
                if (spinnerTick == null)
                {
                    var angle = 0f;
                    spinnerTick = spinner.schedule.Execute(() =>
                    {
                        angle = (angle + 30f) % 360f;
                        spinner.style.rotate = new StyleRotate(new Rotate(new Angle(angle, AngleUnit.Degree)));
                    }).Every(80);
                }
            }
            else
            {
                spinner.RemoveFromClassList("cs-spinner--visible");
                spinnerTick?.Pause();
                spinnerTick = null;
            }
        }

        private void SetButtonsEnabled(bool enabled) =>
            rootVisualElement.Query<Button>().ForEach(b =>
            {
                if (b.name == "btn-new-folder") return;
                b.SetEnabled(enabled);
            });

        private void SetStatus(string msg) { if (statusLabel != null) statusLabel.text = msg; }

        // ── Static data helpers ───────────────────────────────────────────────

        private static void MergeGroupsStatusInto(IReadOnlyList<string> packages, Dictionary<string, FolderStatus> statuses)
        {
            if (!statuses.TryGetValue(ContentGitApi.GroupsFolderName, out var groupsStatus) || groupsStatus.Files == null)
                return;

            foreach (var fc in groupsStatus.Files)
            {
                var pkg   = ExtractPackageFromGroupFile(fc.Path);
                var match = pkg == null ? null : packages.FirstOrDefault(p => p.Equals(pkg, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;

                statuses.TryGetValue(match, out var s);
                if (s.Files == null) s.Files = new List<FileChange>();
                s.Files.Add(fc);
                if (!fc.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    switch (fc.Kind)
                    {
                        case ChangeKind.Added:    s.Untracked++; break;
                        case ChangeKind.Modified: s.Modified++;  break;
                        case ChangeKind.Deleted:  s.Deleted++;   break;
                    }
                }
                statuses[match] = s;
            }
        }

        private static string ExtractPackageFromGroupFile(string repoRelativePath)
        {
            var parts = repoRelativePath.Replace('\\', '/').Split('/');
            if (parts.Length != 2 || !parts[0].Equals(ContentGitApi.GroupsFolderName, StringComparison.OrdinalIgnoreCase))
                return null;
            return ContentGitApi.PackageOfGroupFile(parts[1]);
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private string StagingKey() => ContentUploadSettings.instance.StagingPrefix;

        private sealed class PackageSync
        {
            public ContentDeployStatus Staging;
            public ContentDeployStatus Production;
            public List<ContentCommit> Unpulled = new();
            public ContentMergeLoss Loss;
        }

        private static Texture2D LoadIcon(string name) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>($"{PackageRoot}/Editor/UI/Icons/{name}.png");
    }
}
