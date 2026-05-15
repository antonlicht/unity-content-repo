# Pipeline Usage

Day-to-day use of the Content Repo build and upload pipeline.

## Prerequisites

- The content repository is initialized (Tools > Content Browser > Initialize).
- At least one content package folder is checked out.
- Addressables is set up in this project, with groups named `<ContentPackageName>_*`.
- Project Settings > Content Repo > Build and Project Settings > Content Repo > Upload are filled in.
- AWS CLI is installed and `aws configure` has been run. See `Setup-AWS.md`.

## Building a single content package

1. Open **Tools > Content Browser**.
2. Switch to the **Build** tab.
3. Pick the environment (Staging or Production).
4. Find the package row and click **Build**.

Artifacts are written to `<BuildOutputRoot>/<environment>/<contentPackageName>/`. The status badge becomes `ok` on success or `error` on failure. The Log pane streams build progress.

## Building all checked-out packages

Click **Build all** in the Build tab. Packages are built sequentially; the loop continues on individual failures and the final status reflects whether any package failed.

## Uploading and publishing the manifest

In the **Upload** tab:

1. Click **Validate credentials** once after configuring AWS — `✓` means you're ready.
2. Click **Upload** on a row to upload a single package (and invalidate its CloudFront path).
3. Click **Upload all** to upload every checked-out package.
4. Click **Publish manifest** to regenerate `<env>/manifest.json` on the CDN and invalidate its path.

The manifest lists every checked-out content package with its catalog URL and current git short-SHA of the content repo.

## Full pipeline in one action

- Per-row **Build + Upload** runs build → upload for that package.
- **Build + Upload all** runs build → upload for every checked-out package, then publishes the manifest.

## CI invocation

The build and upload APIs expose static CLI entry points that can be triggered by `Unity -batchmode -executeMethod`.

### Build a single package

```bash
Unity -batchmode -nographics -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod ContentRepo.Editor.ContentBuildApi.BuildContentPackageCLI \
  -contentPackage Episode01 \
  -environment production
```

### Build all checked-out packages

```bash
Unity -batchmode -nographics -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod ContentRepo.Editor.ContentBuildApi.BuildAllCLI \
  -environment production
```

### Upload a single package

```bash
Unity -batchmode -nographics -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod ContentRepo.Editor.ContentUploadApi.UploadContentPackageCLI \
  -contentPackage Episode01 \
  -environment production
```

### Upload all + publish manifest

```bash
Unity -batchmode -nographics -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod ContentRepo.Editor.ContentUploadApi.UploadAllCLI \
  -environment production
```

The process exits with code `0` on success and `1` on failure. If `-environment` is omitted, the **Default build environment** from `Project Settings > Content Repo > Build` is used.

## Adding a new content package

1. In the **Folders** tab click **+ New folder** and enter the package name (e.g. `Episode02`). This commits a `.gitkeep` to the content repo.
2. Place the package's source assets inside `<content-repo-root>/Episode02/` and add them to Unity.
3. In **Window > Asset Management > Addressables > Groups**, create one or more groups whose names start with `Episode02_` (e.g. `Episode02_Scenes`, `Episode02_Models`). The build only picks up groups matching this prefix convention.
4. Set those groups' `BundledAssetGroupSchema` build and load paths to the **Remote** profile variables (default `RemoteBuildPath` / `RemoteLoadPath`).
5. Build and upload as usual.

## Loading content at runtime

The package includes a small Runtime assembly (`ContentRepo.Runtime`) that fetches the master manifest from the CDN and registers every remote Addressables catalog it points to. Once initialization completes, all addresses across every published content package can be loaded with `Addressables.LoadAssetAsync<T>("address")` as normal.

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

foreach (var c in result.Catalogs)
    Debug.Log($"{c.ContentPackageName} v{c.Version} -> {(c.Success ? "ok" : "FAILED: " + c.Error)}");

// From here, regular Addressables loads pick up assets from every content package.
var prefab = await Addressables.LoadAssetAsync<GameObject>("Episode01/Hero").Task;
```

`InitializeAsync` tries the CDN first. On any network or parse failure it falls back to the most recent locally cached manifest (written to `Application.persistentDataPath/ContentRepo/<env>/manifest.json` on every successful fetch), so the app can still boot offline.

### Refreshing while running

Call `ContentRepoRuntime.RefreshAsync(baseUrl, environment)` to re-fetch the manifest. Catalogs whose `version` is unchanged since the last load are skipped; catalogs with a new version are reloaded via `Addressables.LoadContentCatalogAsync`. To force a full reload pass `force: true` to `InitializeAsync`.

### Config-driven bootstrap (optional)

If you'd rather not hard-code the CDN URL, create a `ContentRepoRuntimeSettings` asset:

1. `Assets > Create > Content Repo > Runtime Settings`.
2. Move the asset into any `Resources/` folder (e.g. `Assets/Resources/ContentRepoRuntimeSettings.asset`).
3. Fill in **Base Url** and **Environment**.
4. Tick **Initialize On Load** if you want `InitializeAsync` to run automatically `BeforeSceneLoad` — useful for thin clients where there's no other startup code.

Otherwise call `ContentRepoRuntimeSettings.Load()` yourself and pass the values into `InitializeAsync`.

### Subscribing to init completion

```csharp
ContentRepoRuntime.OnInitialized += r =>
{
    foreach (var c in r.Catalogs)
        if (!c.Success) Debug.LogError($"Catalog {c.ContentPackageName} failed: {c.Error}");
};
```

## Troubleshooting

- **`Failed to start 'aws'`** — the AWS CLI is not installed or not on PATH. Run `aws --version` from the same shell that launched Unity. See `Setup-AWS.md` step 4.
- **`Credentials valid and bucket reachable` returns `✗`** — most often the IAM user is missing `s3:ListBucket` on the bucket ARN. Check `Setup-AWS.md` step 3.
- **`No Addressables groups found with prefix '<name>_'`** — your groups don't follow the naming convention. Rename them or add new groups in the Addressables window.
- **`Addressables profile '<name>' was not found`** — set the **Addressables profile name** in `Project Settings > Content Repo > Build` to match an existing profile.
- **CloudFront `TooManyInvalidationsInProgress`** — CloudFront's free invalidation quota is 1 000 paths/month and only a few invalidations may be in-flight at once. Batch your uploads or wait for the previous invalidation to complete; the manifest path is the only invalidation you strictly need for clients to pick up new content.
- **CDN serves stale content even after invalidation** — invalidations apply per-path; check that you invalidated the manifest path (`/<env>/manifest.json`), since that is what clients re-read to discover new catalog URLs.
- **Runtime: `Manifest fetch failed … Falling back to cache`** — the client successfully read a previous manifest from `persistentDataPath/ContentRepo/<env>/manifest.json` and is booting offline. Resolve the CDN error if you want the latest content; users on flaky networks will keep playing on the cached catalog.
- **Runtime: `Catalog load failed`** — typically a 403 on the bundle host (missing CORS rule on S3, or the bucket policy doesn't allow the CloudFront distribution to read). Re-check `Setup-AWS.md` step 2.
- **Runtime: addresses still resolve to old assets after `RefreshAsync`** — verify the manifest entry's `version` actually changed. If two builds produced the same git short-SHA, the version-skip optimization treats the catalog as unchanged. Either commit a content change or call `InitializeAsync(..., force: true)`.
