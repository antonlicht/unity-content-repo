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

- A top **toolbar**: **Instructions** on the left (see [Instructions for your team](#instructions-for-your-team)),
  and bulk actions on the right — Refresh, Pull all, Build all, Upload all, Promote all, and a ⋮ "More
  actions" menu. Each bulk button is hidden until it is relevant.
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

Local changes do not block a pull. For each changed file that the incoming commits also change, Pull (and
the pull that Check out does) drops the local change when the incoming version already contains it, and
puts it back on top of the pulled version when the two merge cleanly. For the rest it asks whether to use
the team's version, listing the files; Cancel pulls nothing. Every local version it touches is copied to
`Library/ContentRepo/Backups/<time>-pull` first and put back if the pull fails.

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

**Fixing lost changes.** Click the red `repo lost changes` badge (or ⋮ → **Lost changes…**) to open the
**Lost work** window. It says in plain words which merge lost the work and who made it, and lists whose work
was lost: each commit whose changes were dropped, with its author, date, message and number of files. Below
that come every file to restore grouped by folder, the files both sides changed (where the merge kept only one
side's version), and files it leaves alone (changed by a local commit that is not pushed yet, or a folder
`.meta` whose folder still has files). **Open** next to a file writes a copy of its lost version to
`Temp/ContentRepoPreview` (named `… (lost version).ext`) and opens it in its default app, without changing
the package; for files both sides changed it opens the version the merge threw away. **Copy list** puts the
whole list on the clipboard. On a checked-out package that is up to date with the remote, **Restore** writes
each dropped file back with the version from the commit that changed it (or deletes it again, when that change
was a deletion) and stages it, together with the `.meta` file that belongs to it. It works from the repository
as it is when you click, and if anything goes wrong midway it puts every file back. Nothing is committed:

1. Pull (the window offers **Pull now** when the copy is behind), and make sure the files being restored have
   no local changes. A file whose local change already is the lost version (for example because a tool wrote
   it again) does not count.
2. Click the red badge, check the list, and click **Restore**. The restored files appear as changes on the row,
   together with the package's group file, which Unity updates to match the restored assets.
3. Check them in the editor (open the scenes, play through the affected content). Files changed on both sides are not
   restored: use **Open** to see the lost version and copy what is missing by hand, or keep them as they are.
   **Undo restore** takes the restored files out again (files edited since the restore stay, together with
   their `.meta` files); it is not offered once the restored files are committed.
4. Commit and push everything from the row. The badge clears once the push reaches the remote.
5. Build, upload, check that staging shows the check mark, and promote.

Opening the window again shows which files are already restored in this copy, and **Restore** only writes
the remaining ones. Restoring needs the files' contents, so in a partial clone it downloads them.

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

## Instructions for your team

The **Instructions** button in the Content Browser toolbar (or **Window > Content Browser Instructions**)
opens a short guide for everyone who works with content, written for people who do not use git: how a
change goes live as a step-by-step chart, which version each place shows, how to work together, what the
badges and banners mean, and what to set up on a new computer.

The package's topics are generic and live in `Editor/Guide/Guide.md`. A project adds its own topics,
replaces or extends the package's ones, and fills in a few named values with files called
`ContentRepoGuide.md` anywhere under `Assets` (for example `Assets/Editor/ContentRepoGuide.md`). The
window reloads them whenever it gets focus, and its sidebar links to the project's file.

Each topic starts with a header between `---` lines:

```markdown
---
id: menu-texts            # topic id; the package uses release, versions, together, warnings, first-time
title: Menu texts         # sidebar and page title
icon: pencil              # an icon of the package (Editor/UI/Icons) or an asset path to a texture
after: versions           # optional: place the topic after another one
mode: replace             # replace (default), append, prepend or remove, for an id that already exists
---
Text. **Bold**, *italic*, `code`, and coloured words: <copy>your own copy</copy>, <test>test version</test>,
<live>live version</live>, <team>…</team>, <warn>…</warn>, <error>…</error>.

## A heading
- A bullet
| Column | Column |
|---|---|
| Cell | Cell |
> A note box. A line that is only a link becomes a button:
> [Which version do I see?](#versions)
[Another topic](#release)
```

The release chart is written as stages with steps. A step marked `button` gets a grey square behind its icon
because it is a button in the Content Browser:

```markdown
::: stage test | Test version | Who sees it. | upload
button: cloud-upload | Upload | What to do. | An optional grey tip.
step: circle-check | Check it | What to do.
:::
```

The stage key (`computer`, `team`, `test`, `live`) picks the colour, and the last field is the action that
leads into the stage in the overview strip. Package text can contain `{{name: default}}` values; a project
sets them in a section with `id: values` and one `name: text` per line. The package's guide uses
`test-version-audience`, `check-it` and `check-it-tip`, which describe which builds load the test version.

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
