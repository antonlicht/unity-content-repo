# Content Repo

Unity editor tooling for managing a shared content repository via git sparse-checkout, plus an
Addressables build, CDN upload, and local-dev-override pipeline.

Each developer checks out only the content package folders they need. Assets are built per-package,
uploaded to a CDN (S3 + CloudFront), and loaded at runtime via a master manifest — without bundling
any content into the player build.

**Package name:** `com.antonlicht.content-repo`

---

## Installation

Install as an embedded package: drop the folder into `Packages/` in your Unity project.  
Or add via **Window > Package Manager** using a git URL.

---

## Initial Setup

1. Open **Project Settings > Content Repo** and configure:
   - **Local path** — path inside the project where the content repository will live (e.g. `Assets/Content`).
   - **Remote URL** — the git remote of the content repository.
   - **Default branch** — the branch to track (e.g. `main`).
   - **Upload** sub-section — S3 bucket, region, CloudFront distribution ID, CDN domain, and AWS credentials.
2. Commit `ProjectSettings/ContentRepo.asset` so the whole team shares the settings.
3. Open **Tools > Content Browser**, then click **Initialize** to clone the content repository.
4. Follow **[Documentation/Setup-AWS.md](Documentation/Setup-AWS.md)** to set up the CDN infrastructure.

---

## Daily Workflow

Open **Tools > Content Browser**. Two tabs are available:

### Repository tab

Lists every top-level folder in the content repository's remote HEAD. Per folder:

| Action | How |
|---|---|
| Check out | Click the folder row |
| Pull latest | `↓` icon (visible when behind remote) |
| Commit & Push | `↑` icon → inline message field |
| Rename | `✎` icon |
| Disconnect | Remove from sparse-checkout + delete local files |
| Delete remote | `🗑` icon |

`+ New Folder` creates a new package folder, commits a `.gitkeep`, and pushes.  
`Refresh` re-reads remote and local state. `Pull All` pulls every checked-out folder.

Live status badges update every 5 seconds:

| Badge | Meaning |
|---|---|
| `clean` (green) | No local changes |
| `↑N ~M ?K` (yellow) | Staged / modified / untracked counts |
| `stg: <id>` | Build ID currently live on staging |
| `prod: <id>` | Build ID currently live on production |
| `→ prod ready` | Staging is ahead of production |

### Deploy tab

See **[Documentation/Pipeline-Usage.md](Documentation/Pipeline-Usage.md)** for the full build, upload,
promote, and local-dev-override workflow.

---

## Runtime Loading

```csharp
using ContentRepo;
using UnityEngine.AddressableAssets;

// At app startup — fetches the CDN manifest and registers all remote catalogs.
var result = await ContentRepoRuntime.InitializeAsync(
    baseUrl: "https://xxxx.cloudfront.net",
    environment: "production");

// Assets across all content packages are now available via Addressables.
var prefab = await Addressables.LoadAssetAsync<GameObject>("Episode01/Hero").Task;
```

`InitializeAsync` falls back to the last cached manifest when the CDN is unreachable, so the app
can boot offline. See [Pipeline-Usage.md §Loading content at runtime](Documentation/Pipeline-Usage.md#loading-content-at-runtime)
for the config-driven bootstrap option.

---

## Requirements

- Unity 6000.0 or newer.
- `com.unity.addressables` 2.9.1 or newer.
- `git` available on the system `PATH`.
- AWS CLI on `PATH` for upload operations. See [Setup-AWS.md](Documentation/Setup-AWS.md).
