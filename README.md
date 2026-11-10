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
   **Initialize** to clone the content repository. The clone is a partial clone (`--filter=blob:none`): git
   downloads a file's content only when it is checked out, so together with the sparse checkout you only
   download the packages you check out (images, audio and their history included). Existing full clones are
   switched to a partial clone automatically; they keep what they already downloaded. Viewing the history of a
   package you have not checked out downloads those files on demand, which needs a network connection.
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
| Disconnect | ⋮ menu → **Disconnect** (removes the folder from sparse-checkout and deletes the local files; the package's group files and the project's Addressables settings are left untouched) |
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
| `N to pull` | N commits on the remote touch this package and are not pulled yet |
| `repo lost changes` (red) | A recent merge on the remote dropped changes to this package that are still missing there (see below) |
| `staging: <id>` | Build ID currently live on staging (first 8 chars) |
| `production: <id>` | Build ID currently live on production (first 8 chars) |

The staging and production badges also say how that build relates to the content repository, for every
package, checked out or not. Hover a badge for the build's source commit and the commits it is missing.

| Badge state | Meaning |
|---|---|
| check mark | The build was made from exactly the files that are in the repository |
| `N behind` (amber) | N newer commits in the repository are not in the build: pull, build and upload (on production: bring staging up to date, then promote) |
| `N behind` (no color change) | Production only: staging already matches the repository and waits to be promoted |
| `not in repo` (amber) | The build contains changes that are not in the repository as built (not committed, not pushed, or committed with further edits) |
| `removed` (amber) | The package was deleted or renamed in the repository: remove it from that environment |
| `repo lost changes` (red) | A merge after this build's source dropped changes to this package that are still missing: rebuilding from the repository ships that loss (the build may still have the work) |
| `unverified` (amber) | The build cannot be compared: its source commit is not on the remote, the branch is missing, or its fingerprint failed |
| no mark | No newer commits, but the build has no fingerprint (made before 0.5.0, or an older tool rewrote the manifest), so local changes in it cannot be ruled out |

Every build records a fingerprint of the files it was made from: `gitSha`, `sourceTree`, `sourceDirty`
and `builtAt` in `build-metadata.json`, copied as `sourceSha`, `sourceTree`, `sourceDirty` and `builtAt`
into the manifest's platform entry (game clients ignore these fields). A build made from uncommitted
files turns into a check mark as soon as exactly those files are committed and pushed; no rebuild or
upload is needed. When a rebuild produces the same bundles with a later, clean fingerprint, **Upload**
appears again so staging can carry it. Upgrade the whole team together: a pre-0.5 version of the tool
that writes a manifest drops the fingerprints of every package in it.

**Merges that drop changes.** For the last 10 merge commits that touch a package, the window compares
the merge with its parents and their merge base. A file counts as dropped when only one parent changed
it (so a normal merge takes that change without a conflict) and the merge kept the other parent's
unchanged version, as long as the file is still in that state on the remote. Files both parents changed,
their `.meta` companions, renames and edits made in the merge commit itself are not counted. Only
commits and trees are read, so nothing is downloaded and every package can be checked, checked out or
not.

When staging differs from production, the per-row **Promote** button (and the toolbar **Promote all**)
becomes visible. Promote fetches first and warns, listing the reasons, when a staging build that would
go live does not match the repository (for every platform in it), when the package has lost changes,
or when the fetch failed. Build fetches and warns when the package has commits on the remote that are
not pulled yet, when it has lost changes, or when the fetch failed.

If a pull hits a conflict, git stays in the middle of a merge (or a rebase, cherry-pick, revert or
`git am`) and changes that are not committed on this branch yet show up as local changes. The window
then shows a banner that explains how to finish it, with an **Abort** button. Until it is finished in a
git client or aborted, the window refuses to commit, discard, delete files, check out, disconnect,
create, rename or delete packages, build, or run the index repair. The same applies while files have
unresolved conflicts without an operation in progress (for example after `git stash pop`). Upload and
Promote of existing builds still work.

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
