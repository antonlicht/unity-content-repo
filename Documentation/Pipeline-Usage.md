# Pipeline Usage

Day-to-day use of the Content Repo build and upload pipeline.

## Prerequisites

- The content repository is initialized (**Window > Content Browser > Initialize**).
- At least one content package folder is checked out.
- Addressables is initialized in the project (**Window > Asset Management > Addressables > Groups >
  Create Addressables Settings**). You do **not** need to create groups by hand — the build creates
  and populates a group named after each package automatically (see
  [Adding a new content package](#adding-a-new-content-package)).
- **Project Settings > Content Repo > Upload** has S3 bucket, region, CloudFront distribution ID, and domain filled in.
- AWS credentials are configured — use **Configure credentials…** in Project Settings > Content Repo > Upload, or see `Setup-AWS.md`.

Everything below happens in the single **Content Browser** window (**Window > Content Browser**, or
the cloud icon in the main editor toolbar). Bulk actions live in the top toolbar; per-package actions
live on each package row.

---

## Building a single content package

Find the package row and click **Build**.

The build automatically creates/reuses an Addressables group named after the package, points it at
the remote build/load path profile variables, includes every asset under the package folder, and
builds bundles + a remote catalog. Artifacts land in
`<BuildOutputRoot>/builds/<buildId>/<platform>/<contentPackageName>/` (default `BuildOutputRoot` is
`Builds/Content`). The row status shows `ok` or `error`; the log pane streams progress.

## Building all checked-out packages

Click **Build all** in the toolbar. Packages build sequentially; failures are logged but don't abort the rest.

---

## Uploading to staging

Click **Upload** on a package row. Bundles are pushed to S3 and the staging manifest entry is updated.

Click **Upload all** to do this for every checked-out package (this also republishes the staging manifest).

> Upload always targets **staging**. Production is only updated via promotion.

## Promoting staging → production

When a package's staging build differs from production, a **Promote** button appears on its row.

- Click **Promote** on a row to promote that package.
- Click **Promote all** in the toolbar to promote every checked-out package at once.

Promotion only rewrites the production manifest to point at the staging build — no files move.

## Full pipeline in one action

The ⋮ menus expose combined steps: **Build and Deploy** on a package row builds then uploads that
package to staging; **Build and Deploy All** in the toolbar's ⋮ menu does it for every checked-out
package.

---

## Status badges

Each package row shows small badges:

| Badge | Meaning |
|---|---|
| `not checked out` | On the remote but not checked out locally |
| `clean` | No local git changes |
| `+K` / `M` / `-D` | K untracked / M modified+staged / D deleted files |
| `staging: a1b2c3d4` | Build ID live on staging (first 8 chars) |
| `production: a1b2c3d4` | Build ID live on production (first 8 chars) |

When staging is ahead of production, the **Promote** button (and the toolbar **Promote all**) becomes
visible for that package.

---

## Testing local changes without deploying

Checked-out packages are testable in Play Mode **without uploading anything**, and for the common case
this is automatic.

### Fast Mode — automatic, no build needed

When you enter Play Mode, every checked-out package is automatically registered for **AssetDatabase
(Fast Mode)** and the Addressables Play Mode Script is switched to *Use Asset Database (fastest)*.
Assets are served straight from the Unity AssetDatabase — including brand-new packages that have
never been deployed. Just enter Play Mode.

### Local Bundles mode — test with built bundles

Use this when you need to test the exact bundle output, e.g. to catch packing or loading issues. This
is currently driven through the scripting API (there is no dedicated menu in the Content Browser):

```csharp
using ContentRepo.Editor;

// Build the package and register a local file:// catalog override, then enter Play Mode.
await ContentLocalDevApi.BuildAndRegisterLocalBundlesAsync("Episode02", msg => Debug.Log(msg));
```

`BuildAndRegisterLocalBundlesAsync` switches the Play Mode Script to *Use Existing Build* automatically.
Bundles are read from `<BuildOutputRoot>/builds/<buildId>/<platform>/<pkg>/`.

### Scripting API

```csharp
using ContentRepo.Editor;

// Fast Mode
ContentLocalDevApi.SetupForFastMode("Episode02", msg => Debug.Log(msg));
ContentLocalDevApi.ClearFastMode("Episode02");

// Local Bundles
await ContentLocalDevApi.BuildAndRegisterLocalBundlesAsync("Episode02", msg => Debug.Log(msg));
ContentLocalDevApi.ClearLocalBundles("Episode02");

// Query active overrides at runtime
if (ContentLocalDevOverrides.TryGet("Episode02", out var entry))
    Debug.Log($"{entry.Mode} — {entry.LocalCatalogUrl}");
```

For full API reference and internals see [Local-Dev-Overrides.md](Local-Dev-Overrides.md).

---

## CI invocation

The build and upload APIs expose static CLI entry points callable from `Unity -batchmode -executeMethod`.

### Build a single package

```bash
Unity -batchmode -nographics -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod ContentRepo.Editor.ContentBuildApi.BuildContentPackageCLI \
  -contentPackage Episode01
```

### Build all checked-out packages

```bash
Unity -batchmode -nographics -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod ContentRepo.Editor.ContentBuildApi.BuildAllCLI
```

### Upload a single package to staging

```bash
Unity -batchmode -nographics -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod ContentRepo.Editor.ContentUploadApi.UploadContentPackageCLI \
  -contentPackage Episode01 \
  -environment staging
```

### Upload all to staging

```bash
Unity -batchmode -nographics -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod ContentRepo.Editor.ContentUploadApi.UploadAllCLI \
  -environment staging
```

`UploadAllCLI` also republishes the staging manifest. `-environment` is optional and defaults to the
configured staging prefix. There is also a `ContentRepo.Editor.ContentUploadApi.RunCleanupCLI`
entry point (optional `-generation`) that runs the retention cleanup that the scheduled Lambda runs.

The process exits `0` on success and `1` on failure.

---

## Adding a new content package

1. In the Content Browser click the `+` (new package) row and enter the package name (e.g. `Episode02`).
   This creates `<content-repo-root>/Episode02/` locally and adds it to the sparse-checkout.
2. Place the package's assets inside `<content-repo-root>/Episode02/` and let Unity import them.
3. Test immediately: enter Play Mode. The package is auto-registered for Fast Mode, so its assets are
   served from the AssetDatabase even before any build or upload.
4. When ready, click **Build** then **Upload** on the package row (or **Build and Deploy**).

> The first Play or build creates the Addressables group `Episode02` and its group file
> `_groups/Episode02.asset` inside the content repo. **Commit and push this file** — it stores the group's
> stable GUID (the package's bundle identity) and any custom addresses. You do not need to create
> Addressables groups or configure remote build/load paths by hand; the build does it.

## Addressables groups and partial checkouts

Each developer checks out only some packages, so which content groups are registered is machine state.
The tooling keeps it out of the shared project:

- The committed `AddressableAssetSettings.asset` never lists a content group. On every machine the
  registered content groups are exactly `_groups/<pkg>.asset` for each package in the sparse checkout;
  they are registered in memory when the editor loads, after checkout, disconnect and pulls, and before
  Play and builds. Whenever the settings are saved, content groups are left out of the file (a save
  hook removes them for the save and puts them back right after).
- A package's group is always looked up by its path, never by name, and created only for a checked-out
  package that has no committed group yet. Moving a new group or its schemas into `_groups/` must
  succeed or the operation fails.
- Groups of packages that are not checked out are never registered, so Addressables never purges their
  entries, and their files are never written on that machine. `_groups/` always holds every package's
  files exactly as committed.
- Disconnect, checkout, rename and delete only change the content repo and in-memory state. Content
  Browser commits contain only the package's own folder and group files.
- A group named like a package that lives outside the content repo (for example created by an old
  tool version in `AddressableAssetsData/AssetGroups`) is reported, and Play Mode setup and builds of
  that package are blocked until it is migrated into `_groups/<pkg>.asset` and removed, because its
  `m_GUID` decides the bundle identity.

---

## Loading content at runtime

```csharp
using ContentRepo;
using UnityEngine.AddressableAssets;

// At app startup — fetches the CDN manifest and registers all remote catalogs.
var result = await ContentRepoRuntime.InitializeAsync(
    baseUrl: "https://xxxx.cloudfront.net",
    environment: "production",
    generation: "gen/1");

if (result.Manifest == null)
{
    // No CDN and no cached manifest — degrade gracefully.
    return;
}

if (result.UpdateRequired)
{
    // App is below manifest.minAppVersion (result.UpdateForced == true) or recommendedAppVersion.
    // Prompt the player to update. See ContentRepoRuntime.OnUpdateRequired.
}

// All content-package addresses are now available.
var prefab = await Addressables.LoadAssetAsync<GameObject>("Episode01/Hero").Task;
```

The `generation` argument must match the generation the content was published under (see
[README §Generations](../README.md#generations)). Falls back to the last cached manifest
(`persistentDataPath/ContentRepo/<generation>/<env>/manifest.json`) when the CDN is unreachable.

### Refreshing while running

Call `ContentRepoRuntime.RefreshAsync(baseUrl, environment, generation)` to re-fetch the manifest.
Catalogs whose buildId is unchanged are skipped. Pass `force: true` to `InitializeAsync` to reload
every catalog regardless.

### Config-driven bootstrap (optional)

1. **Assets > Create > Content Repo > Runtime Settings**.
2. Move the asset into any `Resources/` folder and keep its name `ContentRepoRuntimeSettings`
   (it is loaded via `Resources.Load`).
3. Fill in **Base Url**, **Environment**, and **Generation**.
4. Tick **Initialize On Load** to run `InitializeAsync` automatically at `BeforeSceneLoad`.

---

## Troubleshooting

- **`Failed to start 'aws'`** — AWS CLI is not installed or not on PATH. Run `aws --version`. See `Setup-AWS.md`.
- **No credentials / `AccessDenied`** — open **Project Settings > Content Repo > Upload > Configure credentials…** and re-enter your keys.
- **`Addressables is not initialized`** — open **Window > Asset Management > Addressables > Groups**, choose **Create Addressables Settings**, then re-run the build.
- **`Addressables profile '<name>' not found`** — the build profile named in **Project Settings > Content Repo > Build** doesn't exist. Create it in the Addressables Profiles window or change the name.
- **`No .bundle files found`** — the package folder has no importable assets, or the group's build path was left at a non-remote value. Confirm the folder contains assets and retry.
- **Local Dev: `'{path}' not found in the Asset Database`** — check out the folder first.
- **Local Dev: `No assets found under '…'`** — Unity hasn't imported the files yet. Check the Console for import errors.
- **New package not loading in Play Mode** — it isn't in the CDN manifest yet, but a checked-out package is auto-registered for Fast Mode; make sure the folder is actually checked out and contains assets.
- **Addressables group is empty after checkout** — the group asset is restored from `_groups/` automatically on checkout (a local modification is copied to `Library/ContentRepo/Backups/` first). If it still appears empty, the group file may not have been committed to the content repo yet; enter Play Mode or run the first build and push `_groups/<packageName>.asset`.
- **`Two Addressables groups are named '<pkg>'`** — a duplicate of the package's group exists outside the content repo. Move its entries (and, to keep the CDN bundle identity, its `m_GUID`) into `_groups/<pkg>.asset`, then delete the duplicate.
- **`The group for '<pkg>' is committed … but missing on disk`** — pull the content repo or check the package out again; creating a new group would change the package's bundle identity.
- **`Not saving '_groups/<pkg>…': its package is not checked out`** — something modified the group of a package this machine doesn't have; the change is discarded on purpose and the file stays as committed.
- **CloudFront `TooManyInvalidationsInProgress`** — free quota is 1 000 paths/month. Batch uploads or wait for the previous invalidation to complete.
- **CDN serves stale content after upload** — the Upload action invalidates the uploaded prefix and the manifest path automatically. If you pushed manually, run `aws cloudfront create-invalidation` yourself.
- **Runtime: `Manifest fetch failed … Falling back to cache`** — the app booted offline from a cached manifest. Fix CDN connectivity or certificate issues on the device.
- **Runtime: `Catalog load failed`** — typically a 403 on the bundle host. Re-check bucket policy and CORS config in `Setup-AWS.md` step 7.
- **Runtime: addresses resolve to old assets after `RefreshAsync`** — the manifest entry's `buildId` didn't change. Call `InitializeAsync(..., force: true)` to reload every catalog.
- **Runtime: `Unable to open archive file: Library/com.unity.addressables/aa/…_unitybuiltinassets_….bundle` (or `…_monoscripts_…`), then `Resource '<asset>' failed to load: invalid object`** — the package's shared **built-in-shaders** / **MonoScript** bundles were baked with the Default group's *local* paths, so they were never uploaded and can't be found at runtime. Fixed in 0.3.17: the build now routes shared bundles into the package's own group (`SharedBundleSettings = CustomGroup`) so they upload to and load from the CDN. **Rebuild and re-upload any package built before 0.3.17.**
- **Runtime: `The AssetBundle '…_monoscripts_…' / '…_unitybuiltinassets_…' can't be loaded because another AssetBundle with the same files is already loaded`** — a content package is DLC that runs inside the full client, which already loads identical shared bundles. Both builds name their MonoScript and built-in-shaders bundles via `ProjectName` = `Hash128(projectName)` — the same *internal* identity (CAB) — and Unity refuses to load a second bundle whose internal identity matches an already-loaded one (a known Addressables multi-catalog limitation). Fixed in 0.3.20: the content build gives **both** shared bundles a **unique internal name** (`MonoScriptBundleNaming` + `BuiltInBundleNaming` = `Custom`, prefix `contentrepo_<package>`). The "same files" check keys on the bundle's internal identity, not its objects, so a unique name lets both load. **Do not _suppress_ the MonoScript bundle** (empty prefix): content `Naninovel.Script` ScriptableObjects need it to resolve their script type, or they load as null (`Failed to load '<script>' resource of type 'Naninovel.Script'`). **Rebuild and re-upload any package built before 0.3.20.**
