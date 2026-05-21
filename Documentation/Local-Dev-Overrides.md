# Local Dev Overrides

Reference documentation for the per-package local-development override system introduced in
Content Repo 0.3.3.

## Overview

By default, `ContentRepoRuntime.InitializeAsync` fetches a manifest from the CDN and loads every
content package's catalog from a CDN URL. The Local Dev Override system intercepts that catalog
lookup **per package** and redirects it to local assets instead. Packages without an override are
not affected and keep loading from CDN, so a developer can iterate on one package locally while
the rest of the game still uses published content.

Two override modes are available:

| Mode | What it does | Build needed? | Addressables Play Mode Script |
|---|---|---|---|
| **AssetDatabase** | Skips catalog loading; Addressables serves assets from the Unity AssetDatabase | No | Use Asset Database (fastest) |
| **LocalBundles** | Replaces the CDN catalog URL with a `file://` path to a locally-built catalog | Yes (automated) | Use Existing Build |

---

## Architecture

### Runtime side — `ContentLocalDevOverrides`

`ContentLocalDevOverrides` (in `ContentRepo.Runtime`) is a static in-memory dictionary keyed by
content package name. Each entry holds a `LocalDevMode` and, for `LocalBundles` mode, the
`file://` catalog URL.

```
ContentLocalDevOverrides.All
  ├─ "Episode02"  →  { Mode: AssetDatabase,  LocalCatalogUrl: null }
  └─ "Episode04"  →  { Mode: LocalBundles,   LocalCatalogUrl: "file:///C:/…/catalog_Episode04.json" }
```

`ContentRepoRuntime.LoadCatalogsAsync` queries this dictionary for each manifest entry before
calling `Addressables.LoadContentCatalogAsync`:

- `AssetDatabase` → `continue` (skip catalog load; Addressables' AssetDatabase provider takes over)
- `LocalBundles` → replace `item.CatalogUrl` with the local `file://` path, then call
  `LoadContentCatalogAsync` as usual
- No entry → use the CDN URL from the manifest unchanged

### Editor side — `ContentLocalDevApi`

`ContentLocalDevApi` (in `ContentRepo.Editor`) provides the two setup methods and the persistence
layer:

- **`SetupForFastMode(packageName)`** — creates/refreshes the Addressables group, labels every
  entry with `packageName`, switches the Play Mode Script to index 0, and registers the override.
- **`BuildAndRegisterLocalBundlesAsync(packageName)`** — runs a full build, rewrites the catalog's
  load paths from the CDN placeholder to `file://` paths, switches the Play Mode Script to index 2,
  and registers the override.
- **`ClearFastMode` / `ClearLocalBundles`** — remove the override from the registry and save to
  `EditorPrefs`.

### Persistence across domain reloads

Overrides are serialized to `EditorPrefs` under the key `ContentRepo.LocalDevOverrides` as JSON.
`ContentLocalDevLoader` (an `[InitializeOnLoad]` class) calls `ContentLocalDevApi.RestoreFromPrefs`
on every domain reload, including the domain reload that happens when entering Play Mode. This
ensures the override registry is populated before `ContentRepoRuntime` runs.

---

## Using from the UI

1. Open **Tools > Content Browser** → **Deploy** tab.
2. Click the `⋮` button on the package row you want to test locally.
3. Under the separator, choose from:

| Menu item | Effect |
|---|---|
| **Local Dev / Use Asset Database (Fast Mode)** | Runs `SetupForFastMode`; shows amber `local: AssetDB` badge |
| **✓ Local Dev: Asset Database active — Clear** | Runs `ClearFastMode`; badge disappears |
| **Local Dev / Build and Use Local Bundles** | Runs `BuildAndRegisterLocalBundlesAsync`; shows amber `local: bundles` badge |
| **✓ Local Dev: Local Bundles active — Clear** | Runs `ClearLocalBundles`; badge disappears |

The amber badge on the row is a persistent visual reminder that an override is active. It
disappears as soon as the override is cleared.

---

## Using from code

```csharp
using ContentRepo.Editor;
using ContentRepo;

// ── Editor-only setup ────────────────────────────────────────────────────────

// Fast Mode: populate group, label assets, switch Play Mode Script.
ContentLocalDevApi.SetupForFastMode("Episode02", msg => Debug.Log(msg));

// Local Bundles: build, rewrite catalog, switch Play Mode Script.
var buildResult = await ContentLocalDevApi.BuildAndRegisterLocalBundlesAsync(
    "Episode02", msg => Debug.Log(msg));

// Clear overrides.
ContentLocalDevApi.ClearFastMode("Episode02");
ContentLocalDevApi.ClearLocalBundles("Episode02");

// ── Runtime query (Editor + Player) ─────────────────────────────────────────

// Check what override is registered for a package.
if (ContentLocalDevOverrides.TryGet("Episode02", out var entry))
{
    Debug.Log($"Mode: {entry.Mode}");          // AssetDatabase or LocalBundles
    Debug.Log($"Catalog: {entry.LocalCatalogUrl}");  // null for AssetDatabase
}

// Enumerate all active overrides.
foreach (var kv in ContentLocalDevOverrides.All)
    Debug.Log($"{kv.Key}: {kv.Value.Mode}");

// Register programmatically (e.g. from a custom build script).
ContentLocalDevOverrides.Register("Episode02", LocalDevMode.AssetDatabase);
ContentLocalDevOverrides.Register("Episode04", LocalDevMode.LocalBundles,
    "file:///C:/Projects/MyGame/Builds/Content/builds/abc123/StandaloneWindows64/Episode04/catalog_Episode04.json");

// Remove one or all overrides.
ContentLocalDevOverrides.Unregister("Episode02");
ContentLocalDevOverrides.Clear();
```

---

## Conditions and constraints

### Fast Mode (AssetDatabase)

- The content package folder must be checked out (present under the configured `LocalPath`).
- The Addressables Play Mode Script must be **Use Asset Database (fastest)** (index 0). `SetupForFastMode` sets this automatically.
- `IncludeInBuild` on the generated group does **not** need to be `true`; Fast Mode ignores it.
- `Addressables.GetDownloadSizeAsync(packageName)` returns `0` — no download step occurs.
- Assets are resolved by their Addressables **address** (path relative to the content folder, as
  set by Addressables on import) and by the **label** equal to `packageName`.

### Local Bundles mode

- The content package folder must be checked out so that `BuildContentPackageAsync` can find assets.
- The Addressables Play Mode Script must be **Use Existing Build (requires built groups)** (index 2). `BuildAndRegisterLocalBundlesAsync` sets this automatically.
- The Addressables group for the package must have **Build Remote Catalog** enabled (the build
  writes a `catalog_<packageName>.json`; without it no catalog file exists to redirect to).
- Bundles are read via the `file://` provider; `GetDownloadSizeAsync` returns `0`.
- The catalog file path is absolute and machine-specific; it is not suitable for sharing via
  source control.

### Hybrid CDN + local

Both modes are compatible with CDN loading for other packages in the same session. You can:

- Test `Episode02` from AssetDatabase while `Episode01`, `Episode03` load from CDN.
- Test `Episode02` with local bundles while `Episode04` loads from CDN.
- Have **no** local overrides at all — full CDN loading for everything.

---

## How the catalog rewrite works (LocalBundles)

When Addressables builds content it writes load paths using the `RemoteLoadPath` profile variable,
which ContentBuildApi sets to the CDN placeholder
`https://content-repo-cdn-placeholder.example/{UnityEditor.EditorUserBuildSettings.activeBuildTarget}`.

`ContentBuildApi.RewriteCatalogLoadPaths(localDir, newBaseUrl)` opens every `.json` file in the
artifact folder and replaces all occurrences of the placeholder with `newBaseUrl` (a `file:///…`
URL). The rewritten catalog is what `BuildAndRegisterLocalBundlesAsync` points the override entry at.

The original CDN catalog (uploaded during a previous `UploadContentPackageAsync`) is not touched.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `'{path}' not found in the Asset Database` | Package not checked out | Check out the folder in the Repository tab |
| `No assets found under '…'` | Folder has no importable assets yet | Wait for Unity import to complete, then retry |
| `No catalog JSON found in '…'` | Build Remote Catalog not enabled | Enable it in Addressables Settings for the package group |
| Assets resolve to stale versions after re-setup | Addressables GUID cache | Refresh in Addressables Groups window or reimport assets |
| Amber badge disappears after domain reload | `EditorPrefs` key was cleared | Re-register via `⋮ > Local Dev` |
| Override not respected at runtime | Play Mode Script mismatch | Fast Mode needs index 0; LocalBundles needs index 2 |
