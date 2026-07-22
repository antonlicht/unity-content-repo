# Local Dev Overrides

Reference documentation for the per-package local-development override system.

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

## How overrides are applied

### Automatic AssetDatabase (Fast Mode) on Play — the default

`ContentGroupAutoSetup` ([InitializeOnLoad]) runs whenever the editor is **exiting Edit Mode**
(i.e. about to enter Play Mode). For every checked-out content package it:

1. Creates / refreshes the package's Addressables group and populates it from the folder on disk.
2. Labels every entry with the package name.
3. Registers an **AssetDatabase** override for the package (unless it already has a `LocalBundles`
   override, which is preserved).
4. Switches the Addressables **Play Mode Script** to *Use Asset Database (fastest)* (index 0) — or to
   *Use Existing Build* (index 2) if any checked-out package has a `LocalBundles` override.

The practical effect: **checked-out packages just work in Play Mode with no manual step** — their
assets are served straight from the AssetDatabase, even for brand-new packages that have never been
deployed to the CDN.

Overrides for a package whose folder is removed from disk are cleared automatically when the Content
Browser window refreshes.

> There is currently **no** dedicated "Local Dev" menu or on-row badge in the Content Browser window.
> Fast Mode is automatic (above); anything beyond that — notably LocalBundles — is driven through the
> scripting API below.

### Manual control / LocalBundles — scripting API

Use `ContentLocalDevApi` when you want to force a specific mode, build local bundles, or clear an
override explicitly. See [Using from code](#using-from-code).

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

- `AssetDatabase` → skip catalog load; Addressables' AssetDatabase provider takes over
- `LocalBundles` → replace `item.CatalogUrl` with the local `file://` path, then call
  `LoadContentCatalogAsync` as usual
- No entry → use the CDN URL from the manifest unchanged

In addition, `ContentLocalDevOverrides.InjectIntoManifest` appends a synthetic manifest entry for
every override whose package is **not** already in the CDN manifest, so a brand-new local-only
package is still processed at runtime. `LoadCatalogsAsync` also handles overrides directly for
packages absent from the manifest.

### Editor side — `ContentLocalDevApi`

`ContentLocalDevApi` (in `ContentRepo.Editor`) provides the setup methods and the persistence layer:

- **`SetupForFastMode(packageName)`** — creates/refreshes the Addressables group, labels every
  entry with `packageName`, switches the Play Mode Script to index 0, and registers the override.
- **`BuildAndRegisterLocalBundlesAsync(packageName)`** — runs a full build, rewrites the catalog's
  load paths from the CDN placeholder to `file://` paths, switches the Play Mode Script to index 2,
  and registers the override.
- **`ClearFastMode` / `ClearLocalBundles` / `ClearOverride`** — remove the override from the registry
  and save to `EditorPrefs`.
- **`EnsureGroupPopulated`** — the shared helper that creates/syncs a package's Addressables group
  from the folder on disk (also used by the automatic Play-Mode setup).

### Persistence across domain reloads

Overrides are serialized to `EditorPrefs` under the key `ContentRepo.LocalDevOverrides` as JSON.
`ContentLocalDevLoader` (an `[InitializeOnLoad]` class) calls `ContentLocalDevApi.RestoreFromPrefs`
on every domain reload, including the domain reload that happens when entering Play Mode. This
ensures the override registry is populated before `ContentRepoRuntime` runs.

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

> Note: registering directly on `ContentLocalDevOverrides` only updates the in-memory registry for
> the current session. Use the `ContentLocalDevApi` methods (which call `SaveToPrefs`) if you want the
> override to survive domain reloads.

---

## Conditions and constraints

### Fast Mode (AssetDatabase)

- The content package folder must be checked out (present under the configured `LocalPath`).
- The Addressables Play Mode Script must be **Use Asset Database (fastest)** (index 0). The automatic
  Play-Mode setup and `SetupForFastMode` both set this.
- `IncludeInBuild` on the generated group does **not** need to be `true`; Fast Mode ignores it.
- `Addressables.GetDownloadSizeAsync(packageName)` returns `0` — no download step occurs.
- Assets are resolved by their Addressables **address** (path relative to the content folder, as
  set by Addressables on import) and by the **label** equal to `packageName`.

### Local Bundles mode

- The content package folder must be checked out so that `BuildContentPackageAsync` can find assets.
- The Addressables Play Mode Script must be **Use Existing Build (requires built groups)** (index 2).
  `BuildAndRegisterLocalBundlesAsync` sets this automatically.
- The build enables remote-catalog generation automatically (it flips `BuildRemoteCatalog` on for the
  duration of the build), so `catalog_<packageName>.json` is always produced.
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
which ContentBuildApi sets to the CDN placeholder `https://content-repo-cdn-placeholder.example/`.

`ContentBuildApi.RewriteCatalogLoadPaths(localDir, newBaseUrl)` opens every `.json` file in the
artifact folder, replaces all occurrences of the placeholder with `newBaseUrl` (a `file:///…`
URL), and refreshes the companion `.hash` file. The rewritten catalog is what
`BuildAndRegisterLocalBundlesAsync` points the override entry at.

The original CDN catalog (uploaded during a previous `UploadContentPackageAsync`) is not touched.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `'{path}' not found in the Asset Database` | Package not checked out | Check out the folder in the Content Browser |
| `No assets found under '…'` | Folder has no importable assets yet | Wait for Unity import to complete, then retry |
| `No catalog JSON found in '…'` | The Addressables build produced no catalog | Confirm the group built at least one bundle; check the Console for build errors |
| Assets resolve to stale versions after re-setup | Addressables GUID cache | Refresh in the Addressables Groups window or reimport assets |
| Override not restored after domain reload | Was registered directly on `ContentLocalDevOverrides` (no persistence) | Use a `ContentLocalDevApi` method, which persists to `EditorPrefs` |
| Override not respected at runtime | Play Mode Script mismatch | Fast Mode needs index 0; LocalBundles needs index 2 |
