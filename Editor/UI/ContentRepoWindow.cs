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

        // Tabs
        private Button tabFolders, tabBuild, tabUpload;
        private VisualElement panelFolders, panelBuild, panelUpload;

        // Folders tab
        private Button refreshBtn, pullAllBtn, newFolderBtn, initBtn;
        private VisualElement setupBanner, newFolderRow;
        private TextField newFolderField;
        private ScrollView folderList;

        // Build tab
        private VisualElement genWarningBanner;
        private Label genWarningLabel, buildEmpty, buildLog;
        private Button bumpGenerationBtn, ackUnityVersionBtn, buildAllBtn;
        private ScrollView buildList;

        // Upload tab
        private EnumField envUploadField;
        private Button validateBtn, uploadAllBtn, publishManifestBtn, promoteAllBtn,
            buildAndUploadAllBtn, deployLambdaBtn, teardownLambdaBtn;
        private Label validateResult, uploadEmpty, uploadLog, stackStatusLabel;
        private ScrollView uploadList;

        // Status bar
        private VisualElement spinner;
        private Label statusLabel;

        // State
        private bool? isInitialized;
        private bool newFolderRowVisible;
        private List<string> remoteFolders = new();
        private List<string> checkedOutFolders = new();
        private Dictionary<string, FolderStatus> folderStatuses = new();
        private readonly Dictionary<string, Action<FolderStatus>> rowUpdaters = new();
        private VisualTreeAsset rowTemplate;
        private bool busy;
        private bool polling;
        private IVisualElementScheduledItem spinnerTick, statusPoller;
        private BuildEnvironment uploadEnv = BuildEnvironment.Staging;

        [MenuItem("Tools/Content Browser")]
        public static void ShowWindow()
        {
            var w = GetWindow<ContentRepoWindow>();
            w.titleContent = new GUIContent("Content Browser");
            w.minSize = new Vector2(580, 400);
        }

        private void OnEnable()
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null) { rootVisualElement.Add(new Label($"Could not load {UxmlPath}")); return; }
            uxml.CloneTree(rootVisualElement);

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            rowTemplate = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(FolderRowUxmlPath);
            ResolveElements();
            WireTabBar();
            WireFolderTab();
            WireBuildTab();
            WireUploadTab();

            envUploadField.Init(uploadEnv);
            ContentGitApi.OnStateChanged += OnExternalStateChanged;

            SetSpinnerVisible(false);
            SetStatus("Ready");
            _ = RunAsync("Loading…", () => Task.CompletedTask);
            statusPoller = rootVisualElement.schedule.Execute(() => _ = PollStatusesAsync()).Every(60_000);
        }

        private void OnDisable()
        {
            ContentGitApi.OnStateChanged -= OnExternalStateChanged;
            spinnerTick?.Pause(); spinnerTick = null;
            statusPoller?.Pause(); statusPoller = null;
        }

        private void ResolveElements()
        {
            tabFolders = Q<Button>("tab-folders"); tabBuild = Q<Button>("tab-build"); tabUpload = Q<Button>("tab-upload");
            panelFolders = Q<VisualElement>("panel-folders"); panelBuild = Q<VisualElement>("panel-build"); panelUpload = Q<VisualElement>("panel-upload");

            refreshBtn = Q<Button>("btn-refresh"); pullAllBtn = Q<Button>("btn-pull-all");
            newFolderBtn = Q<Button>("btn-new-folder"); initBtn = Q<Button>("btn-init");
            setupBanner = Q<VisualElement>("setup-banner"); newFolderRow = Q<VisualElement>("new-folder-row");
            newFolderField = Q<TextField>("new-folder-field"); folderList = Q<ScrollView>("folder-list-container");

            genWarningBanner = Q<VisualElement>("gen-warning-banner"); genWarningLabel = Q<Label>("gen-warning-label");
            bumpGenerationBtn = Q<Button>("btn-bump-generation"); ackUnityVersionBtn = Q<Button>("btn-ack-unity-version");
            buildAllBtn = Q<Button>("btn-build-all"); buildList = Q<ScrollView>("build-list-container");
            buildEmpty = Q<Label>("build-empty"); buildLog = Q<Label>("build-log");
            Q<Image>("img-build-all").image = LoadIcon("hammer");

            envUploadField = Q<EnumField>("env-upload");
            validateBtn = Q<Button>("btn-validate-credentials"); validateResult = Q<Label>("validate-result");
            uploadAllBtn = Q<Button>("btn-upload-all"); publishManifestBtn = Q<Button>("btn-publish-manifest");
            promoteAllBtn = Q<Button>("btn-promote-all"); buildAndUploadAllBtn = Q<Button>("btn-build-and-upload-all");
            uploadList = Q<ScrollView>("upload-list-container");
            uploadEmpty = Q<Label>("upload-empty"); uploadLog = Q<Label>("upload-log");
            stackStatusLabel = Q<Label>("stack-status-label");
            deployLambdaBtn = Q<Button>("btn-deploy-lambda"); teardownLambdaBtn = Q<Button>("btn-teardown-lambda");
            spinner = Q<VisualElement>("progress-spinner"); statusLabel = Q<Label>("status-label");

            newFolderRow.style.display = DisplayStyle.None;
            setupBanner.style.display = DisplayStyle.None;

            Q<Image>("img-new-folder").image = LoadIcon("folder-plus");
            Q<Image>("img-refresh").image = LoadIcon("refresh-cw");
            Q<Image>("img-pull-all").image = LoadIcon("folder-sync");
        }

        private T Q<T>(string name) where T : VisualElement => rootVisualElement.Q<T>(name);

        // ── Tab bar ───────────────────────────────────────────────────────────

        private void WireTabBar()
        {
            tabFolders.clicked += () => SelectTab("folders");
            tabBuild.clicked += () => SelectTab("build");
            tabUpload.clicked += () => SelectTab("upload");
            SelectTab("folders");
        }

        private void SelectTab(string name)
        {
            panelFolders.style.display = name == "folders" ? DisplayStyle.Flex : DisplayStyle.None;
            panelBuild.style.display = name == "build" ? DisplayStyle.Flex : DisplayStyle.None;
            panelUpload.style.display = name == "upload" ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (var t in new[] { tabFolders, tabBuild, tabUpload }) t.RemoveFromClassList("cs-tab--active");
            switch (name)
            {
                case "folders": tabFolders.AddToClassList("cs-tab--active"); break;
                case "build":
                    tabBuild.AddToClassList("cs-tab--active");
                    RefreshGenerationWarning();
                    RebuildPipelineRows();
                    break;
                case "upload":
                    tabUpload.AddToClassList("cs-tab--active");
                    RebuildPipelineRows();
                    _ = RefreshStackStatusAsync();
                    break;
            }
        }

        // ── Folders tab ───────────────────────────────────────────────────────

        private void WireFolderTab()
        {
            refreshBtn.clicked += () => _ = RunAsync("Refreshing…", () => Task.CompletedTask);
            pullAllBtn.clicked += () => _ = RunAsync("Pulling all…", ContentGitApi.PullAllAsync);
            initBtn.clicked += () => _ = RunAsync("Initializing…", ContentGitApi.InitAsync);
            newFolderBtn.clicked += () =>
            {
                newFolderRowVisible = !newFolderRowVisible;
                newFolderRow.style.display = newFolderRowVisible ? DisplayStyle.Flex : DisplayStyle.None;
                if (newFolderRowVisible) newFolderField.Focus();
            };
            Q<Button>("btn-create-folder").clicked += () =>
            {
                var name = newFolderField.value?.Trim();
                if (string.IsNullOrEmpty(name)) { EditorUtility.DisplayDialog("Name required", "Enter a folder name.", "OK"); return; }
                newFolderRowVisible = false; newFolderRow.style.display = DisplayStyle.None; newFolderField.SetValueWithoutNotify("");
                _ = RunAsync($"Creating '{name}'…", () => ContentGitApi.CreateFolderAsync(name));
            };
        }

        // ── Build tab ─────────────────────────────────────────────────────────

        private void WireBuildTab()
        {
            bumpGenerationBtn.clicked += () =>
            {
                ContentRepoGenerationSettings.instance.BumpGeneration();
                RefreshGenerationWarning();
            };
            ackUnityVersionBtn.clicked += () =>
            {
                ContentRepoGenerationSettings.instance.AcknowledgeUnityVersion();
                RefreshGenerationWarning();
            };
            buildAllBtn.clicked += () => _ = RunPipelineAsync(
                "Building all checked-out packages…",
                async () => { await ContentBuildApi.BuildAllCheckedOutAsync(AppendBuildLog); },
                buildLog);
        }

        private void RefreshGenerationWarning()
        {
            var gen = ContentRepoGenerationSettings.instance;
            var change = gen.CheckUnityVersionChange();
            if (change == ContentRepoGenerationSettings.VersionChangeKind.MinorOrMajor)
            {
                genWarningBanner.style.display = DisplayStyle.Flex;
                genWarningLabel.text = $"Unity version changed from {gen.UnityVersionAtGeneration} to {Application.unityVersion}. " +
                                       "Bundle format may be incompatible. Bump the generation before building.";
                ackUnityVersionBtn.style.display = DisplayStyle.None;
                bumpGenerationBtn.style.display = DisplayStyle.Flex;
            }
            else if (change == ContentRepoGenerationSettings.VersionChangeKind.PatchOnly)
            {
                genWarningBanner.style.display = DisplayStyle.Flex;
                genWarningLabel.text = $"Unity patch version changed ({gen.UnityVersionAtGeneration} → {Application.unityVersion}). Bundles are likely compatible.";
                ackUnityVersionBtn.style.display = DisplayStyle.Flex;
                bumpGenerationBtn.style.display = DisplayStyle.None;
            }
            else
            {
                genWarningBanner.style.display = DisplayStyle.None;
            }
        }

        // ── Upload tab ────────────────────────────────────────────────────────

        private void WireUploadTab()
        {
            envUploadField.RegisterValueChangedCallback(evt => { uploadEnv = (BuildEnvironment)evt.newValue; RebuildPipelineRows(); });

            validateBtn.clicked += () => _ = RunPipelineAsync("Validating credentials…", async () =>
            {
                var ok = await ContentUploadProviderFactory.Resolve().ValidateConfigAsync(AppendUploadLog);
                validateResult.text = ok ? "✓ Credentials valid." : "✗ Validation failed — see log.";
            }, uploadLog);

            uploadAllBtn.clicked += () => _ = RunPipelineAsync($"Uploading all ({EnvKey()})…",
                async () => { await ContentUploadApi.UploadAllCheckedOutAsync(EnvKey(), AppendUploadLog); }, uploadLog);

            publishManifestBtn.clicked += () => _ = RunPipelineAsync($"Publishing manifest ({EnvKey()})…",
                () => ContentUploadApi.PublishManifestAsync(EnvKey(), AppendUploadLog), uploadLog);

            promoteAllBtn.clicked += () =>
            {
                var staging = ContentUploadSettings.instance.StagingPrefix;
                var production = ContentUploadSettings.instance.ProductionPrefix;
                if (!EditorUtility.DisplayDialog("Promote all to production",
                    $"Promote all checked-out packages from {staging} → {production}?\nOnly the manifest is updated — no files move.",
                    "Promote", "Cancel")) return;
                _ = RunPipelineAsync($"Promoting all → production…",
                    () => ContentUploadApi.PromoteAllAsync(staging, production, AppendUploadLog), uploadLog);
            };

            buildAndUploadAllBtn.clicked += () => _ = RunPipelineAsync($"Build + Upload all ({EnvKey()})…",
                async () =>
                {
                    await ContentBuildApi.BuildAllCheckedOutAsync(AppendUploadLog);
                    await ContentUploadApi.UploadAllCheckedOutAsync(EnvKey(), AppendUploadLog);
                    await ContentUploadApi.PublishManifestAsync(EnvKey(), AppendUploadLog);
                }, uploadLog);

            deployLambdaBtn.clicked += () => _ = RunPipelineAsync("Deploying cleanup Lambda…",
                async () => { await ContentInfraApi.DeployCleanupLambdaAsync(AppendUploadLog); await RefreshStackStatusAsync(); },
                uploadLog);

            teardownLambdaBtn.clicked += () =>
            {
                if (!EditorUtility.DisplayDialog("Teardown Lambda", "Delete the content-repo-cleanup CloudFormation stack?", "Teardown", "Cancel")) return;
                _ = RunPipelineAsync("Tearing down Lambda…",
                    async () => { await ContentInfraApi.TeardownCleanupLambdaAsync(AppendUploadLog); await RefreshStackStatusAsync(); },
                    uploadLog);
            };
        }

        private async Task RefreshStackStatusAsync()
        {
            try { stackStatusLabel.text = $"Stack: {await ContentInfraApi.GetStackStatusAsync()}"; }
            catch { stackStatusLabel.text = "Stack: unknown"; }
        }

        private string EnvKey() => ContentUploadSettings.instance.GetEnvironmentPrefix(uploadEnv);

        // ── Pipeline rows (Build + Upload tabs) ───────────────────────────────

        private void RebuildPipelineRows()
        {
            buildList.Clear(); uploadList.Clear();
            buildEmpty.style.display = checkedOutFolders.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            uploadEmpty.style.display = buildEmpty.style.display;

            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            foreach (var pkg in checkedOutFolders.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                buildList.Add(BuildPipelineRow(pkg, platform, isUpload: false));
                uploadList.Add(BuildPipelineRow(pkg, platform, isUpload: true));
            }
        }

        private VisualElement BuildPipelineRow(string pkg, string platform, bool isUpload)
        {
            var envKey = EnvKey();
            var row = new VisualElement(); row.AddToClassList("cs-pipeline-row");

            var nameLabel = new Label(pkg); nameLabel.AddToClassList("cs-pipeline-name"); row.Add(nameLabel);

            var meta = new Label(); meta.AddToClassList("cs-build-meta");
            if (isUpload)
            {
                var ts = ContentUploadApi.GetLastUploadTimestamp(pkg, platform, envKey);
                meta.text = ts.HasValue ? $"uploaded {ts.Value.ToLocalTime():MM-dd HH:mm}" : "never uploaded";
            }
            else
            {
                var ts = ContentBuildApi.GetLastBuildTimestamp(pkg, platform);
                var lastResult = ContentBuildApi.GetLastBuildResult(pkg);
                var buildIdStr = lastResult != null ? $"  id:{lastResult.BuildId[..8]}" : "";
                meta.text = ts.HasValue ? $"built {ts.Value.ToLocalTime():MM-dd HH:mm}{buildIdStr}" : "never built";
            }
            row.Add(meta);

            var status = new Label("idle"); status.AddToClassList("cs-pipeline-status"); status.AddToClassList("cs-pipeline-status--idle");
            row.Add(status);
            var spacer = new VisualElement { style = { flexGrow = 1 } }; row.Add(spacer);

            if (isUpload)
            {
                AddBtn(row, "Upload", "btn-upload-package", () => _ = RunPipelineAsync($"Uploading '{pkg}'…",
                    async () =>
                    {
                        SetPipelineStatus(status, "running");
                        try { await ContentUploadApi.UploadContentPackageAsync(pkg, envKey, log: AppendUploadLog); SetPipelineStatus(status, "ok"); }
                        catch { SetPipelineStatus(status, "err"); throw; }
                    }, uploadLog));

                var staging = ContentUploadSettings.instance.StagingPrefix;
                var production = ContentUploadSettings.instance.ProductionPrefix;
                AddBtn(row, "Promote → prod", "btn-promote-package", () =>
                {
                    if (!EditorUtility.DisplayDialog("Promote", $"Promote '{pkg}' from {staging} → {production}?", "Promote", "Cancel")) return;
                    _ = RunPipelineAsync($"Promoting '{pkg}'…",
                        () => ContentUploadApi.PromoteContentPackageAsync(pkg, staging, production, AppendUploadLog),
                        uploadLog);
                });

                AddBtn(row, "Build+Upload", "btn-build-and-upload-package", () => _ = RunPipelineAsync($"Build+Upload '{pkg}'…",
                    async () =>
                    {
                        SetPipelineStatus(status, "running");
                        try
                        {
                            await ContentBuildApi.BuildContentPackageAsync(pkg, AppendUploadLog);
                            await ContentUploadApi.UploadContentPackageAsync(pkg, envKey, log: AppendUploadLog);
                            SetPipelineStatus(status, "ok");
                        }
                        catch { SetPipelineStatus(status, "err"); throw; }
                    }, uploadLog));
            }
            else
            {
                AddBtn(row, "Build", "btn-build-package", () => _ = RunPipelineAsync($"Building '{pkg}'…",
                    async () =>
                    {
                        SetPipelineStatus(status, "running");
                        try { await ContentBuildApi.BuildContentPackageAsync(pkg, AppendBuildLog); SetPipelineStatus(status, "ok"); RebuildPipelineRows(); }
                        catch { SetPipelineStatus(status, "err"); throw; }
                    }, buildLog));
            }

            return row;
        }

        private static void AddBtn(VisualElement row, string text, string name, Action clicked)
        {
            var btn = new Button(clicked) { text = text, name = name };
            btn.AddToClassList("cs-text-btn");
            row.Add(btn);
        }

        private static void SetPipelineStatus(Label status, string state)
        {
            status.RemoveFromClassList("cs-pipeline-status--idle");
            status.RemoveFromClassList("cs-pipeline-status--ok");
            status.RemoveFromClassList("cs-pipeline-status--err");
            status.RemoveFromClassList("cs-pipeline-status--running");
            switch (state)
            {
                case "ok":      status.text = "ok";      status.AddToClassList("cs-pipeline-status--ok"); break;
                case "err":     status.text = "error";   status.AddToClassList("cs-pipeline-status--err"); break;
                case "running": status.text = "running"; status.AddToClassList("cs-pipeline-status--running"); break;
                default:        status.text = "idle";    status.AddToClassList("cs-pipeline-status--idle"); break;
            }
        }

        // ── Folder rows (Folders tab) ─────────────────────────────────────────

        private void Rebuild()
        {
            setupBanner.style.display = isInitialized == false ? DisplayStyle.Flex : DisplayStyle.None;
            rowUpdaters.Clear(); folderList.Clear();
            if (isInitialized != true) return;
            if (remoteFolders.Count == 0)
            {
                folderList.Add(new Label("No folders available.") { style = { unityFontStyleAndWeight = FontStyle.Italic, marginTop = 8, marginLeft = 8 } });
                return;
            }
            foreach (var folder in remoteFolders) folderList.Add(BuildRow(folder));
        }

        private VisualElement BuildRow(string folder)
        {
            var isCheckedOut = checkedOutFolders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            folderStatuses.TryGetValue(folder, out var status);
            var row = rowTemplate.CloneTree();

            row.Q<Image>("img-rename").image     = LoadIcon("pencil");
            row.Q<Image>("img-pull").image       = LoadIcon("arrow-down-to-line");
            row.Q<Image>("img-push").image       = LoadIcon("arrow-up-from-line");
            row.Q<Image>("img-checkout").image   = LoadIcon("folder-down");
            row.Q<Image>("img-disconnect").image = LoadIcon("circle-x");
            row.Q<Image>("img-delete").image     = LoadIcon("trash");

            var nameLabel     = row.Q<Label>("folder-name-label");
            var renameField   = row.Q<TextField>("folder-rename-field");
            var renameBtn     = row.Q<Button>("btn-rename");
            var badge         = row.Q<VisualElement>("folder-badge-group");
            var pullBtn       = row.Q<Button>("btn-pull");
            var pushBtn       = row.Q<Button>("btn-push");
            var checkoutBtn   = row.Q<Button>("btn-checkout");
            var disconnectBtn = row.Q<Button>("btn-disconnect");
            var deleteBtn = row.Q<Button>("btn-delete-remote");
            var expandBtn = row.Q<Button>("btn-expand");
            var fileList  = row.Q<VisualElement>("file-list");

            nameLabel.text = folder;
            renameField.style.display = DisplayStyle.None;
            checkoutBtn.style.display  = isCheckedOut ? DisplayStyle.None : DisplayStyle.Flex;
            disconnectBtn.style.display = isCheckedOut ? DisplayStyle.Flex : DisplayStyle.None;

            var expanded = false;
            FolderStatus latestStatus = status;

            // per-row selection state: repoRoot-relative paths → their ChangeKind
            var selectedFiles = new Dictionary<string, ChangeKind>(StringComparer.OrdinalIgnoreCase);
            var entryElements = new Dictionary<string, VisualElement>(StringComparer.OrdinalIgnoreCase);

            void ApplySelectionStyles()
            {
                foreach (var kv in entryElements)
                {
                    if (selectedFiles.ContainsKey(kv.Key))
                        kv.Value.AddToClassList("cs-file-entry--selected");
                    else
                        kv.Value.RemoveFromClassList("cs-file-entry--selected");
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
                    var rel  = fc.Path.Length > folder.Length + 1 ? fc.Path.Substring(folder.Length + 1) : fc.Path;
                    var assetPath = fc.Kind != ChangeKind.Deleted ? $"{localPath}/{fc.Path}" : null;
                    var hasMeta = metaSet.Contains(fc.Path + ".meta");
                    var entry = MakeFileEntry(rel, fc.Kind, fc.Path, assetPath, hasMeta);
                    entryElements[fc.Path] = entry;
                    fileList.Add(entry);
                }
                ApplySelectionStyles();
            }

            VisualElement MakeFileEntry(string displayName, ChangeKind kind, string repoPath, string assetPath, bool hasMeta)
            {
                var entry = new VisualElement();
                entry.AddToClassList("cs-file-entry");
                entry.AddToClassList(kind == ChangeKind.Added ? "cs-file-entry--added"
                    : kind == ChangeKind.Deleted ? "cs-file-entry--deleted"
                    : "cs-file-entry--modified");

                var sym = new Label(kind == ChangeKind.Added ? "+" : kind == ChangeKind.Deleted ? "−" : "~");
                sym.AddToClassList("cs-file-prefix");
                sym.pickingMode = PickingMode.Ignore;

                var lbl = new Label(displayName);
                lbl.AddToClassList("cs-file-name");
                lbl.pickingMode = PickingMode.Ignore;

                entry.Add(sym); entry.Add(lbl);

                if (hasMeta)
                {
                    var metaTag = new Label("·meta");
                    metaTag.AddToClassList("cs-file-meta-indicator");
                    metaTag.pickingMode = PickingMode.Ignore;
                    entry.Add(metaTag);
                }

                // click to select / deselect
                entry.RegisterCallback<ClickEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    var ctrl = evt.ctrlKey || evt.commandKey;
                    if (!ctrl) selectedFiles.Clear();
                    if (selectedFiles.ContainsKey(repoPath))
                        selectedFiles.Remove(repoPath);
                    else
                        selectedFiles[repoPath] = kind;
                    ApplySelectionStyles();

                    if (assetPath != null && !ctrl)
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
                    }
                    evt.StopPropagation();
                });

                // right-click context menu
                entry.AddManipulator(new ContextualMenuManipulator(evt =>
                {
                    // right-clicking an unselected item selects it first
                    if (!selectedFiles.ContainsKey(repoPath)) { selectedFiles.Clear(); selectedFiles[repoPath] = kind; ApplySelectionStyles(); }

                    var hasModified = selectedFiles.Values.Any(k => k != ChangeKind.Added);
                    var hasAdded    = selectedFiles.Values.Any(k => k == ChangeKind.Added);
                    var count       = selectedFiles.Count;
                    var label       = count == 1 ? $"\"{displayName}\"" : $"{count} files";

                    if (hasModified)
                        evt.menu.AppendAction($"Discard changes ({label})", _a =>
                        {
                            var paths = selectedFiles.Keys.ToList();
                            var kinds = selectedFiles.Values.ToList();
                            _ = RunAsync("Discarding changes…", () => ContentGitApi.DiscardFilesAsync(paths, kinds));
                        });

                    if (hasAdded)
                        evt.menu.AppendAction($"Delete ({label})", _a =>
                        {
                            if (!EditorUtility.DisplayDialog("Delete files", $"Permanently delete {label} from disk?", "Delete", "Cancel")) return;
                            var paths = selectedFiles.Where(p => p.Value == ChangeKind.Added).Select(p => p.Key).ToList();
                            _ = RunAsync("Deleting files…", () => ContentGitApi.DeleteLocalFilesAsync(paths));
                        });

                    evt.menu.AppendAction($"Commit and push ({label})…", _a => PromptAndCommit());
                }));

                return entry;
            }


            void UpdateRowState(FolderStatus s)
            {
                latestStatus = s;
                ApplyBadgeState(badge, isCheckedOut, s);
                var dirty = isCheckedOut && !s.IsClean;
                pullBtn.style.display  = isCheckedOut && ContentGitApi.RepositoryBehind > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                pushBtn.style.display  = dirty ? DisplayStyle.Flex : DisplayStyle.None;
                expandBtn.style.display = dirty ? DisplayStyle.Flex : DisplayStyle.None;
                if (!dirty && expanded) { expanded = false; expandBtn.text = "▶"; fileList.style.display = DisplayStyle.None; }
                if (expanded) RebuildFileList();
            }
            rowUpdaters[folder] = UpdateRowState; UpdateRowState(status);

            expandBtn.clicked += () =>
            {
                expanded = !expanded;
                expandBtn.text = expanded ? "▼" : "▶";
                fileList.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                if (expanded) RebuildFileList();
            };

            var renameEditing = false;
            void EnterRenameMode() { renameEditing = true; renameField.SetValueWithoutNotify(folder); nameLabel.style.display = DisplayStyle.None; renameField.style.display = DisplayStyle.Flex; renameField.Focus(); renameField.SelectAll(); }
            void ExitRenameMode(bool commit) { renameEditing = false; nameLabel.style.display = DisplayStyle.Flex; renameField.style.display = DisplayStyle.None; if (!commit) return; var n = renameField.value?.Trim(); if (!string.IsNullOrEmpty(n) && n != folder) _ = RunAsync($"Renaming '{folder}'…", () => ContentGitApi.RenameFolderAsync(folder, n)); }

            renameBtn.clicked += () => { if (renameEditing) ExitRenameMode(true); else EnterRenameMode(); };
            renameField.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { evt.StopPropagation(); ExitRenameMode(true); } else if (evt.keyCode == KeyCode.Escape) { evt.StopPropagation(); ExitRenameMode(false); } });
            checkoutBtn.clicked += () => _ = RunAsync($"Checking out '{folder}'…", () => ContentGitApi.CheckOutFolderAsync(folder));
            disconnectBtn.clicked += () => { if (EditorUtility.DisplayDialog("Disconnect", $"Remove '{folder}' from sparse-checkout?\nUncommitted changes will be lost.", "Disconnect", "Cancel")) _ = RunAsync($"Disconnecting '{folder}'…", () => ContentGitApi.DisconnectFolderAsync(folder)); };
            pullBtn.clicked += () => _ = RunAsync($"Pulling '{folder}'…", () => ContentGitApi.PullFolderAsync(folder));

            void PromptAndCommit()
            {
                var autoMsg   = $"Content Updates {folder}";
                var fileNames = (latestStatus.Files ?? new List<FileChange>())
                    .Select(f => f.Path.Length > folder.Length + 1 ? f.Path.Substring(folder.Length + 1) : f.Path)
                    .ToList();
                CommitConfirmWindow.Show(autoMsg, fileNames,
                    msg => _ = RunAsync($"Committing '{folder}'…", () => ContentGitApi.CommitAndPushFolderAsync(folder, msg)));
            }

            pushBtn.clicked += () => PromptAndCommit();
            deleteBtn.clicked += () => { if (EditorUtility.DisplayDialog("Delete remote", $"Permanently delete '{folder}' from the remote?\nThis cannot be undone.", "Delete", "Cancel")) _ = RunAsync($"Deleting '{folder}'…", () => ContentGitApi.DeleteRemoteFolderAsync(folder)); };

            return row;
        }

        // ── Async orchestration ───────────────────────────────────────────────

        private void OnExternalStateChanged() => EditorApplication.delayCall += () => { if (this == null || busy) return; _ = RunAsync("Refreshing…", () => Task.CompletedTask); };

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
                await RefreshDataAsync(); Rebuild(); RebuildPipelineRows();
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
            try { await op(); SetStatus("Done."); }
            catch (Exception ex) { SetStatus($"Error: {ex.Message}"); AppendLog(log, $"ERROR: {ex.Message}"); Debug.LogException(ex); }
            finally { RebuildPipelineRows(); SetBusy(false); }
        }

        private async Task RefreshDataAsync()
        {
            isInitialized = await ContentGitApi.IsInitializedAsync();
            remoteFolders.Clear(); checkedOutFolders.Clear(); folderStatuses.Clear();
            if (isInitialized != true) return;

            try { remoteFolders = await ContentGitApi.GetRemoteFoldersAsync(); } catch (Exception ex) { Debug.LogWarning($"Remote folders: {ex.Message}"); }
            try { checkedOutFolders = await ContentGitApi.GetCheckedOutFoldersAsync(); } catch (Exception ex) { Debug.LogWarning($"Checked-out folders: {ex.Message}"); }

            var allStatuses = await ContentGitApi.GetAllFolderStatusesAsync();
            foreach (var f in checkedOutFolders)
            {
                if (!remoteFolders.Any(r => r.Equals(f, StringComparison.OrdinalIgnoreCase))) remoteFolders.Add(f);
                allStatuses.TryGetValue(f, out var s); folderStatuses[f] = s;
            }
            remoteFolders.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private void AppendBuildLog(string line) => AppendLog(buildLog, line);
        private void AppendUploadLog(string line) => AppendLog(uploadLog, line);

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
            busy = b; SetSpinnerVisible(b); SetButtonsEnabled(!b);
            if (message != null) SetStatus(message);
        }

        private void SetSpinnerVisible(bool visible)
        {
            if (spinner == null) return;
            if (visible)
            {
                spinner.AddToClassList("cs-spinner--visible");
                if (spinnerTick == null) { var angle = 0f; spinnerTick = spinner.schedule.Execute(() => { angle = (angle + 30f) % 360f; spinner.style.rotate = new StyleRotate(new Rotate(new Angle(angle, AngleUnit.Degree))); }).Every(80); }
            }
            else { spinner.RemoveFromClassList("cs-spinner--visible"); spinnerTick?.Pause(); spinnerTick = null; }
        }

        private void SetButtonsEnabled(bool enabled) => rootVisualElement.Query<Button>().ForEach(b =>
        {
            if (b.name == "btn-new-folder" || b.name == "tab-folders" || b.name == "tab-build" || b.name == "tab-upload") return;
            b.SetEnabled(enabled);
        });

        private async Task PollStatusesAsync()
        {
            if (busy || isInitialized != true || checkedOutFolders.Count == 0 || polling) return;
            polling = true;
            try
            {
                var statuses = await ContentGitApi.GetAllFolderStatusesAsync();
                foreach (var kvp in rowUpdaters) { statuses.TryGetValue(kvp.Key, out var s); kvp.Value(s); if (checkedOutFolders.Any(c => c.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))) folderStatuses[kvp.Key] = s; }
            }
            catch (Exception ex) { Debug.LogWarning($"Status poll: {ex.Message}"); }
            finally { polling = false; }
        }

        private static void ApplyBadgeState(VisualElement group, bool isCheckedOut, FolderStatus status)
        {
            group.Clear();
            if (!isCheckedOut)                             { group.Add(MakeBadge("not checked out", "cs-badge--off")); return; }
            if (status.IsClean)                            { group.Add(MakeBadge("clean",           "cs-badge--on"));  return; }
            if (status.Untracked > 0)                      group.Add(MakeBadge($"+{status.Untracked}",              "cs-badge--new"));
            if (status.Modified + status.Staged > 0)       group.Add(MakeBadge($"{status.Modified + status.Staged}", "cs-badge--modified"));
            if (status.Deleted > 0)                        group.Add(MakeBadge($"-{status.Deleted}",                "cs-badge--deleted"));
        }

        private static Label MakeBadge(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList("cs-badge");
            l.AddToClassList(cls);
            return l;
        }

        private void SetStatus(string msg) { if (statusLabel != null) statusLabel.text = msg; }
        private static Texture2D LoadIcon(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{PackageRoot}/Editor/UI/Icons/{name}.png");
    }
}
