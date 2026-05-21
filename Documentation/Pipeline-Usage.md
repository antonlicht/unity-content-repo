# Pipeline Usage

Day-to-day use of the Content Repo build and upload pipeline.

## Prerequisites

- The content repository is initialized (**Tools > Content Browser > Initialize**).
- At least one content package folder is checked out.
- Addressables groups named `<ContentPackageName>_*` exist for each package (see [Adding a new content package](#adding-a-new-content-package)).
- **Project Settings > Content Repo > Upload** has S3 bucket, region, CloudFront distribution ID, and domain filled in.
- AWS credentials are configured — use **Configure credentials…** in Project Settings > Content Repo > Upload, or see `Setup-AWS.md`.

---

## Building a single content package

1. Open **Tools > Content Browser** → **Deploy** tab.
2. Find the package row and click **Build**.

Artifacts land in `Builds/Content/builds/<buildId>/<platform>/<contentPackageName>/`. The row badge shows `ok` or `error`; the log pane streams progress.

## Building all checked-out packages

Click **Build all** in the Deploy tab toolbar. Packages build sequentially; failures are logged but don't abort the rest.

---

## Uploading to staging

Click **Upload** on a package row. Bundles are pushed to S3 and the staging manifest entry is updated.

Click **Upload all** to do this for every checked-out package.

> Upload always targets **staging**. Production is only updated via promotion.

## Promoting staging → production

- Click **Promote → prod** on a row (appears only when staging is ahead of production).
- Click **Promote all → prod** in the toolbar to promote every checked-out package at once.

## Full pipeline in one action

Click **Build + Upload all** to build and upload all checked-out packages to staging in one step.

Per-row **Build+Upload** does the same for a single package.

---

## Status badges

Each row in the Deploy tab shows small badges:

| Badge | Meaning |
|---|---|
| `staging: a1b2c3d4` | Build ID live on staging |
| `staging: —` | Not yet published to staging |
| `→ production ready` | Staging is ahead of production — ready to promote |
| `production: a1b2c3d4` | Build ID live on production |
| `production: —` | Not yet on production |
| `local: AssetDB` (amber) | AssetDatabase override active |
| `local: bundles` (amber) | Local-bundles override active |

The `staging`, `production`, and `→ production ready` badges also appear in the **Repository** tab.

---

## Testing local changes without deploying

Two modes let you test a checked-out package without uploading anything. Packages without an override keep loading from CDN.

### Fast Mode — no build needed

Use this when you want to iterate quickly. Assets are served straight from the Unity AssetDatabase.

**Requires:** package folder is checked out.

1. Click `⋮` on the package row → **Local Dev > Use Asset Database (Fast Mode)**.
2. Enter Play Mode.

The row shows an amber `local: AssetDB` badge. To stop, click `⋮` → **✓ Local Dev: Asset Database active — Clear**.

> The Addressables Play Mode Script is switched to **Use Asset Database (fastest)** automatically.

### Local Bundles mode — test with built bundles

Use this when you need to test the exact bundle output, e.g. to catch packing or loading issues.

**Requires:** package folder is checked out.

1. Click `⋮` on the package row → **Local Dev > Build and Use Local Bundles**.
2. Wait for the build to finish (progress streams in the log pane).
3. Enter Play Mode.

The row shows an amber `local: bundles` badge. Bundles are read from `Builds/Content/builds/<buildId>/<platform>/<pkg>/`. To stop, click `⋮` → **✓ Local Dev: Local Bundles active — Clear**.

> The Addressables Play Mode Script is switched to **Use Existing Build** automatically.

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

The process exits `0` on success and `1` on failure.

---

## Adding a new content package

1. In the **Repository** tab click **+ New folder** and enter the package name (e.g. `Episode02`).
2. Place the package's assets inside `<content-repo-root>/Episode02/` and let Unity import them.
3. Open **Window > Asset Management > Addressables > Groups**. If Addressables isn't initialized, choose **Create Addressables Settings**.
4. Create one or more groups whose names start with `Episode02_` (e.g. `Episode02_Scenes`). The build picks up all groups matching this prefix.
5. Set those groups' **BundledAssetGroupSchema** build and load paths to the **Remote** profile variables (`RemoteBuildPath` / `RemoteLoadPath`).
6. Build and upload from the **Deploy** tab as usual.

> The first build creates `_groups/Episode02.asset` inside the content repo. Commit and push this file — it stores the group's stable GUID and custom addresses, and is restored automatically when any developer checks out the package.

### Testing a new package before the first deploy

Before the package is uploaded it has no CDN manifest entry, so the runtime skips it. Use the Local Dev override to test it immediately:

1. Check out the folder in the **Repository** tab (or create it locally).
2. In the **Deploy** tab open `⋮ > Local Dev > Use Locally (Fast Mode / AssetDatabase)`. This creates the Addressables group if it doesn't exist yet, labels every asset with the package name, and registers an AssetDatabase override.
3. Switch the Addressables Play Mode Script to **Use Asset Database (fastest)**.
4. Enter Play Mode. The runtime injects the package into catalog loading even though it has no CDN manifest entry — assets are served directly from AssetDatabase.
5. When ready, build and upload normally, then remove the override via `⋮ > Local Dev > Clear`.

---

## Loading content at runtime

```csharp
using ContentRepo;
using UnityEngine.AddressableAssets;

// At app startup — fetches the CDN manifest and registers all remote catalogs.
var result = await ContentRepoRuntime.InitializeAsync(
    baseUrl: "https://xxxx.cloudfront.net",
    environment: "production");

if (result.Manifest == null)
{
    // No CDN and no cached manifest — degrade gracefully.
    return;
}

// All content-package addresses are now available.
var prefab = await Addressables.LoadAssetAsync<GameObject>("Episode01/Hero").Task;
```

Falls back to the last cached manifest (`persistentDataPath/ContentRepo/<env>/manifest.json`) when the CDN is unreachable.

### Refreshing while running

Call `ContentRepoRuntime.RefreshAsync(baseUrl, environment)` to re-fetch the manifest. Catalogs whose version is unchanged are skipped. Pass `force: true` to `InitializeAsync` to bypass the version check.

### Config-driven bootstrap (optional)

1. **Assets > Create > Content Repo > Runtime Settings**.
2. Move the asset into any `Resources/` folder.
3. Fill in **Base Url** and **Environment**.
4. Tick **Initialize On Load** to run `InitializeAsync` automatically at `BeforeSceneLoad`.

---

## Troubleshooting

- **`Failed to start 'aws'`** — AWS CLI is not installed or not on PATH. Run `aws --version`. See `Setup-AWS.md`.
- **No credentials / `AccessDenied`** — open **Project Settings > Content Repo > Upload > Configure credentials…** and re-enter your keys.
- **`No Addressables groups found with prefix '<name>_'`** — rename your groups or create new ones following the `<name>_*` convention.
- **`Addressables is not initialized`** — open **Window > Asset Management > Addressables > Groups**, choose **Create Addressables Settings**, then re-run the build.
- **Local Dev: `'{path}' not found in the Asset Database`** — check out the folder in the Repository tab first.
- **Local Dev: `No assets found under '…'`** — Unity hasn't imported the files yet. Check the Console for import errors.
- **Local Dev: `No catalog JSON found in '…'`** — enable **Build Remote Catalog** in the Addressables settings for this package.
- **Local Dev: assets not updating after re-running Fast Mode** — click **Refresh** in the Addressables Groups window, then re-enter Play Mode.
- **Local Dev: badge missing after domain reload** — open `⋮ > Local Dev` and re-register the override.
- **New package not loading in Play Mode** — the package isn't in the CDN manifest yet. Register it via `⋮ > Local Dev > Use Locally (Fast Mode / AssetDatabase)`; the runtime injects it automatically until you deploy and clear the override.
- **Addressables group is empty after checkout** — the group asset was restored from `_groups/` automatically when you checked out the package. If it still appears empty, the group file may not have been committed to the content repo yet; run the first build and push `_groups/<packageName>.asset`.
- **CloudFront `TooManyInvalidationsInProgress`** — free quota is 1 000 paths/month. Batch uploads or wait for the previous invalidation to complete.
- **CDN serves stale content after upload** — the Upload action invalidates `/<env>/manifest.json` automatically. If you pushed manually, run `aws cloudfront create-invalidation` yourself.
- **Runtime: `Manifest fetch failed … Falling back to cache`** — the app booted offline from a cached manifest. Fix CDN connectivity or certificate issues on the device.
- **Runtime: `Catalog load failed`** — typically a 403 on the bundle host. Re-check bucket policy and CORS config in `Setup-AWS.md` step 7.
- **Runtime: addresses resolve to old assets after `RefreshAsync`** — verify the manifest entry's `version` changed. Call `InitializeAsync(..., force: true)` to bypass the version-skip check.
