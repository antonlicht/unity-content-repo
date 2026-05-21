# Pipeline Usage

Day-to-day use of the Content Repo build and upload pipeline.

## Prerequisites

- The content repository is initialized (**Tools > Content Browser > Initialize**).
- At least one content package folder is checked out.
- Addressables is set up with groups named `<ContentPackageName>_*` (see [Adding a new content package](#adding-a-new-content-package)).
- **Project Settings > Content Repo > Upload** has S3 bucket, region, CloudFront distribution ID, and domain filled in.
- AWS credentials are configured — use the **Configure credentials…** button in Project Settings > Content Repo > Upload, or see `Setup-AWS.md`.

---

## Building a single content package

1. Open **Tools > Content Browser**.
2. Switch to the **Deploy** tab.
3. Find the package row and click **Build**.

Artifacts are written locally to `Builds/Content/builds/<buildId>/<platform>/<contentPackageName>/`. The status badge becomes `ok` on success or `error` on failure. The log pane streams progress.

## Building all checked-out packages

Click **Build all** in the Deploy tab. Packages are built sequentially; the loop continues on individual failures.

---

## Uploading to staging

Click **Upload** on a package row to upload its bundles to S3 and update the staging manifest entry. The game can now load the new version from staging.

Click **Upload all** to do this for every checked-out package.

Upload always targets **staging**. Production is only updated via promotion (see below).

## Promoting staging → production

Once you've verified the staging build:

- Click **Promote → prod** on a specific row (the button appears only when staging has a newer build than production).
- Or click **Promote all → prod** in the toolbar to promote every checked-out package at once.

Promotion only updates the manifest — no files move in S3.

## Full pipeline in one action

Click **Build + Upload all** to build every checked-out package and upload each one to staging (including manifest update) in a single operation.

Per-row **Build+Upload** does the same for a single package.

---

## Reading the live status badges

Each row in the Deploy tab shows small status badges:

| Badge | Meaning |
|---|---|
| `stg: a1b2c3d4` | This build ID is currently live on staging |
| `stg: —` | Package is not yet published to staging |
| `→ prod ready` | Staging has a newer build than production — ready to promote |
| `prod: a1b2c3d4` | This build ID is currently live on production |
| `prod: —` | Package is not yet on production |
| `local: AssetDB` (amber) | AssetDatabase override active — CDN bypassed, assets served from AssetDatabase |
| `local: bundles` (amber) | LocalBundles override active — CDN bypassed, bundles served from disk |

The same `stg`, `prod`, and `→ prod` badges also appear on folder rows in the **Repository** tab for a quick overview without switching tabs.

---

## Testing local changes without deploying

The Local Dev Override system lets you test a content package from local assets — either directly
from the AssetDatabase (no build needed) or from locally-built bundles — without uploading anything
to the CDN. Any package **without** an override continues to load from CDN as normal, so the two
paths can coexist in the same Play Mode session.

### How it works

`ContentRepoRuntime.LoadCatalogsAsync` checks a per-package override registry before making any
CDN catalog request:

- **AssetDatabase mode** — skips `LoadContentCatalogAsync` entirely; Addressables' AssetDatabase
  provider resolves assets directly from the project. No build required.
- **LocalBundles mode** — replaces the CDN catalog URL with a `file://` path pointing to the
  locally-built catalog; Addressables loads bundles from disk.
- **No override** — the CDN URL from the manifest is used as usual.

Overrides survive domain reloads because they are persisted in `EditorPrefs` and restored via
`[InitializeOnLoad]`.

### Fast Mode (AssetDatabase) — no build needed

**Requires:** the content package folder is checked out under the configured local path.

1. In the **Deploy** tab, click `⋮` next to the package row.
2. Choose **Local Dev > Use Asset Database (Fast Mode)**.
3. The window:
   - Creates (or refreshes) an Addressables group for the package with all checked-out assets.
   - Applies the package name as a label to every entry.
   - Switches the Addressables Play Mode Script to **Use Asset Database (fastest)**.
   - Registers the override (shown as an amber `local: AssetDB` badge on the row).
4. Enter Play Mode. Assets are served from AssetDatabase; no CDN requests are made for this package.

To stop using the override, choose **✓ Local Dev: Asset Database active — Clear** from the same menu.

> **Note:** Addressables' Fast Mode Play Mode Script must stay on **Use Asset Database (fastest)**
> (index 0) for this to work. The setup action switches it automatically.

### Local Bundles mode — test with actual built bundles

**Requires:** the content package folder is checked out.

1. In the **Deploy** tab, click `⋮` next to the package row.
2. Choose **Local Dev > Build and Use Local Bundles**.
3. The pipeline:
   - Runs a full `ContentBuildApi.BuildContentPackageAsync` for the package.
   - Rewrites the catalog's load paths from the CDN placeholder to `file://` URLs pointing to
     the build artifact folder (`Builds/Content/builds/<buildId>/<platform>/<pkg>/`).
   - Switches the Addressables Play Mode Script to **Use Existing Build (requires built groups)**.
   - Registers the override (shown as an amber `local: bundles` badge).
4. Enter Play Mode. Bundles are served from disk; no CDN requests are made for this package.

To stop using the override, choose **✓ Local Dev: Local Bundles active — Clear** from the menu.

> **Note:** Play Mode Script must stay on **Use Existing Build** (index 2). The build action
> switches it automatically.

### CDN fallback for non-overridden packages

Only packages with an active override are redirected. All other packages — including ones that
are not checked out locally — continue to fetch their catalog and bundles from the CDN. This
means a developer can work on `Episode02` locally while `Episode01` and `Episode03` still load
from the published CDN content without any extra setup.

### Scripting API

```csharp
using ContentRepo.Editor;

// Register an AssetDatabase override and switch Play Mode Script.
ContentLocalDevApi.SetupForFastMode("Episode02", msg => Debug.Log(msg));

// Build locally, rewrite catalog to file://, switch Play Mode Script.
var result = await ContentLocalDevApi.BuildAndRegisterLocalBundlesAsync("Episode02", msg => Debug.Log(msg));

// Remove overrides.
ContentLocalDevApi.ClearFastMode("Episode02");
ContentLocalDevApi.ClearLocalBundles("Episode02");

// Query the current override registry at runtime.
if (ContentLocalDevOverrides.TryGet("Episode02", out var entry))
    Debug.Log($"Override: {entry.Mode} — {entry.LocalCatalogUrl}");
```

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

The process exits `0` on success and `1` on failure.

---

## Adding a new content package

1. In the **Repository** tab click **+ New folder** and enter the package name (e.g. `Episode02`). This commits a `.gitkeep` to the content repo.
2. Place the package's source assets inside `<content-repo-root>/Episode02/` and add them to Unity.
3. Open **Window > Asset Management > Addressables > Groups**. If Addressables hasn't been initialized yet, choose **Create Addressables Settings**.
4. Create one or more groups whose names start with `Episode02_` (e.g. `Episode02_Scenes`, `Episode02_Models`). The build only picks up groups matching this prefix.
5. Set those groups' **BundledAssetGroupSchema** build and load paths to the **Remote** profile variables (`RemoteBuildPath` / `RemoteLoadPath`). These profile variables are created automatically on the first build if they don't exist yet.
6. Build and upload as usual from the **Deploy** tab.

---

## Loading content at runtime

The package includes a Runtime assembly (`ContentRepo.Runtime`) that fetches the master manifest from the CDN and registers every remote Addressables catalog it references. After initialization, all addresses across every published content package load via `Addressables.LoadAssetAsync<T>` as normal.

### Direct API

```csharp
using ContentRepo;
using UnityEngine.AddressableAssets;

// At app startup
var result = await ContentRepoRuntime.InitializeAsync(
    baseUrl: "https://xxxx.cloudfront.net",
    environment: "production");

if (result.Manifest == null)
{
    // No CDN and no cached manifest — degrade gracefully.
    return;
}

// Regular Addressables loads now pick up assets from every content package.
var prefab = await Addressables.LoadAssetAsync<GameObject>("Episode01/Hero").Task;
```

`InitializeAsync` tries the CDN first. On failure it falls back to the most recently cached manifest (`Application.persistentDataPath/ContentRepo/<env>/manifest.json`), so the app can still boot offline.

### Refreshing while running

Call `ContentRepoRuntime.RefreshAsync(baseUrl, environment)` to re-fetch the manifest. Catalogs whose version is unchanged are skipped; catalogs with a new version are reloaded. Pass `force: true` to `InitializeAsync` to skip the version check entirely.

### Config-driven bootstrap (optional)

If you'd rather not hard-code the CDN URL:

1. **Assets > Create > Content Repo > Runtime Settings**.
2. Move the asset into any `Resources/` folder.
3. Fill in **Base Url** and **Environment**.
4. Tick **Initialize On Load** to run `InitializeAsync` automatically at `BeforeSceneLoad`.

---

## Troubleshooting

- **`Failed to start 'aws'`** — the AWS CLI is not installed or not on PATH. Run `aws --version` in the terminal. See `Setup-AWS.md`.
- **No credentials / `AccessDenied`** — open **Project Settings > Content Repo > Upload > Configure credentials…** and re-enter your Access Key ID and Secret Access Key.
- **`No Addressables groups found with prefix '<name>_'`** — your groups don't follow the naming convention. Rename them or add new groups in the Addressables window.
- **`Addressables is not initialized`** — open **Window > Asset Management > Addressables > Groups** and choose **Create Addressables Settings**, then re-run the build.
- **Local Dev: `'{path}' not found in the Asset Database`** — the package folder is not checked out. Check out the folder in the Repository tab first, then retry.
- **Local Dev: `No assets found under '…'`** — the checked-out folder contains no importable assets yet. Ensure Unity has imported the files (check the Console for import errors).
- **Local Dev: `No catalog JSON found in '…'`** — the local build did not produce a catalog. Verify that **Build Remote Catalog** is enabled in the Addressables settings for this package.
- **Local Dev: assets not updating after re-running Fast Mode setup** — Addressables caches asset GUIDs. Click **Refresh** in the Addressables Groups window or reimport the affected assets, then re-enter Play Mode.
- **Local Dev: badge disappears after Play Mode exit** — overrides survive domain reloads via `EditorPrefs`. If the badge is missing, open `⋮ > Local Dev` and re-register the override.
- **CloudFront `TooManyInvalidationsInProgress`** — CloudFront's free invalidation quota is 1 000 paths/month. Batch your uploads or wait for the previous invalidation to complete.
- **CDN serves stale content after upload** — check that the manifest path (`/<env>/manifest.json`) was invalidated. The Upload action does this automatically; if you pushed files manually you may need to run `aws cloudfront create-invalidation` yourself.
- **Runtime: `Manifest fetch failed … Falling back to cache`** — the client is reading a previous manifest from `persistentDataPath` and booting offline. Resolve the CDN connectivity or certificate issue on the device.
- **Runtime: `Catalog load failed`** — typically a 403 on the bundle host. Re-check the bucket policy and CORS config in `Setup-AWS.md` step 7.
- **Runtime: addresses still resolve to old assets after `RefreshAsync`** — verify the manifest entry's `version` actually changed. If two builds share the same git short-SHA, the version-skip optimization treats the catalog as unchanged. Call `InitializeAsync(..., force: true)` to bypass it.
