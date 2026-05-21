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
        private Button tabFolders, tabDeploy;
        private VisualElement panelFolders, panelDeploy;

        // Folders tab
        private Button refreshBtn, pullAllBtn, newFolderBtn, initBtn;
        private VisualElement setupBanner, newFolderRow;
        private TextField newFolderField;
        private ScrollView folderList;

        // Deploy tab (build + upload merged)
        private VisualElement genWarningBanner;
        private Label genWarningLabel, deployEmpty, deployLog, stackStatusLabel;
        private Button bumpGenerationBtn, ackUnityVersionBtn, buildAllBtn, deployRefreshBtn;
        private Button uploadAllBtn, promoteAllBtn, buildAndUploadAllBtn, deployLambdaBtn, teardownLambdaBtn;
        private ScrollView deployList;
        private ContentManifest stagingManifest;
        private ContentManifest productionManifest;

        // Status bar
        private VisualElement spinner;
        private Label statusLabel;

        // State
        private bool? isInitialized;
        private bool newFolderRowVisible;
        private List<string> remoteFolders = new();
        private HashSet<string> repoFolders = new(StringComparer.OrdinalIgnoreCase); // folders that exist in the remote git repo
        private List<string> checkedOutFolders = new();
        private Dictionary<string, FolderStatus> folderStatuses = new();
        private readonly Dictionary<string, Action<FolderStatus>> rowUpdaters = new();
        private VisualTreeAsset rowTemplate;
        private bool busy;
        private bool polling;
        private bool _projectChangedPending;
        private IVisualElementScheduledItem spinnerTick, statusPoller;

        [MenuItem("Window/Content Browser")]
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
            WireDeployTab();

            ContentGitApi.OnStateChanged += OnExternalStateChanged;
            EditorApplication.projectChanged += OnProjectChanged;

            // Reset busy state so the initial load is never skipped when the window
            // is re-enabled after exiting Play mode (no domain reload occurs, so
            // the busy flag from any in-flight operation while in Play mode persists).
            busy = false;
            SetSpinnerVisible(false);
            SetStatus("Ready");
            _ = RunAsync("Loading…", () => Task.CompletedTask);
            _ = RefreshManifestsAsync();
            statusPoller = rootVisualElement.schedule.Execute(() => _ = PollStatusesAsync()).Every(60_000);
        }

        private void OnDisable()
        {
            ContentGitApi.OnStateChanged -= OnExternalStateChanged;
            EditorApplication.projectChanged -= OnProjectChanged;
            spinnerTick?.Pause(); spinnerTick = null;
            statusPoller?.Pause(); statusPoller = null;
        }

        private void ResolveElements()
        {
            tabFolders = Q<Button>("tab-folders"); tabDeploy = Q<Button>("tab-deploy");
            panelFolders = Q<VisualElement>("panel-folders"); panelDeploy = Q<VisualElement>("panel-deploy");

            refreshBtn = Q<Button>("btn-refresh"); pullAllBtn = Q<Button>("btn-pull-all");
            newFolderBtn = Q<Button>("btn-new-folder"); initBtn = Q<Button>("btn-init");
            setupBanner = Q<VisualElement>("setup-banner"); newFolderRow = Q<VisualElement>("new-folder-row");
            newFolderField = Q<TextField>("new-folder-field"); folderList = Q<ScrollView>("folder-list-container");

            genWarningBanner = Q<VisualElement>("gen-warning-banner"); genWarningLabel = Q<Label>("gen-warning-label");
            bumpGenerationBtn = Q<Button>("btn-bump-generation"); ackUnityVersionBtn = Q<Button>("btn-ack-unity-version");
            deployRefreshBtn = Q<Button>("btn-deploy-refresh");
            buildAllBtn = Q<Button>("btn-build-all");
            uploadAllBtn = Q<Button>("btn-upload-all");
            promoteAllBtn = Q<Button>("btn-promote-all"); buildAndUploadAllBtn = Q<Button>("btn-more-all");

            Q<Image>("img-deploy-refresh").image = LoadIcon("refresh-cw");
            Q<Image>("img-build-all").image      = LoadIcon("hammer");
            Q<Image>("img-upload-all").image     = LoadIcon("cloud-upload");
            deployList = Q<ScrollView>("deploy-list-container");
            deployEmpty = Q<Label>("deploy-empty"); deployLog = Q<Label>("deploy-log");
            stackStatusLabel = Q<Label>("stack-status-label");
            deployLambdaBtn = Q<Button>("btn-deploy-lambda"); teardownLambdaBtn = Q<Button>("btn-teardown-lambda");
            spinner = Q<VisualElement>("progress-spinner"); statusLabel = Q<Label>("status-label");

            newFolderRow.style.display = DisplayStyle.None;
            setupBanner.style.display = DisplayStyle.None;

            Q<Image>("img-new-folder").image = LoadIcon("plus");
            Q<Image>("img-refresh").image = LoadIcon("refresh-cw");
            Q<Image>("img-pull-all").image = LoadIcon("folder-sync");
        }

        private T Q<T>(string name) where T : VisualElement => rootVisualElement.Q<T>(name);

        // ── Tab bar ───────────────────────────────────────────────────────────

        private void WireTabBar()
        {
            tabFolders.clicked += () => SelectTab("folders");
            tabDeploy.clicked += () => SelectTab("deploy");
            SelectTab("folders");
        }

        private void SelectTab(string name)
        {
            panelFolders.style.display = name == "folders" ? DisplayStyle.Flex : DisplayStyle.None;
            panelDeploy.style.display  = name == "deploy"  ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (var t in new[] { tabFolders, tabDeploy }) t.RemoveFromClassList("cs-tab--active");
            switch (name)
            {
                case "folders": tabFolders.AddToClassList("cs-tab--active"); break;
                case "deploy":
                    tabDeploy.AddToClassList("cs-tab--active");
                    RefreshGenerationWarning();
                    RebuildDeployRows();
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
            var createFolderBtn = Q<Button>("btn-create-folder");
            var newFolderLabel  = Q<Label>("new-folder-label");
            void CollapseInput()
            {
                newFolderRowVisible = false;
                newFolderBtn.style.display   = DisplayStyle.Flex;
                newFolderLabel.style.display = DisplayStyle.Flex;
                newFolderField.style.display = DisplayStyle.None;
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
                newFolderRowVisible = true;
                newFolderBtn.style.display   = DisplayStyle.None;
                newFolderLabel.style.display = DisplayStyle.None;
                newFolderField.style.display = DisplayStyle.Flex;
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

        // ── Deploy tab ────────────────────────────────────────────────────────

        private void WireDeployTab()
        {
            bumpGenerationBtn.clicked  += () => { ContentRepoGenerationSettings.instance.BumpGeneration();          RefreshGenerationWarning(); };
            ackUnityVersionBtn.clicked += () => { ContentRepoGenerationSettings.instance.AcknowledgeUnityVersion(); RefreshGenerationWarning(); };
            deployRefreshBtn.clicked   += () => _ = RefreshManifestsAsync();

            buildAllBtn.clicked += () => _ = RunPipelineAsync("Building all…",
                async () => { await ContentBuildApi.BuildAllCheckedOutAsync(AppendDeployLog); RebuildDeployRows(); },
                deployLog);

            uploadAllBtn.clicked += () => _ = RunPipelineAsync("Uploading all to staging…",
                async () => { await ContentUploadApi.UploadAllCheckedOutAsync(StagingKey(), AppendDeployLog); await RefreshManifestsAsync(); },
                deployLog);

            promoteAllBtn.clicked += () =>
            {
                var staging    = ContentUploadSettings.instance.StagingPrefix;
                var production = ContentUploadSettings.instance.ProductionPrefix;
                if (!EditorUtility.DisplayDialog("Promote all to production",
                    $"Promote all checked-out packages from {staging} → {production}?\nOnly the manifest is updated — no files move.",
                    "Promote", "Cancel")) return;
                _ = RunPipelineAsync("Promoting all → production…",
                    async () => { await ContentUploadApi.PromoteAllAsync(staging, production, AppendDeployLog); await RefreshManifestsAsync(); },
                    deployLog);
            };

            buildAndUploadAllBtn.clicked += () =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Build and Deploy All"), false, () =>
                    _ = RunPipelineAsync("Build and Deploy All…",
                        async () =>
                        {
                            await ContentBuildApi.BuildAllCheckedOutAsync(AppendDeployLog);
                            await ContentUploadApi.UploadAllCheckedOutAsync(StagingKey(), AppendDeployLog);
                            await RefreshManifestsAsync();
                        }, deployLog));
                menu.DropDown(buildAndUploadAllBtn.worldBound);
            };

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

        private async Task RefreshStackStatusAsync()
        {
            try { stackStatusLabel.text = $"Stack: {await ContentInfraApi.GetStackStatusAsync()}"; }
            catch { stackStatusLabel.text = "Stack: unknown"; }
        }

        private async Task RefreshManifestsAsync()
        {
            var settings = ContentUploadSettings.instance;
            try
            {
                stagingManifest    = await ContentUploadApi.GetManifestAsync(settings.StagingPrefix);
                productionManifest = await ContentUploadApi.GetManifestAsync(settings.ProductionPrefix);
            }
            catch { /* credentials not configured yet */ }
            RebuildDeployRows();
            if (!busy) Rebuild();
            UpdateToolbarVisibility();
        }

        private void UpdateToolbarVisibility()
        {
            if (uploadAllBtn == null || promoteAllBtn == null) return;
            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();

            var anyUploadNeeded = checkedOutFolders.Any(pkg =>
            {
                var stgId        = stagingManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
                var localBuildId = ContentBuildApi.GetLastBuildResult(pkg)?.BuildId
                                   ?? ContentBuildApi.GetLatestBuildIdFromDisk(pkg, platform);
                return localBuildId != null && localBuildId != stgId;
            });
            uploadAllBtn.style.display = anyUploadNeeded ? DisplayStyle.Flex : DisplayStyle.None;

            var anyPromoteReady = checkedOutFolders.Any(pkg =>
            {
                var stgId = stagingManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
                var prdId = productionManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
                return stgId != null && stgId != prdId;
            });
            promoteAllBtn.style.display = anyPromoteReady ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private string StagingKey() => ContentUploadSettings.instance.StagingPrefix;

        // ── Deploy rows ───────────────────────────────────────────────────────

        private void RebuildDeployRows()
        {
            deployList.Clear();
            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();

            var local = checkedOutFolders.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            deployEmpty.style.display = local.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var pkg in local)
                deployList.Add(BuildDeployRow(pkg, platform));

            var remoteOnly = GetRemoteOnlyPackages().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            if (remoteOnly.Count > 0)
            {
                var divider = new Label("Not checked out locally") { style = { opacity = 0.45f, fontSize = 10, marginTop = 6, marginLeft = 8, marginBottom = 2 } };
                deployList.Add(divider);
                foreach (var pkg in remoteOnly)
                    deployList.Add(BuildRemoteOnlyRow(pkg, platform));
            }
        }

        private IEnumerable<string> GetRemoteOnlyPackages()
        {
            var local  = new HashSet<string>(checkedOutFolders, StringComparer.OrdinalIgnoreCase);
            var remote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (stagingManifest    != null) foreach (var e in stagingManifest.contentPackages)    remote.Add(e.name);
            if (productionManifest != null) foreach (var e in productionManifest.contentPackages) remote.Add(e.name);
            return remote.Where(p => !local.Contains(p));
        }

        private VisualElement BuildRemoteOnlyRow(string pkg, string platform)
        {
            var row = new VisualElement(); row.AddToClassList("cs-pipeline-row"); row.AddToClassList("cs-pipeline-row--remote");

            var nameLabel = new Label(pkg); nameLabel.AddToClassList("cs-pipeline-name"); row.Add(nameLabel);

            var stgId = stagingManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
            var prdId = productionManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;

            // Warning: in production but removed from staging — next promote would leave prod out of sync
            if (stgId == null && prdId != null)
            {
                var warn = new Label("⚠") { tooltip = "This package is in production but not in staging.\nUse 'Restore from production' to re-add it to staging." };
                warn.AddToClassList("cs-warn-icon");
                row.Add(warn);
            }

            var stagingBadge = MakeLiveBadge("staging", stgId);
            if (stagingBadge != null) row.Add(stagingBadge);
            var prodBadge = MakeProdBadge(prdId, false);
            if (prodBadge != null) row.Add(prodBadge);

            row.Add(new VisualElement { style = { flexGrow = 1 } });

            var staging    = ContentUploadSettings.instance.StagingPrefix;
            var production = ContentUploadSettings.instance.ProductionPrefix;
            var moreBtn = new Button { text = "⋮", tooltip = "More actions" };
            moreBtn.AddToClassList("cs-icon-btn");
            moreBtn.AddToClassList("cs-icon-btn--lg");
            moreBtn.AddToClassList("cs-menu-btn");
            moreBtn.clicked += () =>
            {
                var menu = new GenericMenu();
                if (prdId != null && prdId != stgId)
                    menu.AddItem(new GUIContent("Restore from production"), false, () =>
                        _ = RunPipelineAsync($"Restoring '{pkg}' from production…",
                            async () => { await ContentUploadApi.PromoteContentPackageAsync(pkg, production, staging, AppendDeployLog); await RefreshManifestsAsync(); },
                            deployLog));
                if (stgId != null)
                    menu.AddItem(new GUIContent("Remove from Staging"), false, () =>
                        _ = RunPipelineAsync($"Removing '{pkg}' from staging…",
                            async () => { await ContentUploadApi.RemoveFromManifestAsync(pkg, staging, AppendDeployLog); await RefreshManifestsAsync(); },
                            deployLog));
                if (prdId == null && stgId == null)
                    menu.AddDisabledItem(new GUIContent("Nothing to restore"));
                menu.DropDown(moreBtn.worldBound);
            };
            row.Add(moreBtn);

            return row;
        }

        private VisualElement BuildDeployRow(string pkg, string platform)
        {
            var row = new VisualElement(); row.AddToClassList("cs-pipeline-row");

            // Name
            var nameLabel = new Label(pkg); nameLabel.AddToClassList("cs-pipeline-name"); row.Add(nameLabel);

            // Build meta label — prefer session timestamp, fall back to disk artifact info.
            var buildTs   = ContentBuildApi.GetLastBuildTimestamp(pkg, platform);
            var lastBuild = ContentBuildApi.GetLastBuildResult(pkg);
            var localBuildId = lastBuild?.BuildId ?? ContentBuildApi.GetLatestBuildIdFromDisk(pkg, platform);
            var buildMetaText = buildTs.HasValue
                ? $"built {buildTs.Value.ToLocalTime():MM-dd HH:mm}" + (lastBuild != null ? $" · {lastBuild.BuildId[..8]}" : "")
                : localBuildId != null
                    ? $"local: {localBuildId[..Math.Min(8, localBuildId.Length)]}"
                    : null;
            if (buildMetaText != null)
            {
                var buildMeta = new Label(buildMetaText); buildMeta.AddToClassList("cs-build-meta");
                row.Add(buildMeta);
            }

            // Staging / production live badges
            var stgId = stagingManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
            var prdId = productionManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
            var promoteReady = stgId != null && stgId != prdId;
            if (stagingManifest != null || productionManifest != null)
            {
                var stagBadge = MakeLiveBadge("staging", stgId);
                if (stagBadge != null) row.Add(stagBadge);
                var prodBadge2 = MakeProdBadge(prdId, promoteReady);
                if (prodBadge2 != null) row.Add(prodBadge2);
            }

            // Local dev badge — visible at a glance when an override is active for this package.
            var devOverrideActive = ContentLocalDevOverrides.TryGet(pkg, out var devBadgeEntry);
            if (devOverrideActive)
            {
                var devBadgeText = devBadgeEntry.Mode == LocalDevMode.AssetDatabase ? "local: AssetDB" : "local: bundles";
                var devBadge = MakeBadge(devBadgeText, "cs-badge--local-dev",
                    devBadgeEntry.Mode == LocalDevMode.AssetDatabase
                        ? "Assets are served from AssetDatabase (Fast Mode). CDN bypassed for this package."
                        : "Bundles are served from local disk. CDN bypassed for this package.");
                row.Add(devBadge);
            }

            // Status + spacer — only added when a pipeline operation is running; starts hidden.
            var status = new Label(); status.AddToClassList("cs-pipeline-status"); status.AddToClassList("cs-pipeline-status--idle");
            status.style.display = DisplayStyle.None;
            row.Add(status);
            row.Add(new VisualElement { style = { flexGrow = 1 } });

            // Build
            AddIconBtn(row, "hammer", $"Build '{pkg}'", () => _ = RunPipelineAsync($"Building '{pkg}'…",
                async () =>
                {
                    SetPipelineStatus(status, "running");
                    try { await ContentBuildApi.BuildContentPackageAsync(pkg, AppendDeployLog); SetPipelineStatus(status, "ok"); RebuildDeployRows(); }
                    catch { SetPipelineStatus(status, "err"); throw; }
                }, deployLog));

            // Upload — shown whenever there is a local build artifact that differs from staging.
            // Intentionally does NOT require a session timestamp so it works after Unity restarts
            // or when artifacts were produced by CI and copied locally.
            var showUpload = localBuildId != null && localBuildId != stgId;
            var uploadBtn = AddIconBtn(row, "cloud-upload", $"Upload '{pkg}' to staging", () => _ = RunPipelineAsync($"Uploading '{pkg}'…",
                async () =>
                {
                    SetPipelineStatus(status, "running");
                    try { await ContentUploadApi.UploadContentPackageAsync(pkg, StagingKey(), log: AppendDeployLog); SetPipelineStatus(status, "ok"); }
                    catch { SetPipelineStatus(status, "err"); throw; }
                    finally { await RefreshManifestsAsync(); }
                }, deployLog));
            uploadBtn.style.display = showUpload ? DisplayStyle.Flex : DisplayStyle.None;

            var staging    = ContentUploadSettings.instance.StagingPrefix;
            var production = ContentUploadSettings.instance.ProductionPrefix;

            // Promote → production text button (only when staging is ahead of production)
            var promoteBtn = new Button(() =>
            {
                if (!EditorUtility.DisplayDialog("Promote", $"Promote '{pkg}' from {staging} → {production}?", "Promote", "Cancel")) return;
                _ = RunPipelineAsync($"Promoting '{pkg}'…",
                    async () => { await ContentUploadApi.PromoteContentPackageAsync(pkg, staging, production, AppendDeployLog); await RefreshManifestsAsync(); },
                    deployLog);
            }) { text = "→ production" };
            promoteBtn.AddToClassList("cs-text-btn");
            promoteBtn.AddToClassList("cs-promote-btn");
            promoteBtn.style.display = promoteReady ? DisplayStyle.Flex : DisplayStyle.None;
            row.Add(promoteBtn);

            // ⋮ menu (last) — overflow actions
            var moreBtn = new Button { text = "⋮", tooltip = "More actions" };
            moreBtn.AddToClassList("cs-icon-btn");
            moreBtn.AddToClassList("cs-icon-btn--lg");
            moreBtn.AddToClassList("cs-menu-btn");
            moreBtn.clicked += () =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Build and Deploy"), false, () =>
                    _ = RunPipelineAsync($"Build and Deploy '{pkg}'…",
                        async () =>
                        {
                            SetPipelineStatus(status, "running");
                            try
                            {
                                await ContentBuildApi.BuildContentPackageAsync(pkg, AppendDeployLog);
                                await ContentUploadApi.UploadContentPackageAsync(pkg, StagingKey(), log: AppendDeployLog);
                                SetPipelineStatus(status, "ok");
                            }
                            catch { SetPipelineStatus(status, "err"); throw; }
                            finally { await RefreshManifestsAsync(); }
                        }, deployLog));
                if (prdId != null && prdId != stgId)
                    menu.AddItem(new GUIContent("Restore from production"), false, () =>
                        _ = RunPipelineAsync($"Restoring '{pkg}' from production…",
                            async () => { await ContentUploadApi.PromoteContentPackageAsync(pkg, production, staging, AppendDeployLog); await RefreshManifestsAsync(); },
                            deployLog));
                if (stgId != null)
                    menu.AddItem(new GUIContent("Remove from Staging"), false, () =>
                        _ = RunPipelineAsync($"Removing '{pkg}' from staging…",
                            async () => { await ContentUploadApi.RemoveFromManifestAsync(pkg, staging, AppendDeployLog); await RefreshManifestsAsync(); },
                            deployLog));

                // ── Local dev overrides ──────────────────────────────────────
                menu.AddSeparator("");
                var isAssetDb      = ContentLocalDevOverrides.TryGet(pkg, out var devEntry)
                                     && devEntry.Mode == LocalDevMode.AssetDatabase;
                var isLocalBundles = ContentLocalDevOverrides.TryGet(pkg, out devEntry)
                                     && devEntry.Mode == LocalDevMode.LocalBundles;

                // AssetDatabase (Fast Mode)
                if (!isAssetDb)
                    menu.AddItem(new GUIContent("Local Dev / Use Asset Database (Fast Mode)"), false, () =>
                    {
                        try   { ContentLocalDevApi.SetupForFastMode(pkg, AppendDeployLog); RebuildDeployRows(); }
                        catch (Exception ex) { AppendDeployLog($"ERROR: {ex.Message}"); Debug.LogException(ex); }
                    });
                else
                    menu.AddItem(new GUIContent("\u2713 Local Dev: Asset Database active — Clear"), false, () =>
                    {
                        ContentLocalDevApi.ClearFastMode(pkg, AppendDeployLog);
                        RebuildDeployRows();
                    });

                // Local Bundles (Build + Use Existing Build)
                if (!isLocalBundles)
                    menu.AddItem(new GUIContent("Local Dev / Build and Use Local Bundles"), false, () =>
                        _ = RunPipelineAsync($"Build & register local bundles for '{pkg}'\u2026",
                            async () =>
                            {
                                SetPipelineStatus(status, "running");
                                try   { await ContentLocalDevApi.BuildAndRegisterLocalBundlesAsync(pkg, AppendDeployLog); SetPipelineStatus(status, "ok"); }
                                catch { SetPipelineStatus(status, "err"); throw; }
                                finally { RebuildDeployRows(); }
                            }, deployLog));
                else
                    menu.AddItem(new GUIContent("\u2713 Local Dev: Local Bundles active — Clear"), false, () =>
                    {
                        ContentLocalDevApi.ClearLocalBundles(pkg, AppendDeployLog);
                        RebuildDeployRows();
                    });

                menu.DropDown(moreBtn.worldBound);
            };
            row.Add(moreBtn);

            return row;
        }

        private static void AddBtn(VisualElement row, string text, string name, Action clicked)
        {
            var btn = new Button(clicked) { text = text, name = name };
            btn.AddToClassList("cs-text-btn");
            row.Add(btn);
        }

        private static Button AddIconBtn(VisualElement parent, string iconName, string tooltip, Action clicked)
        {
            var btn = new Button(clicked) { tooltip = tooltip };
            btn.AddToClassList("cs-icon-btn");
            var img = new Image { image = LoadIcon(iconName), pickingMode = PickingMode.Ignore };
            img.AddToClassList("cs-icon-image");
            btn.Add(img);
            parent.Add(btn);
            return btn;
        }

        private static void SetPipelineStatus(Label status, string state)
        {
            status.RemoveFromClassList("cs-pipeline-status--idle");
            status.RemoveFromClassList("cs-pipeline-status--ok");
            status.RemoveFromClassList("cs-pipeline-status--err");
            status.RemoveFromClassList("cs-pipeline-status--running");
            switch (state)
            {
                case "ok":
                    status.text = "ok";
                    status.AddToClassList("cs-pipeline-status--ok");
                    status.style.display = DisplayStyle.Flex;
                    break;
                case "err":
                    status.text = "error";
                    status.AddToClassList("cs-pipeline-status--err");
                    status.style.display = DisplayStyle.Flex;
                    break;
                case "running":
                    status.text = "running";
                    status.AddToClassList("cs-pipeline-status--running");
                    status.style.display = DisplayStyle.Flex;
                    break;
                default:
                    // "idle" is the reset state — hide the label so it takes no space.
                    status.text = string.Empty;
                    status.AddToClassList("cs-pipeline-status--idle");
                    status.style.display = DisplayStyle.None;
                    break;
            }
        }

        // ── Folder rows (Folders tab) ─────────────────────────────────────────

        private void Rebuild()
        {
            setupBanner.style.display = isInitialized == false ? DisplayStyle.Flex : DisplayStyle.None;
            rowUpdaters.Clear(); folderList.Clear();
            if (isInitialized != true) return;
            if (remoteFolders.Count == 0)
                folderList.Add(new Label("No folders available.") { style = { unityFontStyleAndWeight = FontStyle.Italic, marginTop = 8, marginLeft = 8 } });
            else
                foreach (var folder in remoteFolders) folderList.Add(BuildRow(folder));
            newFolderRow.style.display = DisplayStyle.Flex;
            folderList.Add(newFolderRow);
        }

        private VisualElement BuildRow(string folder)
        {
            var isCheckedOut = checkedOutFolders.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            var isOnRemote   = repoFolders.Contains(folder);
            folderStatuses.TryGetValue(folder, out var status);
            var row = rowTemplate.CloneTree();

            row.Q<Image>("img-pull").image       = LoadIcon("arrow-down-to-line");
            row.Q<Image>("img-push").image       = LoadIcon("arrow-up-from-line");
            row.Q<Image>("img-checkout").image   = LoadIcon("folder-down");
            row.Q<Image>("img-disconnect").image = LoadIcon("x");

            var nameLabel   = row.Q<Label>("folder-name-label");
            var renameField = row.Q<TextField>("folder-rename-field");
            var badge         = row.Q<VisualElement>("folder-badge-group");
            var pullBtn       = row.Q<Button>("btn-pull");
            var pushBtn       = row.Q<Button>("btn-push");
            var checkoutBtn   = row.Q<Button>("btn-checkout");
            var disconnectBtn = row.Q<Button>("btn-disconnect");
            var moreBtn       = row.Q<Button>("btn-more");
            var expandBtn     = row.Q<Button>("btn-expand");
            var fileList      = row.Q<VisualElement>("file-list");

            nameLabel.text = folder;
            renameField.style.display = DisplayStyle.None;
            checkoutBtn.style.display = isCheckedOut ? DisplayStyle.None : DisplayStyle.Flex;

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
                    var pfx = folder + "/";
                    var rel  = fc.Path.StartsWith(pfx, StringComparison.OrdinalIgnoreCase) ? fc.Path.Substring(pfx.Length) : fc.Path;
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
                ApplyBadgeState(badge, isCheckedOut, s, folder);
                var dirty = isCheckedOut && !s.IsClean;
                pullBtn.style.display       = isCheckedOut && ContentGitApi.RepositoryBehind > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                pushBtn.style.display       = dirty ? DisplayStyle.Flex : DisplayStyle.None;
                expandBtn.style.display     = dirty ? DisplayStyle.Flex : DisplayStyle.None;
                disconnectBtn.style.display = isCheckedOut && !dirty && isOnRemote ? DisplayStyle.Flex : DisplayStyle.None;
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

            renameField.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { evt.StopPropagation(); ExitRenameMode(true); } else if (evt.keyCode == KeyCode.Escape) { evt.StopPropagation(); ExitRenameMode(false); } });
            checkoutBtn.clicked += () => _ = RunAsync($"Checking out '{folder}'…", () => ContentGitApi.CheckOutFolderAsync(folder));
            disconnectBtn.clicked += () => { if (EditorUtility.DisplayDialog("Disconnect", $"Remove '{folder}' from sparse-checkout?\nUncommitted changes will be lost.", "Disconnect", "Cancel")) _ = RunAsync($"Disconnecting '{folder}'…", () => ContentGitApi.DisconnectFolderAsync(folder)); };
            pullBtn.clicked += () => _ = RunAsync($"Pulling '{folder}'…", () => ContentGitApi.PullFolderAsync(folder));

            void PromptAndCommit()
            {
                var autoMsg   = $"Content Updates {folder}";
                var pfx       = folder + "/";
                var fileNames = (latestStatus.Files ?? new List<FileChange>())
                    .Select(f => f.Path.StartsWith(pfx, StringComparison.OrdinalIgnoreCase) ? f.Path.Substring(pfx.Length) : f.Path)
                    .ToList();
                CommitConfirmWindow.Show(autoMsg, fileNames,
                    msg => _ = RunAsync($"Committing '{folder}'…", () => ContentGitApi.CommitAndPushFolderAsync(folder, msg)));
            }

            pushBtn.clicked += () => PromptAndCommit();
            moreBtn.clicked += () =>
            {
                var dirty = isCheckedOut && !latestStatus.IsClean;
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Rename"), false, () => EnterRenameMode());
                if (dirty && isOnRemote)
                {
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Disconnect"), false, () =>
                    {
                        if (EditorUtility.DisplayDialog("Disconnect", $"Remove '{folder}' from sparse-checkout?\nUncommitted changes will be lost.", "Disconnect", "Cancel"))
                            _ = RunAsync($"Disconnecting '{folder}'…", () => ContentGitApi.DisconnectFolderAsync(folder));
                    });
                }
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete from repository"), false, () =>
                {
                    if (EditorUtility.DisplayDialog("Delete remote", $"Permanently delete '{folder}' from the remote?\nThis cannot be undone.", "Delete", "Cancel"))
                        _ = RunAsync($"Deleting '{folder}'…", () => ContentGitApi.DeleteRemoteFolderAsync(folder));
                });
                menu.DropDown(moreBtn.worldBound);
            };

            return row;
        }

        // ── Async orchestration ───────────────────────────────────────────────

        private void OnExternalStateChanged() => EditorApplication.delayCall += () => { if (this == null || busy) return; _ = RunAsync("Refreshing…", () => Task.CompletedTask); };

        // Called by EditorApplication.projectChanged after every AssetDatabase refresh.
        // We debounce with a single delayCall so rapid reimports only trigger one refresh.
        private void OnProjectChanged()
        {
            if (_projectChangedPending) return;
            _projectChangedPending = true;
            EditorApplication.delayCall += () =>
            {
                _projectChangedPending = false;
                if (this == null || busy) return;
                _ = RunAsync("Refreshing…", () => Task.CompletedTask);
            };
        }

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
                await RefreshDataAsync(); Rebuild(); RebuildDeployRows(); UpdateToolbarVisibility();
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
            finally { RebuildDeployRows(); SetBusy(false); }
        }

        private async Task RefreshDataAsync()
        {
            isInitialized = await ContentGitApi.IsInitializedAsync();
            remoteFolders.Clear(); repoFolders.Clear(); checkedOutFolders.Clear(); folderStatuses.Clear();
            if (isInitialized != true) return;

            try { remoteFolders = await ContentGitApi.GetRemoteFoldersAsync(); } catch (Exception ex) { Debug.LogWarning($"Remote folders: {ex.Message}"); }
            repoFolders = new HashSet<string>(remoteFolders, StringComparer.OrdinalIgnoreCase);
            try { checkedOutFolders = await ContentGitApi.GetCheckedOutFoldersAsync(); } catch (Exception ex) { Debug.LogWarning($"Checked-out folders: {ex.Message}"); }

            // Also include any folder the developer created directly on disk (via Unity Project
            // window or file explorer) that isn't on the remote or in sparse-checkout yet.
            List<string> diskFolders = new List<string>();
            try { diskFolders = await ContentGitApi.GetLocalFoldersOnDiskAsync(); } catch (Exception ex) { Debug.LogWarning($"Disk folders: {ex.Message}"); }

            var allStatuses = await ContentGitApi.GetAllFolderStatusesAsync();
            MergeGroupsStatusInto(checkedOutFolders, allStatuses);
            foreach (var f in checkedOutFolders)
            {
                // If a folder is in the sparse-checkout list but no longer exists on disk
                // and is not on the remote, it's a stale entry — prune it and hide it from UI.
                var isOnRemote = remoteFolders.Any(r => r.Equals(f, StringComparison.OrdinalIgnoreCase));
                var isOnDisk   = diskFolders.Any(d => d.Equals(f, StringComparison.OrdinalIgnoreCase));
                if (!isOnRemote && !isOnDisk)
                {
                    try { await ContentGitApi.RemoveFolderFromSparseCheckoutAsync(f); }
                    catch (Exception ex) { Debug.LogWarning($"[ContentRepo] Could not prune stale sparse-checkout entry '{f}': {ex.Message}"); }
                    Debug.Log($"[ContentRepo] Pruned stale sparse-checkout entry '{f}' — not on remote or disk.");
                    continue;
                }

                if (!isOnRemote) remoteFolders.Add(f);
                allStatuses.TryGetValue(f, out var s); folderStatuses[f] = s;
            }
            foreach (var f in diskFolders)
            {
                if (!remoteFolders.Any(r => r.Equals(f, StringComparison.OrdinalIgnoreCase)))
                {
                    remoteFolders.Add(f);
                    // Also register in sparse-checkout so the folder is tracked in git.
                    try { await ContentGitApi.EnsureFolderInSparseCheckoutAsync(f); } catch { /* best-effort */ }
                }
                if (!checkedOutFolders.Any(c => c.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    checkedOutFolders.Add(f);
                allStatuses.TryGetValue(f, out var s); folderStatuses[f] = s;
            }
            remoteFolders.Sort(StringComparer.OrdinalIgnoreCase);

            // Clean up ContentLocalDevOverrides for packages whose local folder no longer exists
            // on disk. This happens when a developer deletes the folder from the Project window
            // or file explorer — the override should not persist after the assets are gone.
            foreach (var pkg in ContentLocalDevOverrides.All.Keys.ToList())
            {
                if (!diskFolders.Any(f => f.Equals(pkg, StringComparison.OrdinalIgnoreCase)))
                {
                    ContentLocalDevApi.ClearOverride(pkg);
                    Debug.Log($"[ContentRepo] Cleared local dev override for '{pkg}' — folder no longer on disk.");
                }
            }

            // Clean up orphan Addressables groups in _groups/ whose content package no longer
            // exists locally (on disk) or on the remote. Auto-generated groups are created by
            // EnsureGroupPopulated / ContentGroupAutoSetup and should be removed together with
            // the package folder so they don't clutter the Addressables Groups window.
            CleanupOrphanAddressableGroups(remoteFolders, diskFolders);
        }

        /// <summary>
        /// Deletes Addressables group assets from <c>_groups/</c> whose package name is not
        /// present in either <paramref name="remoteFolders"/> (known on the remote) or
        /// <paramref name="diskFolders"/> (present locally on disk).  These orphan groups are
        /// auto-generated by <see cref="ContentLocalDevApi.EnsureGroupPopulated"/> /
        /// <see cref="ContentGroupAutoSetup"/> and should be removed with the package.
        /// </summary>
        private static void CleanupOrphanAddressableGroups(
            IReadOnlyList<string> remoteFolders,
            IReadOnlyList<string> diskFolders)
        {
            var addressableSettings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
            if (addressableSettings == null) return;

            // Build a fast lookup of all known package names.
            var known = new System.Collections.Generic.HashSet<string>(
                remoteFolders.Concat(diskFolders),
                StringComparer.OrdinalIgnoreCase);

            // Collect groups to remove — never remove groups whose name is in the known set
            // and never touch the default group or internal Addressables groups.
            var toRemove = addressableSettings.groups
                .Where(g => g != null
                         && !g.IsDefaultGroup()
                         && !known.Contains(g.name))
                .ToList();

            foreach (var group in toRemove)
            {
                // Only remove if the group asset lives inside _groups/ — this avoids
                // accidentally deleting manually-created groups that have nothing to do
                // with ContentRepo.
                var assetPath = UnityEditor.AssetDatabase.GetAssetPath(group);
                if (string.IsNullOrEmpty(assetPath)) continue;

                var normalised = assetPath.Replace('\\', '/');
                var groupsFolderSegment = $"/{ContentGitApi.GroupsFolderName}/";
                if (!normalised.Contains(groupsFolderSegment)) continue;

                Debug.Log($"[ContentRepo] Removing orphan Addressables group '{group.name}' — package no longer exists.");
                addressableSettings.RemoveGroup(group);
            }

            if (toRemove.Any(g => g != null))
                UnityEditor.AssetDatabase.SaveAssets();
        }

        // Distributes _groups/<GroupFile> status entries to the content package they belong to,
        // so group file changes appear under their package row rather than as an orphan _groups entry.
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
            // Group files are named <PackageName>.asset — strip extensions to recover the name.
            // Only files directly inside _groups/ are considered; ignore anything deeper.
            var parts = repoRelativePath.Replace('\\', '/').Split('/');
            if (parts.Length != 2 || !parts[0].Equals(ContentGitApi.GroupsFolderName, StringComparison.OrdinalIgnoreCase))
                return null;

            var file = parts[1];
            if (file.EndsWith(".meta",  StringComparison.OrdinalIgnoreCase)) file = file.Substring(0, file.Length - 5);
            if (file.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) file = file.Substring(0, file.Length - 6);
            return string.IsNullOrEmpty(file) ? null : file;
        }

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
                MergeGroupsStatusInto(checkedOutFolders, statuses);
                foreach (var kvp in rowUpdaters) { statuses.TryGetValue(kvp.Key, out var s); kvp.Value(s); if (checkedOutFolders.Any(c => c.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))) folderStatuses[kvp.Key] = s; }
            }
            catch (Exception ex) { Debug.LogWarning($"Status poll: {ex.Message}"); }
            finally { polling = false; }
        }

        private void ApplyBadgeState(VisualElement group, bool isCheckedOut, FolderStatus status, string pkg)
        {
            group.Clear();

            // Git state
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

            // Deploy state — shown for all folders (checked out or not) when manifests are loaded
            if (stagingManifest == null && productionManifest == null) return;
            var platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            var stgId = stagingManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
            var prdId = productionManifest?.Find(pkg)?.FindPlatform(platform)?.buildId;
            if (stgId == null && prdId == null) return;

            var promoteReady = stgId != null && stgId != prdId;
            if (stgId != null)
                group.Add(MakeLiveBadge("staging", stgId));
            var applyProdBadge = MakeProdBadge(prdId, promoteReady);
            if (applyProdBadge != null) group.Add(applyProdBadge);
        }

        private static Label MakeBadge(string text, string cls, string tooltip = null)
        {
            var l = new Label(text) { tooltip = tooltip };
            l.AddToClassList("cs-badge");
            l.AddToClassList(cls);
            return l;
        }

        // Returns null when buildId is null — callers must null-check before adding.
        private static Label MakeLiveBadge(string env, string buildId)
        {
            if (buildId == null) return null;
            var text    = $"{env}: {buildId[..Math.Min(8, buildId.Length)]}";
            var tooltip = $"Live on {env}\n{buildId}";
            var l = new Label(text) { tooltip = tooltip };
            l.AddToClassList("cs-badge");
            l.AddToClassList("cs-badge--stg");
            return l;
        }

        // Returns null when there is nothing meaningful to show (no prdId, not promote-ready).
        private static Label MakeProdBadge(string prdId, bool promoteReady)
        {
            if (!promoteReady && prdId == null) return null;
            string text; string cls; string tooltip;
            if (promoteReady)
            {
                text    = "→ production ready";
                cls     = "cs-badge--promote";
                tooltip = $"Staging has a newer build — ready to promote\nProduction: {prdId ?? "none"}";
            }
            else
            {
                text    = $"production: {prdId[..Math.Min(8, prdId.Length)]}";
                cls     = "cs-badge--prod";
                tooltip = $"Live on production\n{prdId}";
            }
            var l = new Label(text) { tooltip = tooltip };
            l.AddToClassList("cs-badge");
            l.AddToClassList(cls);
            return l;
        }

        private void SetStatus(string msg) { if (statusLabel != null) statusLabel.text = msg; }
        private static Texture2D LoadIcon(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{PackageRoot}/Editor/UI/Icons/{name}.png");
    }
}
