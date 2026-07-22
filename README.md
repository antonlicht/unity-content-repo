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

1. Open **Project Settings > Content Repo** and configure the three sub-pages:
   - **Content Repo** — **Local path** (where the content repository lives, e.g. `Assets/Content`),
     **Remote URL** (the git remote of the content repository), and **Branch** (the branch to track,
     e.g. `main`).
   - **Content Repo > Build** — Addressables profile name, remote load/build path variable names,
     build output root, and the **Generation** string (default `gen/1`; see
     [Generations](#generations)).
   - **Content Repo > Upload** — S3 bucket, region, CloudFront distribution ID, CDN domain, the
     staging/production environment prefixes, and AWS credentials.
2. Commit `ProjectSettings/ContentRepo.asset`, `ContentRepoBuild.asset`, `ContentRepoGeneration.asset`,
   and `ContentRepoUpload.asset` so the whole team shares the settings.
3. Open **Window > Content Browser** (or click the cloud icon in the main editor toolbar), then click
   **Initialize** to clone the content repository.
4. Follow **[Documentation/Setup-AWS.md](Documentation/Setup-AWS.md)** to set up the CDN infrastructure.

---

## Daily Workflow

Open **Window > Content Browser** (or the cloud icon in the main toolbar). Everything lives in one
window:

- A top **toolbar** of bulk actions — Refresh, Pull all, Build all, Upload all, Promote all, and a
  ⋮ "More actions" menu. Each bulk button is hidden until it is relevant.
- A **new-package** row (the `+` icon) for creating a package folder.
- The **package list**. Each row combines git state and deploy actions.
- An **Infrastructure** section (deploy/teardown of the cleanup Lambda) and a **Log** pane.

Per-package row actions:

| Action | How |
|---|---|
| Check out | Check-out button on a not-checked-out package row |
| Pull latest | Pull button (shown when the repo is behind the remote) |
| Commit & Push | Push button, or right-click a changed file → **Commit and push…** |
| Build / Upload / Promote | Per-row **Build**, **Upload**, and **Promote** buttons (Promote appears only when staging is ahead of production) |
| Rename | ⋮ menu → **Rename Package** |
| Disconnect | ⋮ menu → **Disconnect** (removes the folder from sparse-checkout and deletes the local files) |
| Delete from repository | ⋮ menu → **Delete from repository** |

The `+` row creates a new package **folder locally** and adds it to the sparse-checkout. Git can't
track an empty folder, so the package is pushed to the remote when you first **commit content** in it
(or when the first **Build** commits its `_groups/<name>.asset` group file).

`Refresh` re-reads remote and local state. `Pull all` pulls every checked-out folder. Package status
is polled about once a minute.

Status badges per row:

| Badge | Meaning |
|---|---|
| `not checked out` | Folder exists on the remote but isn't checked out locally |
| `clean` | No local changes |
| `+K` | K untracked files |
| `M` | M modified/staged files |
| `-D` | D deleted files |
| `staging: <id>` | Build ID currently live on staging (first 8 chars) |
| `production: <id>` | Build ID currently live on production (first 8 chars) |

When staging is ahead of production, the per-row **Promote** button (and the toolbar **Promote all**)
becomes visible.

See **[Documentation/Pipeline-Usage.md](Documentation/Pipeline-Usage.md)** for the full build, upload,
promote, and local-dev-override workflow.

---

## Generations

A *generation* is a string (default `gen/1`) that namespaces every CDN path
(`<generation>/<env>/manifest.json`, `<generation>/builds/…`) and is stamped into each build's
metadata. Bump it when a Unity upgrade changes the Addressables bundle format so that new and old
players never load incompatible bundles. Manage it in **Project Settings > Content Repo > Build**
(the **Bump generation** button) — the Content Browser also shows a banner when your Unity version
no longer matches the version the current generation was built with.

The **runtime** must be initialized with the same generation string it was published under (see below).

---

## Runtime Loading

```csharp
using ContentRepo;
using UnityEngine.AddressableAssets;

// At app startup — fetches the CDN manifest and registers all remote catalogs.
var result = await ContentRepoRuntime.InitializeAsync(
    baseUrl: "https://xxxx.cloudfront.net",
    environment: "production",
    generation: "gen/1");

// Assets across all content packages are now available via Addressables.
var prefab = await Addressables.LoadAssetAsync<GameObject>("Episode01/Hero").Task;
```

`InitializeAsync` falls back to the last cached manifest when the CDN is unreachable, so the app
can boot offline. It also gates on the manifest's `minAppVersion` / `recommendedAppVersion` and fires
`ContentRepoRuntime.OnUpdateRequired` when the running build is too old. See
[Pipeline-Usage.md §Loading content at runtime](Documentation/Pipeline-Usage.md#loading-content-at-runtime)
for the config-driven bootstrap option.

---

## Requirements

- Unity 6000.0 or newer.
- `com.unity.addressables` 2.9.1 or newer.
- `git` available on the system `PATH`.
- AWS CLI on `PATH` for upload operations. See [Setup-AWS.md](Documentation/Setup-AWS.md).
