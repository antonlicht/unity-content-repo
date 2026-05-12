# Content Browser

Unity editor tooling for managing a shared content repository via git sparse-checkout.
Each developer checks out only the folders (chapters, episodes, etc.) they need — no terminal required.

## Installation

Install as an embedded package: drop the package folder into the `Packages/` directory of your Unity project.
Or add it via `Window > Package Manager` using a git URL.

**Package name:** `com.antonlicht.content-browser`

> The package folder may still be named `com.antonlicht.content-submodules` on disk if you are upgrading
> from an earlier version. Unity discovers embedded packages by `package.json`, not folder name, so this
> has no functional impact. You can rename the folder manually once Unity is closed.

## Setup

1. Open **Project Settings > Content Browser**.
2. Configure:
   - **Local path** — path inside the Unity project where the content repository will live, relative to the project root (e.g. `Assets/Content`).
   - **Remote URL** — the git remote of the content repository.
   - **Default branch** — the branch to track (e.g. `main`).
3. Commit `ProjectSettings/ContentBrowser.asset` to the parent project so the team can share the settings.
4. Open **Tools > Content Browser**.
5. Click **Initialize**. This clones the content repository at the configured path and writes a `.gitignore` next to it so the parent project's git status stays clean.

## Daily use

The editor window lists every top-level folder available in the content repository's remote HEAD.
For each folder you can:

- **Check out** — adds the folder to sparse-checkout and pulls.
- **Pull** (`↓` icon, visible when behind remote) — pulls the latest changes.
- **Commit & Push** (`↑` icon, visible when there are local changes) — opens an inline commit message field, then commits and pushes.
- **Rename** (`✎` icon) — inline rename that commits and pushes.
- **Disconnect** — removes the folder from sparse-checkout and deletes local files (with confirmation).
- **Delete remote** (`🗑` icon) — removes the folder from the remote repository (with confirmation).

The `+ New Folder` button (top bar) creates a new folder in the repository, commits a `.gitkeep`, and pushes.

`Refresh` re-reads remote and local state. `Pull All` runs a single pull for everything currently checked out.

The badge on each row updates live every 5 seconds using a single `git status -sb` call:
- `clean` (green) — no local changes
- `↑N ~M ?K` (yellow) — staged / modified / untracked counts
- `↑N` alone (blue) — only staged, nothing dirty
- `not checked out` (grey)

## Scripting API

```csharp
using ContentBrowser.Editor;

// One-time initialization
await ContentGitApi.InitAsync();

// Folder management
await ContentGitApi.CheckOutFolderAsync("Chapter01");
await ContentGitApi.PullFolderAsync("Chapter01");
await ContentGitApi.CommitAndPushFolderAsync("Chapter01", "Update level layout");
await ContentGitApi.DisconnectFolderAsync("Chapter01");

// Live status
var statuses = await ContentGitApi.GetAllFolderStatusesAsync();
// ContentGitApi.RepositoryAhead / .RepositoryBehind — set by the last status poll

// Fired after every mutating operation
ContentGitApi.OnStateChanged += () => { /* refresh your tooling */ };

// Settings
var url = ContentBrowserSettings.instance.RemoteUrl;
```

## Zero-noise parent repo

`InitAsync` uses a plain `git clone` (not `git submodule add`) and writes a `.gitignore` next to the
cloned directory (e.g. `Assets/.gitignore`) that ignores both the content folder and its Unity `.meta`
file. After the team lead commits `ProjectSettings/ContentBrowser.asset` and `Assets/.gitignore` once,
every other developer can initialize Content Browser without any git noise appearing in the parent project.

## Requirements

- Unity 6000.0 or newer.
- `git` available on the system `PATH`. If git is missing the window shows a clear error in its status bar.
