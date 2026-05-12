using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
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

        private Button refreshBtn;
        private Button pullAllBtn;
        private Button newFolderBtn;
        private Button initBtn;
        private VisualElement setupBanner;
        private VisualElement newFolderRow;
        private TextField newFolderField;
        private ScrollView folderList;
        private VisualElement spinner;
        private Label statusLabel;

        private bool? isInitialized;   // null = loading/unknown, true/false = confirmed
        private bool newFolderRowVisible;
        private List<string> remoteFolders = new();
        private List<string> checkedOutFolders = new();
        private Dictionary<string, FolderStatus> folderStatuses = new();
        private readonly Dictionary<string, Action<FolderStatus>> rowUpdaters = new();
        private VisualTreeAsset rowTemplate;
        private bool busy;
        private bool polling;
        private IVisualElementScheduledItem spinnerTick;
        private IVisualElementScheduledItem statusPoller;

        [MenuItem("Tools/Content Browser")]
        public static void ShowWindow()
        {
            var w = GetWindow<ContentRepoWindow>();
            w.titleContent = new GUIContent("Content Browser");
            w.minSize = new Vector2(520, 320);
        }

        private void OnEnable()
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                rootVisualElement.Add(new Label($"Could not load {UxmlPath}"));
                return;
            }
            uxml.CloneTree(rootVisualElement);

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null)
                rootVisualElement.styleSheets.Add(uss);

            rowTemplate = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(FolderRowUxmlPath);

            refreshBtn = rootVisualElement.Q<Button>("btn-refresh");
            pullAllBtn = rootVisualElement.Q<Button>("btn-pull-all");
            newFolderBtn = rootVisualElement.Q<Button>("btn-new-folder");
            initBtn = rootVisualElement.Q<Button>("btn-init");
            setupBanner = rootVisualElement.Q<VisualElement>("setup-banner");
            newFolderRow = rootVisualElement.Q<VisualElement>("new-folder-row");
            newFolderField = rootVisualElement.Q<TextField>("new-folder-field");
            folderList = rootVisualElement.Q<ScrollView>("folder-list-container");
            spinner = rootVisualElement.Q<VisualElement>("progress-spinner");
            statusLabel = rootVisualElement.Q<Label>("status-label");

            newFolderRow.style.display = DisplayStyle.None;
            setupBanner.style.display = DisplayStyle.None;  // hide until confirmed uninitialized

            rootVisualElement.Q<Image>("img-new-folder").image = LoadIcon("folder-plus");
            rootVisualElement.Q<Image>("img-refresh").image    = LoadIcon("refresh-cw");
            rootVisualElement.Q<Image>("img-pull-all").image   = LoadIcon("folder-sync");

            refreshBtn.clicked += () => _ = RunAsync("Refreshing…", () => Task.CompletedTask);
            pullAllBtn.clicked += () => _ = RunAsync("Pulling all checked-out folders…", ContentGitApi.PullAllAsync);
            initBtn.clicked += () => _ = RunAsync("Initializing…", ContentGitApi.InitAsync);

            newFolderBtn.clicked += () =>
            {
                newFolderRowVisible = !newFolderRowVisible;
                newFolderRow.style.display = newFolderRowVisible ? DisplayStyle.Flex : DisplayStyle.None;
                if (newFolderRowVisible) newFolderField.Focus();
            };

            rootVisualElement.Q<Button>("btn-create-folder").clicked += () =>
            {
                var folderName = newFolderField.value?.Trim();
                if (string.IsNullOrEmpty(folderName))
                {
                    EditorUtility.DisplayDialog("Folder name required", "Please enter a folder name.", "OK");
                    return;
                }
                newFolderRowVisible = false;
                newFolderRow.style.display = DisplayStyle.None;
                newFolderField.SetValueWithoutNotify("");
                _ = RunAsync($"Creating '{folderName}'…", () => ContentGitApi.CreateFolderAsync(folderName));
            };

            ContentGitApi.OnStateChanged += OnExternalStateChanged;

            SetSpinnerVisible(false);
            SetStatus("Ready");
            _ = RunAsync("Loading…", () => Task.CompletedTask);

            statusPoller = rootVisualElement.schedule.Execute(() => _ = PollStatusesAsync()).Every(5000);
        }

        private void OnDisable()
        {
            ContentGitApi.OnStateChanged -= OnExternalStateChanged;
            spinnerTick?.Pause();
            spinnerTick = null;
            statusPoller?.Pause();
            statusPoller = null;
        }

        private void OnExternalStateChanged()
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                if (busy) return;
                _ = RunAsync("Refreshing…", () => Task.CompletedTask);
            };
        }

        private async Task RunAsync(string statusMessage, Func<Task> op)
        {
            if (busy) return;
            ContentGitApi.ClearLastWarning();
            SetBusy(true, statusMessage);
            try
            {
                await op();
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
                Debug.LogException(ex);
                SetBusy(false);
                return;
            }

            try
            {
                SetStatus("Refreshing…");
                await RefreshDataAsync();
                Rebuild();
                SetStatus(!string.IsNullOrEmpty(ContentGitApi.LastWarning)
                    ? $"⚠ {ContentGitApi.LastWarning}"
                    : "Ready");
            }
            catch (Exception ex)
            {
                SetStatus($"Error: {ex.Message}");
                Debug.LogException(ex);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task RefreshDataAsync()
        {
            isInitialized = await ContentGitApi.IsInitializedAsync();
            remoteFolders.Clear();
            checkedOutFolders.Clear();
            folderStatuses.Clear();

            if (isInitialized != true)
                return;

            try
            {
                remoteFolders = await ContentGitApi.GetRemoteFoldersAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not list remote folders: {ex.Message}");
            }

            try
            {
                checkedOutFolders = await ContentGitApi.GetCheckedOutFoldersAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not list checked-out folders: {ex.Message}");
            }

            var allStatuses = await ContentGitApi.GetAllFolderStatusesAsync();
            foreach (var f in checkedOutFolders)
            {
                if (!remoteFolders.Any(r => r.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    remoteFolders.Add(f);
                allStatuses.TryGetValue(f, out var s);
                folderStatuses[f] = s;
            }

            remoteFolders.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private void Rebuild()
        {
            setupBanner.style.display = isInitialized == false ? DisplayStyle.Flex : DisplayStyle.None;
            rowUpdaters.Clear();
            folderList.Clear();

            if (isInitialized != true)
                return;

            if (remoteFolders.Count == 0)
            {
                folderList.Add(new Label("No folders available in the content repository.")
                {
                    style = { unityFontStyleAndWeight = FontStyle.Italic, marginTop = 8, marginLeft = 8 }
                });
                return;
            }

            foreach (var folder in remoteFolders)
                folderList.Add(BuildRow(folder));
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

            var nameLabel   = row.Q<Label>("folder-name-label");
            var renameField = row.Q<TextField>("folder-rename-field");
            var renameBtn   = row.Q<Button>("btn-rename");
            var badge       = row.Q<Label>("folder-badge");
            var pullBtn     = row.Q<Button>("btn-pull");
            var pushBtn     = row.Q<Button>("btn-push");
            var checkoutBtn = row.Q<Button>("btn-checkout");
            var disconnectBtn = row.Q<Button>("btn-disconnect");
            var deleteBtn   = row.Q<Button>("btn-delete-remote");
            var commitRow   = row.Q<VisualElement>("commit-row");
            var commitField = row.Q<TextField>("commit-message-field");
            var commitConfirm = row.Q<Button>("btn-commit-confirm");

            nameLabel.text = folder;
            renameField.style.display = DisplayStyle.None;
            commitRow.style.display = DisplayStyle.None;

            // Static visibility based on checkout state
            checkoutBtn.style.display  = isCheckedOut ? DisplayStyle.None : DisplayStyle.Flex;
            disconnectBtn.style.display = isCheckedOut ? DisplayStyle.Flex : DisplayStyle.None;

            // Dynamic visibility delegate — called by poll and on initial build
            void UpdateRowState(FolderStatus s)
            {
                ApplyBadgeState(badge, isCheckedOut, s);
                pullBtn.style.display = isCheckedOut && ContentGitApi.RepositoryBehind > 0
                    ? DisplayStyle.Flex : DisplayStyle.None;
                pushBtn.style.display = isCheckedOut && !s.IsClean
                    ? DisplayStyle.Flex : DisplayStyle.None;
            }

            rowUpdaters[folder] = UpdateRowState;
            UpdateRowState(status);

            // Rename
            var renameEditing = false;

            void EnterRenameMode()
            {
                renameEditing = true;
                renameField.SetValueWithoutNotify(folder);
                nameLabel.style.display  = DisplayStyle.None;
                renameField.style.display = DisplayStyle.Flex;
                renameField.Focus();
                renameField.SelectAll();
            }

            void ExitRenameMode(bool commit)
            {
                renameEditing = false;
                nameLabel.style.display  = DisplayStyle.Flex;
                renameField.style.display = DisplayStyle.None;
                if (!commit) return;
                var newName = renameField.value?.Trim();
                if (!string.IsNullOrEmpty(newName) && newName != folder)
                    _ = RunAsync($"Renaming '{folder}' → '{newName}'…",
                        () => ContentGitApi.RenameFolderAsync(folder, newName));
            }

            renameBtn.clicked += () => { if (renameEditing) ExitRenameMode(true); else EnterRenameMode(); };
            renameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                    { evt.StopPropagation(); ExitRenameMode(true); }
                else if (evt.keyCode == KeyCode.Escape)
                    { evt.StopPropagation(); ExitRenameMode(false); }
            });

            // Checkout / Disconnect
            checkoutBtn.clicked += () => _ = RunAsync(
                $"Checking out '{folder}'…", () => ContentGitApi.CheckOutFolderAsync(folder));

            disconnectBtn.clicked += () =>
            {
                if (EditorUtility.DisplayDialog("Disconnect folder",
                    $"Remove '{folder}' from sparse-checkout and delete local files?\nUncommitted local changes will be lost.",
                    "Disconnect", "Cancel"))
                    _ = RunAsync($"Disconnecting '{folder}'…", () => ContentGitApi.DisconnectFolderAsync(folder));
            };

            // Pull
            pullBtn.clicked += () => _ = RunAsync(
                $"Pulling '{folder}'…", () => ContentGitApi.PullFolderAsync(folder));

            // Push — toggles commit row
            var commitRowVisible = false;
            pushBtn.clicked += () =>
            {
                commitRowVisible = !commitRowVisible;
                commitRow.style.display = commitRowVisible ? DisplayStyle.Flex : DisplayStyle.None;
                if (commitRowVisible) commitField.Focus();
            };

            commitConfirm.clicked += () =>
            {
                var msg = commitField.value?.Trim();
                if (string.IsNullOrEmpty(msg))
                {
                    EditorUtility.DisplayDialog("Commit message required",
                        "Please enter a commit message before confirming.", "OK");
                    return;
                }
                commitRowVisible = false;
                commitRow.style.display = DisplayStyle.None;
                commitField.SetValueWithoutNotify("");
                _ = RunAsync($"Committing & pushing '{folder}'…",
                    () => ContentGitApi.CommitAndPushFolderAsync(folder, msg));
            };

            // Delete remote
            deleteBtn.clicked += () =>
            {
                if (EditorUtility.DisplayDialog("Delete remote folder",
                    $"Permanently remove '{folder}' from the remote repository?\n\nThis cannot be undone.",
                    "Delete from remote", "Cancel"))
                    _ = RunAsync($"Deleting '{folder}' from remote…",
                        () => ContentGitApi.DeleteRemoteFolderAsync(folder));
            };

            return row;
        }

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

        private void SetButtonsEnabled(bool enabled)
        {
            rootVisualElement.Query<Button>().ForEach(b =>
            {
                if (b.name != "btn-new-folder")
                    b.SetEnabled(enabled);
            });
        }

        private async Task PollStatusesAsync()
        {
            if (busy || isInitialized != true || checkedOutFolders.Count == 0 || polling)
                return;

            polling = true;
            try
            {
                var statuses = await ContentGitApi.GetAllFolderStatusesAsync();
                foreach (var kvp in rowUpdaters)
                {
                    statuses.TryGetValue(kvp.Key, out var s);
                    kvp.Value(s);
                    if (checkedOutFolders.Any(c => c.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)))
                        folderStatuses[kvp.Key] = s;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Status poll failed: {ex.Message}");
            }
            finally
            {
                polling = false;
            }
        }

        private static void ApplyBadgeState(Label badge, bool isCheckedOut, FolderStatus status)
        {
            badge.RemoveFromClassList("cs-badge--on");
            badge.RemoveFromClassList("cs-badge--off");
            badge.RemoveFromClassList("cs-badge--modified");
            badge.RemoveFromClassList("cs-badge--staged");

            if (!isCheckedOut)
            {
                badge.text = "not checked out";
                badge.AddToClassList("cs-badge--off");
            }
            else if (status.IsClean)
            {
                badge.text = "clean";
                badge.AddToClassList("cs-badge--on");
            }
            else if (status.Staged > 0 && status.Modified == 0 && status.Deleted == 0 && status.Untracked == 0)
            {
                badge.text = status.ToString();
                badge.AddToClassList("cs-badge--staged");
            }
            else
            {
                badge.text = status.ToString();
                badge.AddToClassList("cs-badge--modified");
            }
        }

        private void SetStatus(string msg)
        {
            if (statusLabel != null)
                statusLabel.text = msg;
        }

        private static Texture2D LoadIcon(string name) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>($"{PackageRoot}/Editor/UI/Icons/{name}.png");

    }
}
