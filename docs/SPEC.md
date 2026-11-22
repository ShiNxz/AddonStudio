# Addon Studio - spec (v1)

Working name. A Windows desktop app that manages CS2 Steam Workshop addons as git repos: edit sources, commit, see history, build only what changed, see exactly what goes into the VPK, and publish to the Workshop with one button (or automatically on a version commit).

This file is the contract every part of the codebase follows. If something here is wrong or missing, say so in your report instead of silently diverging.

## 1. Scope

- v1 runs on Windows only for compile and publish. Valve's compiler (`resourcecompiler.exe`) is Windows-only and we do not use Wine.
- The core still builds and its tests run on macOS/Linux, so development and CI work off Windows. Anything that needs CS2, the compiler or the Steam client fails there with a clear error code (`not_windows`, `cs2_missing`, `tools_missing`, `steam_unavailable`).
- No GitHub Actions for addons. Build and publish run in the app on the user's PC. (The app itself has a CI workflow that builds its single Windows exe, see section 9.)
- No headless agent, no Mac compile, no map compiling (maps and other things we can not rebuild go in `prebuilt/`).

## 2. The pipeline

```
content/ (sources, in git)
   | resourcecompiler.exe, only changed roots
   v
<CS2>/game/csgo_addons/<addon>/   = build dir (Valve's location, regenerated, never in git)
   ^ prebuilt/ copied in as-is (compiled files we have no source for, e.g. models taken from other items)
   | pack: same rules and format as Valve's workshop manager (VPK v2, 100 MB chunks, 3 GiB cap)
   v
<id>_dir.vpk + _000.vpk... + publish_data.txt
   | upload through the running Steam client (ISteamUGC)
   v
Steam Workshop item -> Steam processes it -> live
```

## 3. An addon project (a git repo)

```
<repo>/
  workshop.json      project settings (below)
  content/           sources. Linked into <CS2>/content/csgo_addons/<addon> with a directory junction
  prebuilt/          already-compiled files copied into the build dir as-is (models, maps, anything without source)
  preview.png        optional workshop thumbnail (path set in workshop.json)
  .gitattributes     LFS rules (template below)
  .gitignore         ignores .addonstudio/
  .addonstudio/      local state, never committed: build-state.json, last-publish.json, hash-cache.json, logs/
```

### workshop.json

```jsonc
{
  "addon": "next_hud",              // folder name under csgo_addons. ^[a-z0-9_]{1,64}$
  "workshopId": null,               // string, null until the first publish (then written back)
  "title": "Next HUD",
  "description": null,              // null = leave Steam's description as it is
  "visibility": "private",          // "public" | "friendsOnly" | "private" | "unlisted"
  "tags": ["Custom"],
  "preview": "preview.png",         // repo-relative, optional
  "pack": { "include": [], "exclude": ["**/*.psd"] },   // globs relative to the build dir; user rules go before Valve's gameinfo rules
  "autoBuild": true,                // rebuild changed sources when files change
  "autoPublish": {
    "enabled": false,
    "pattern": "^v(\\d+\\.\\d+\\.\\d+)\\b",   // matched against the commit subject, group 1 = version
    "githubRelease": false          // also create a GitHub release with the VPK chunks (needs gh)
  }
}
```

Unknown keys are kept on save. Validation errors are returned as `configErrors`, the project still opens.

### .gitattributes template

```
*.png filter=lfs diff=lfs merge=lfs -text
*.jpg filter=lfs diff=lfs merge=lfs -text
*.jpeg filter=lfs diff=lfs merge=lfs -text
*.tga filter=lfs diff=lfs merge=lfs -text
*.psd filter=lfs diff=lfs merge=lfs -text
*.exr filter=lfs diff=lfs merge=lfs -text
*.wav filter=lfs diff=lfs merge=lfs -text
*.mp3 filter=lfs diff=lfs merge=lfs -text
*.fbx filter=lfs diff=lfs merge=lfs -text
*.dmx filter=lfs diff=lfs merge=lfs -text
*.obj filter=lfs diff=lfs merge=lfs -text
*.vpk filter=lfs diff=lfs merge=lfs -text
prebuilt/** filter=lfs diff=lfs merge=lfs -text
```

## 4. CS2 paths, linking and the compiler

- CS2 install: `WorkshopManager.FromSteamInstall().GamePath` from the `CS2WorkshopManager` NuGet package (it reads Steam's library folders), unless `settings.cs2PathOverride` is set.
- Compiler: `<cs2>/game/bin/win64/resourcecompiler.exe`. Missing = Workshop Tools not installed (doctor check `workshopTools`, fix: "Steam > CS2 > Properties > DLC > Counter-Strike 2 Workshop Tools").
- `gameinfo.gi`: `<cs2>/game/csgo/gameinfo.gi` (packing rules).
- Content link: `<cs2>/content/csgo_addons/<addon>` must be a directory junction to `<repo>/content`. The compiler works out the addon from the file's location, so it is always given the path through the junction.
  - Create with `cmd /c mklink /J "<link>" "<target>"` (junctions need no admin, unlike symlinks). Read with `DirectoryInfo.LinkTarget`.
  - If a real folder is already there, the link state is `conflict`. We never delete it. Import (section 6.4) or `projects.link { backup: true }` renames it to `<addon>.addonstudio-backup-<yyyyMMdd-HHmmss>` first.
- Build dir: `<cs2>/game/csgo_addons/<addon>`. The compiler writes there. Valve's own files there (`tools_asset_info.bin`, `readonly_tools_asset_info.bin`, `_bakeresourcecache/`, `_vrad3/`) are never touched.
- Compile command: `settings.compilerArgs`, default `-nop4 -i "{input}"`, run from `<cs2>/game/bin/win64` with `CreateNoWindow`, per root file, `settings.compileParallelism` processes at once (default max(1, cores/2)), 5 minute timeout per file, process tree killed on cancel.
- Success = exit code 0 and every expected output exists with a write time after the job started (or, when the compiler's summary says it skipped them as up to date, they exist at all). Each root's compiler output also goes to `.addonstudio/logs/<source with / as _>.log`. Error lines: lines containing `ERROR` or `FAILED` (upper case, so the "1 compiled, 0 failed" summary is not an error), or `error:` / `failed to compile` (any case) go in `errors`. `WARNING` (upper case) or `warning:` (any case) lines make the status `warning`.
- Compiler fingerprint = size + mtime of resourcecompiler.exe + compilerArgs. When it changes every root counts as changed.

### Roots, dependencies, outputs

Roots (content-relative, case-insensitive):

| Source | Kind | Expected output (build-relative) |
| --- | --- | --- |
| `panorama/**.xml` | panorama | same path `.vxml_c` |
| `panorama/**.css` | panorama | `.vcss_c` |
| `panorama/**.js` | panorama | `.vjs_c` |
| `**.svg` | panorama | `.vsvg_c` |
| `panorama/images/**.png/.jpg/.jpeg/.tga/.psd` | texture | `<name>_<ext>.vtex_c` (e.g. `coin_png.vtex_c`) |
| `**.vmdl` | model | `.vmdl_c` |
| `**.vmat` | material | `.vmat_c` |
| `**.vtex` | texture | `.vtex_c` |
| `**.vpcf` | particle | `.vpcf_c` |
| `**.vsndevts` | soundevents | `.vsndevts_c` |
| `sounds/**.wav/.mp3` | sound | `.vsnd_c` |
| `**.vdata` | vdata | `.vdata_c` |
| `**.vmap` | unsupported ("maps need Hammer, put the compiled map in prebuilt/") | none |

Everything else in `content/` is a dependency file (textures under materials/, fbx/dmx, etc.) or ignored.

Dependencies: scan text roots for references and keep the ones that resolve to an existing file in `content/`:
- Panorama: `file://{images}/x` -> `panorama/images/x`, `file://{resources}/x` -> `panorama/x`, `s2r://panorama/...` (strip `_png.vtex` style suffix back to the source image), `<include src="...">`, CSS `url(...)` and `@import`.
- KV3/keyvalues text (vmat, vmdl, vtex, vpcf, vsndevts, vdata): quoted strings ending in a known source or resource extension (`.png .tga .jpg .psd .exr .fbx .dmx .obj .vmat .vtex .wav .mp3 .vsnd .vpcf .vmdl`). `.vsnd` maps back to `sounds/...wav|mp3`.
- A root's hash = SHA-256 over its own bytes plus every dependency's bytes (sorted). A changed dependency makes every root that uses it `changed`.

### Build state

`.addonstudio/build-state.json`:

```jsonc
{
  "version": 1,
  "compilerFingerprint": "…",
  "roots": { "panorama/layout/custom_game/x.xml": { "hash": "…", "deps": ["…"], "outputs": ["…"], "result": "ok", "builtAt": "ISO" } },
  "prebuilt": { "models/x.vmdl_c": { "size": 123, "mtime": "ISO" } }
}
```

- Deleted root: its recorded outputs are removed from the build dir.
- `prebuilt/` is mirrored into the build dir (copy when size or mtime differ, remove what was deleted from prebuilt/).
- A path that is both a compiled output and in prebuilt/: compiled wins, reported in `conflicts`.
- Files in the build dir that no root, prebuilt entry or Valve file explains are `orphans`. Plans list them, `build.clean` (confirmed in the UI) deletes the whole build dir except Valve's files and then a full build follows.

## 5. Packing and publishing

- The pack size cap is a user setting: `AppSettings.maxPackBytes`, default 3 GiB (3221225472, Valve's own tool limit). Above 3 GiB the UI shows a warning ("Valve's tool caps uploads at 3 GB, bigger uploads are untested"). Single files stay under 2 GiB (VPK stores sizes as int32), that is not configurable.
- `CS2WorkshopManager` (MIT, NuGet 1.5.0, net10.0) hard-codes the 3 GiB cap inside `AddonPackager.Pack` and `WorkshopManager.PublishAsync`, so we use only its file selection and checks: `AddonPackager.CollectFiles/GetContents` (Valve's gameinfo rules and skipped files), `WorkshopManager.GetConflictingSourceFolder`, `WorkshopManager.GetPublishedItemsAsync`, `GameFolderLocator` via `FromSteamInstall`. It reads files from `<cs2>/game/csgo_addons/<addon>`, which is our build dir. Map `workshop.json` pack globs to `AddonRules` (`Rule(Exclude, Pattern)`). AddonRules match path prefixes, not globs, so `IPackService.BuildRules` expands each glob against the build dir's files into one rule per match (excludes first); a pattern without wildcards stays a prefix.
- Our own packer (`Pack/`) mirrors `AddonPackager.Pack` and `Stage` exactly (ValvePak `Package`, `WriteChunkSize` 100 MiB, `AddFile(..., multiChunk: true)`, `vpks/<id>/<id>_dir.vpk` plus chunks plus `publish_data.txt` in the same format) but checks `maxPackBytes` instead of the constant.
- Our own uploader (`Publish/`) mirrors `WorkshopManager.PublishAsync` (create item when new, `StartItemUpdate`, title, description, visibility, preview, `SetItemContent(<our staging folder>)`, tags, `SubmitItemUpdate(changeNote)`, progress from `GetItemUpdateProgress`, `UserNeedsToAcceptWorkshopLegalAgreement`) using `Steamworks.SteamClient(730)` and its `UGC` from the `Steamworks.dll` that ships inside the CS2WorkshopManager package. Read the reference source for the exact calls and the preview/thumbnail handling.
- `last-publish.json`: `{ at, workshopId, commit, files: { "<path>": { size, sha256 } } }`. Pack contents diff against it. SHA-256s are cached in `hash-cache.json` keyed by path + size + mtime.
- Publish flow (`publish.run`):
  1. Unless `skipBuild`, run a `changed` build. Any error stops the publish.
  2. Upload with `PublishAsync`: `AddonName = addon`, `PublishedFileId = workshopId` (null creates an item, the new id is written back to workshop.json), title, description (only if not null), visibility, tags, preview, rules, `ChangeNote = note + "\n\n[" + shortHash + "]"` (plus " (uncommitted changes)" when the tree is dirty). `SourceFolderConflictException` -> error `conflict`, retry with `allowSourceFolderChange: true` after the user confirms.
  3. Write last-publish.json.
  4. Tag HEAD (if the repo has commits): annotated `deploy/<yyyyMMdd-HHmmss>` (UTC), message `Published to Steam Workshop item <id>\n\nfiles: N\nsize: B\nnote: <first line>`. If `version` is given, also annotated `v<version>` (if it exists already: warning, skip).
  5. Push the tags when `push` is true and a remote exists. Failure is a warning, not an error.
  6. If `githubRelease`: `pack.export` to a temp dir, then `gh release create v<version> <files> --title v<version> --notes <note>`. Missing or unauthenticated gh = warning.
- Steam status: owner items from `GetPublishedItemsAsync` (works for private items) plus the public `ISteamRemoteStorage/GetPublishedFileDetails/v1` POST (`itemcount=1&publishedfileids[0]=<id>`, no key) for `banned`, `ban_reason`, `time_updated`, `file_size`. `live` = Steam's time_updated >= our last publish time minus 5 minutes (Steam stamps the update a little before our upload call returns), `processing` = older, `unknown` = no data. No last publish and a found item = `live`. `steam.items` runs the same owner query as `GetPublishedItemsAsync` on our own `SteamClient(730)`, but without its required "CS2" and "Map" tags, so items with other tags show up too. One Steam call at a time (one pipe, every wait drains all callbacks). Keep the raw JSON in `raw` so the UI can show fields we do not map yet (the "Valve check" status is not mapped yet, the owner will show us what it looks like).

## 6. Core services (C#, `AddonStudio.Core`)

All services are interfaces in `Contracts/` with one implementation class each. The host wires them with plain constructors (no DI container needed).

### 6.1 IEnvironmentService
`Doctor()`: checks `os`, `steam` (Steam installed), `cs2`, `workshopTools`, `steamRunning`, `git` (warn when `user.name`/`user.email` are not set, since commits fail), `gitLfs`, `gh` (only the active github.com account: `gh auth status --active --hostname github.com`). `Cs2Path()`, `CompilerPath()`. Each check has a fix text.

### 6.2 ISettingsStore
`%APPDATA%/AddonStudio/settings.json` (macOS/Linux: `~/.config/AddonStudio/settings.json`), override with env `ADDONSTUDIO_HOME`. The app sets that to `<exe dir>/data` only when a `portable.txt` sits next to `AddonStudio.exe`. Holds AppSettings and the recent projects list (max 50).

### 6.3 IProjectService
`List`, `Open`, `Create` (layout + workshop.json + .gitattributes + .gitignore + `git init` + `git lfs install --local` when available, then a first commit "Create <addon>"), `Clone` (via IGitService, then open), `SaveConfig`, `Link`, `Remove` (from the recent list only, never deletes files), `ListCs2Addons` (addons found in CS2's content and game folders).

### 6.4 IImportService
Turns an existing CS2 addon into a project.
- `Plan(addon, targetPath, decompile)`: content files are copied as sources. Every file in the build dir is classified:
  - `skip` when a source we have produces it (expected-output table) or it is a Valve file.
  - `decompile` (only when `decompile` is true) for types that come back cleanly with ValveResourceFormat: panorama `vxml_c/vcss_c/vjs_c` -> xml/css/js, `vsvg_c` -> svg, panorama images `*_png.vtex_c` -> png, `vsnd_c` -> wav/mp3, `vsndevts_c` -> vsndevts. Only when the source file does not already exist.
  - `prebuilt` for everything else (models, materials, particles, maps...), copied to `prebuilt/<same path>`.
- `Run(...)`: copies (never moves) into the repo, creates the project if needed, renames the old content folder to the backup name and links. A project the import created gets one first commit "Import <addon> from CS2" with everything in it; importing into an existing project leaves the changes uncommitted for review. A commit failure (for example no git name/email) is a warning, not an error. The old build dir is left alone. A failed decompile falls back to `prebuilt` with a warning.

### 6.5 IBuildService
`Plan(path)`, `Run(path, mode, files, progress, ct)`, `Clean(path)`. Modes: `changed` (new/changed/failed roots + prebuilt mirror + deleted-root cleanup), `full` (every root + prebuilt mirror + deleted-root cleanup), `files` (given roots only). One build per project at a time (`busy` error). Event indexes are 0-based.

### 6.6 IPackService
`Contents(path)`, `Export(path, outDir?)` (uses `AddonPackager.Stage`, default out dir `.addonstudio/export/<timestamp>`).

### 6.7 IPublishService / ISteamService
As in section 5. `ISteamService.Items()`, `Status(workshopId, lastPublishAt?)`.

### 6.8 IGitService
Shells out to `git` (and `git lfs`, `gh`). Always `-c core.quotepath=off`, `--no-optional-locks` for read commands, `CreateNoWindow`, UTF-8. Status uses `--porcelain=v2 -z --branch`. Methods: `Status`, `Stage`, `Unstage`, `Discard` (UI confirms; untracked files are deleted with `git clean -f -- <files>`), `Commit`, `Push` (with `--follow-tags`), `Pull` (`--ff-only`), `Fetch`, `Log(limit, skip)`, `Show(hash)` (files with `--numstat`, binary when `-`), `Init`, `Clone` (progress from `--progress` stderr, then `git lfs pull`), `CreateTag`, `PushTags`, `Tags` (annotated tag messages, so deploy tags give workshop id and time), `RemoteUrl`, `LfsAvailable`.
- `Commit` returns `autoPublish: { version }` when workshop.json has autoPublish enabled and the subject matches the pattern. The UI then starts `publish.run`.
- A commit's `deploys` come from `deploy/*` tags on it, `version` from a `v*` tag on it or the pattern match on its subject.

### 6.9 IWatchService
FileSystemWatcher on `<repo>/content` and `<repo>/prebuilt`, debounced 750 ms, ignores `.addonstudio/` and `.git/`. Emits `watch.changed`. The UI decides whether to build (autoBuild).

## 7. RPC between the app and the core

The core ships as `addonstudio-core.exe` (Host project, self-contained single file). `addonstudio-core rpc` speaks JSON lines on stdin/stdout. stdout carries only protocol messages, logs go to stderr.

- Request: `{"id": 7, "method": "build.run", "params": {...}}`
- Response: `{"id": 7, "result": {...}}` or `{"id": 7, "error": {"code": "cs2_missing", "message": "…", "details": null}}`
- Event: `{"event": "build.item", "data": {...}}`. Events of a long request carry `requestId`.
- `rpc.cancel { requestId }` cancels a long request (it then fails with `cancelled`). Exception: `build.run` answers with its partial `BuildResult` (`cancelled: true`, killed items `skipped`) after `build.finished`, so finished items are not lost.
- Requests run concurrently. Error codes: `invalid_params`, `not_found`, `not_windows`, `cs2_missing`, `tools_missing`, `steam_unavailable`, `git_failed`, `conflict`, `busy`, `cancelled`, `internal`.
- JSON: camelCase, enums as camelCase strings, dates ISO 8601 strings, workshop ids as strings, sizes as numbers (bytes).

Also CLI verbs for testing on Windows without the UI, printing JSON: `doctor`, `plan <repo>`, `build <repo> [--full]`, `pack <repo>`, `publish <repo> --note "…" [--version x.y.z]`, `status <repo>`.

### Methods

| Method | Params | Result |
| --- | --- | --- |
| `app.info` | - | `AppInfo { version, os: "windows"\|"macos"\|"linux", canCompile, canPublish }` |
| `env.doctor` | - | `DoctorReport { os, canCompile, canPublish, cs2Path, checks: DoctorCheck[] }` |
| `settings.get` | - | `AppSettings` |
| `settings.set` | `Partial<AppSettings>` without recentProjects | `AppSettings` |
| `projects.list` | - | `RecentProject[]` |
| `projects.open` | `{ path }` | `ProjectInfo` |
| `projects.create` | `{ path, addon, title }` | `ProjectInfo` |
| `projects.clone` | `{ url, path }` (events `git.progress`) | `ProjectInfo` |
| `projects.saveConfig` | `{ path, config: WorkshopConfig }` | `ProjectInfo` |
| `projects.link` | `{ path, backup: boolean }` | `ProjectInfo` |
| `projects.remove` | `{ path }` | `RecentProject[]` |
| `projects.cs2Addons` | - | `Cs2Addon[]` |
| `import.plan` | `{ addon, path, decompile }` | `ImportPlan` |
| `import.run` | `{ addon, path, decompile }` (events `import.progress`) | `ImportResult` |
| `build.plan` | `{ path }` | `BuildPlan` |
| `build.run` | `{ path, mode: "changed"\|"full"\|"files", files? }` (events below) | `BuildResult` |
| `build.clean` | `{ path }` | `{ removed: number }` |
| `pack.contents` | `{ path }` | `PackContents` |
| `pack.export` | `{ path, outDir? }` | `PackExportResult` |
| `publish.run` | `PublishRequest` (events `publish.progress`) | `PublishResult` |
| `steam.items` | - | `SteamItem[]` |
| `steam.status` | `{ path }` | `SteamItemStatus` |
| `git.status` | `{ path }` | `GitStatus` |
| `git.stage` / `git.unstage` / `git.discard` | `{ path, files: string[] }` | `GitStatus` |
| `git.commit` | `CommitRequest` | `CommitResult` |
| `git.push` / `git.pull` / `git.fetch` | `{ path }` (events `git.progress`) | `GitStatus` |
| `git.log` | `{ path, limit, skip }` | `GitCommit[]` |
| `git.show` | `{ path, hash }` | `GitCommitDetail` |
| `git.publishRepo` | `{ path, owner: string \| null, name, private: boolean, description: string \| null }` | `ProjectInfo` (runs `gh repo create [<owner>/]<name> --private\|--public --source <path> --remote origin --push [--description ...]`; a repo with no commits gets its first commit first; `conflict` when it already has a remote; `git_failed` with gh's message when gh is missing or not logged in) |
| `git.githubOwners` | - | `{ user: string, orgs: string[] }` (`gh api user` and `gh api user/orgs`; orgs need the `read:org` scope) |
| `watch.set` | `{ path, enabled }` | `{ enabled }` |
| `rpc.cancel` | `{ requestId }` | `{ cancelled: boolean }` |

### Types

```ts
type Os = "windows" | "macos" | "linux";
type CheckStatus = "ok" | "warn" | "error";
interface DoctorCheck { id: "os"|"steam"|"cs2"|"workshopTools"|"steamRunning"|"git"|"gitLfs"|"gh"; label: string; status: CheckStatus; detail: string; fix: string | null }
interface AppSettings { cs2PathOverride: string | null; compileParallelism: number; compilerArgs: string; maxPackBytes: number /* default 3221225472 */; theme: "dark"|"light"|"system"; recentProjects: RecentProject[] }
interface RecentProject { path: string; addon: string; title: string; lastOpened: string; exists: boolean }

type Visibility = "public" | "friendsOnly" | "private" | "unlisted";
interface WorkshopConfig { addon: string; workshopId: string | null; title: string; description: string | null; visibility: Visibility; tags: string[]; preview: string | null;
  pack: { include: string[]; exclude: string[] }; autoBuild: boolean; autoPublish: { enabled: boolean; pattern: string; githubRelease: boolean } }
interface LinkInfo { state: "linked" | "missing" | "conflict" | "wrongTarget" | "noCs2"; linkPath: string | null; target: string | null; detail: string | null }
interface ProjectInfo { path: string; config: WorkshopConfig; configErrors: string[]; contentDir: string; prebuiltDir: string; buildDir: string | null;
  link: LinkInfo; git: { isRepo: boolean; branch: string | null; remote: string | null; lfs: boolean } }

interface Cs2Addon { name: string; hasContent: boolean; hasGame: boolean; contentFiles: number; gameFiles: number; sizeBytes: number; linkedTo: string | null }
type ImportAction = "copySource" | "decompile" | "prebuilt" | "skip";
interface ImportItem { path: string; size: number; action: ImportAction; target: string | null; reason: string | null }
interface ImportPlan { addon: string; path: string; sourceFiles: number; items: ImportItem[]; totalBytes: number; warnings: string[] }
interface ImportResult { project: ProjectInfo; copied: number; decompiled: number; prebuilt: number; skipped: number; backupPath: string | null; warnings: string[] }

type RootKind = "panorama"|"texture"|"model"|"material"|"particle"|"sound"|"soundevents"|"vdata"|"map"|"other";
type RootStatus = "new"|"changed"|"unchanged"|"deleted"|"unsupported"|"failed";
interface BuildRoot { source: string; kind: RootKind; status: RootStatus; reason: string | null; dependencies: string[]; outputs: string[]; lastResult: "ok"|"warning"|"error"|null; lastBuiltAt: string | null }
interface PrebuiltEntry { path: string; size: number; status: "new"|"changed"|"unchanged"|"deleted" }
interface BuildConflict { path: string; source: string }
interface BuildPlan { generatedAt: string; cs2Ready: boolean; buildDir: string | null; roots: BuildRoot[]; prebuilt: PrebuiltEntry[]; conflicts: BuildConflict[]; orphans: string[];
  counts: { new: number; changed: number; unchanged: number; deleted: number; unsupported: number; failed: number; prebuiltChanged: number } }
type ItemStatus = "ok" | "warning" | "error" | "skipped";
interface BuildItemResult { source: string; status: ItemStatus; durationMs: number; exitCode: number | null; outputs: string[]; errors: string[]; warnings: string[]; log: string /* capped 64 KB */ }
interface BuildResult { ok: boolean; cancelled: boolean; durationMs: number; items: BuildItemResult[]; prebuiltCopied: number; removed: string[] }
// events: build.started { requestId, path, total }  build.itemStarted { requestId, source, index, total }
//         build.log { requestId, source, line }  build.item { requestId, index, total, item: BuildItemResult }  build.finished { requestId, result: BuildResult }

type FileChange = "added" | "changed" | "same";
interface PackFile { path: string; size: number; change: FileChange | null /* null = nothing published yet */; origin: "compiled" | "prebuilt" | "other" }
interface PackContents { files: PackFile[]; removed: string[]; fileCount: number; totalSize: number; limitBytes: number; byType: { label: string; count: number; size: number }[];
  lastPublish: { at: string; workshopId: string; commit: string | null } | null; changes: { added: number; changed: number; removed: number } }
interface PackExportResult { dir: string; files: string[]; totalSize: number }

interface PublishRequest { path: string; changeNote: string; version: string | null; skipBuild: boolean; push: boolean; githubRelease: boolean; allowSourceFolderChange: boolean }
type PublishStage = "build" | "pack" | "upload" | "tag" | "push" | "release" | "done";
// event: publish.progress { requestId, stage: PublishStage, fraction: number | null, message: string | null }
interface PublishResult { workshopId: string; created: boolean; url: string; needsAgreement: boolean; tags: string[]; releaseUrl: string | null; warnings: string[] }

interface SteamItem { workshopId: string; title: string; visibility: Visibility; timeCreated: string; timeUpdated: string; size: number; subscribers: number; favorites: number; views: number; previewUrl: string | null }
interface SteamItemStatus { workshopId: string | null; found: boolean; title: string | null; visibility: Visibility | null; timeUpdated: string | null; size: number | null;
  banned: boolean; banReason: string | null; live: "live" | "processing" | "unknown"; lastPublishAt: string | null; raw: Record<string, unknown> | null }

type GitState = "unmodified"|"modified"|"added"|"deleted"|"renamed"|"copied"|"untracked"|"ignored"|"conflicted"|"typeChanged";
interface GitFileChange { path: string; origPath: string | null; index: GitState; worktree: GitState; staged: boolean; lfs: boolean; size: number | null }
interface GitStatus { isRepo: boolean; branch: string | null; upstream: string | null; ahead: number; behind: number; files: GitFileChange[]; merging: boolean; lfsAvailable: boolean }
interface Deploy { tag: string; at: string; workshopId: string | null; note: string | null }
interface GitCommit { hash: string; short: string; subject: string; body: string; authorName: string; date: string; parents: string[]; refs: string[]; tags: string[]; version: string | null; deploys: Deploy[] }
interface GitCommitDetail { commit: GitCommit; files: { path: string; status: GitState; additions: number | null; deletions: number | null; binary: boolean }[] }
interface CommitRequest { path: string; message: string; files: string[] | null /* stage these first, null = what is staged */; push: boolean }
interface CommitResult { hash: string; pushed: boolean; autoPublish: { version: string } | null; status: GitStatus }
// events: git.progress { requestId, phase: string, percent: number | null, line: string }
//         import.progress { requestId, done: number, total: number, current: string }
//         watch.changed { path, files: string[] }
```

## 8. The app (Tauri 2 + React + HeroUI v3)

- `app/`: Vite + React 19 + TypeScript + Tailwind v4 + HeroUI v3 (`@heroui/react`, `@heroui/styles`), `motion`, `lucide-react`, `@tanstack/react-query`. Tauri plugins: dialog (folder pickers), opener (Steam links).
- RPC client in `src/rpc/`: `types.ts` (section 7 types plus a typed `Methods` map and `Events` map), `client.ts` (`call(method, params, { onEvent, signal })`, `subscribe(event, fn)`), `transport-tauri.ts` (invokes the app commands `core_spawn`/`core_write`/`core_kill` and listens to `core://stdout`, `core://stderr`, `core://exit`), `transport-mock.ts` (browser dev, demo data from `mock/shell.ts` and `mock/project.ts`). Mock is used when `window.__TAURI_INTERNALS__` is missing, so `npm run dev` works in any browser.
- Screens:
  - Sidebar: projects (initial avatar, title, addon, dirty dot, ahead/behind), add project button, doctor status and settings at the bottom.
  - Add project modal: Create new / Clone URL / Import existing CS2 addon (list from `projects.cs2Addons`, then the import plan table with actions, decompile toggle, then run with progress).
  - Doctor: every check with status chip, detail, fix text with copy button.
  - App settings: CS2 path override, parallel compiles, compiler args, pack size cap in GB (default 3, warning above 3), theme.
  - GitHub + LFS help: when a project has no remote, the Changes view shows a "Put this on GitHub" card: a short explanation (sources go to GitHub, big binaries through Git LFS, 10 GiB free then about $0.07 per GiB a month, add a payment method or pushes stop at the quota), a "Create private GitHub repo" button (`git.publishRepo`, needs `gh auth login` once) and a link to docs/GITHUB-LFS.md steps for doing it by hand. Doctor's `gitLfs` and `gh` fix texts point at the same steps.
  - Project header: title, addon, workshop id (opens Steam page), branch, link state chip (with "Link to CS2" action), then tabs: Changes, History, Build, Package, Publish, Settings.
  - Changes: changed files grouped Staged / Changes with checkboxes, LFS badge, size; commit message box; Commit, Commit & Push; Pull/Push with ahead/behind; discard with confirm. If the commit result has `autoPublish`, jump to Publish and start it with the version and the commit message as the note.
  - History: commits (paged with `git.log`), version chip, "Deployed" badge with time and workshop link for commits with deploy tags; click opens the files changed.
  - Build: plan grouped by status (new, changed, failed, deleted, unchanged collapsed), counts, conflicts and orphans, buttons Build changed / Full rebuild / Clean (confirm), live per-file progress with status icons, per-file log in a modal. Auto-build: when `config.autoBuild`, call `watch.set` on open, and on `watch.changed` run a `changed` build (one queued at most, never two at once).
  - Package: every file in the VPK with size, origin and change vs last publish (filter + search + sort), removed files, size bar against `limitBytes` (the maxPackBytes setting), type breakdown, Export button.
  - Publish: change note, version, toggles (push tags, GitHub release), warnings (dirty tree, build has errors, near size cap), Publish button, stage progress, result with links, Steam status card (time updated, size, visibility, banned/ban reason, live/processing, raw fields expandable, refresh).
  - Settings: workshop.json form with validation, Save.
- Design rules (the owner's standing preferences):
  - It must look like a native Windows 11 app (the owner's call: Tauri styled like Windows 11, not a website in a window):
    - Font: `"Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, sans-serif` (display variant for headings), 14px base, Windows 11 type ramp (caption 12, body 14, subtitle 20, title 28).
    - Window background is Windows 11 Mica (Tauri `windowEffects: ["mica"]` + transparent window). The app root keeps a transparent background where Mica should show (sidebar/navigation pane), content panes sit on a slightly lighter layer fill (Windows "layer" colors: dark ~rgba(58,58,58,0.30), light ~rgba(255,255,255,0.50)). A solid fallback color when Mica is not available (Windows 10, browser dev mode).
    - Navigation like Windows 11 apps: left navigation pane with a pill-shaped selected item and the small accent bar on its left edge (sliding with motion layoutId), 4px corner radius on controls, 8px on cards, Windows accent color (default #4CC2FF dark / #005FB8 light) for primary buttons, focus rings and toggles.
    - HeroUI components are themed to match this (radius, colors, density); they must not look like a web dashboard.
  - Dark by default (follows the theme setting). Cards and panels use a fill and maybe a soft shadow, never a 1px outline. No gradient text, no glows.
  - Every clickable element has `cursor: pointer` (Tailwind 4 resets it, add a global rule) and `transition-colors duration-200`.
  - Skeletons shaped like the content while loading, not spinners. Active nav and tab backgrounds slide between items with a motion `layoutId`. Enter-only page transitions, quick. Respect reduced motion.
  - No font-weight change on the active nav item. Logs open in modals. Commands and paths wrap (no sideways scrollbars) and have a copy button.
  - Plain, short UI text. No em dashes anywhere (use `-`).

## 9. Packaging and the app's own CI

- `app/src-tauri/`: Tauri 2, no installer (`bundle.active: false`). The app is ONE exe: `build.rs` zstd-compresses `binaries/addonstudio-core-<triple>[.exe]` into the binary, `src/core.rs` unpacks it once per version to `<app local data>/core/<hash>/` (Windows: `%LOCALAPPDATA%\dev.addonstudio.app\core\`), starts `addonstudio-core rpc` with CREATE_NO_WINDOW and bridges stdio through the commands `core_spawn`/`core_write`/`core_kill` and the events `core://stdout`/`core://stderr`/`core://exit` (each with the spawn id). The core is killed when the app exits. Window 1280x800 (min 1024x640), native title bar with theme following the app, `transparent: true` and `windowEffects: { effects: ["mica"] }` so the Windows 11 Mica backdrop shows through.
- `scripts/build-sidecar.ps1` and `scripts/build-sidecar.sh`: `dotnet publish core/src/AddonStudio.Host -c Release -r <rid> --self-contained -p:PublishSingleFile=true` and copy to `app/src-tauri/binaries/addonstudio-core-<triple>[.exe]`.
- `.github/workflows/app.yml`: on `workflow_dispatch` and tags `app-v*`, on `windows-latest`: .NET 10, Node 24, Rust stable, build the core for win-x64, `npm ci`, `npm run tauri build -- --no-bundle`, `scripts/package-exe.ps1`, upload `AddonStudio.exe` as an unzipped artifact, run `cargo test --lib` (unpacks and starts the embedded core on Windows), and on a tag attach `AddonStudio-<version>-win-x64.exe` to a draft release. Also a `check` job on ubuntu-latest that runs `dotnet test` and `npm run build`.

## 10. This repo

```
docs/SPEC.md, docs/WINDOWS-TESTING.md, docs/GITHUB-LFS.md
core/AddonStudio.slnx
core/src/AddonStudio.Core/     Contracts/ Env/ Settings/ Projects/ Import/ Build/ Pack/ Publish/ Steam/ Git/ Watch/ Util/
core/src/AddonStudio.Host/     Rpc/ Cli/ Program.cs (assembly name addonstudio-core)
core/tests/AddonStudio.Tests/  one folder per area, xunit
app/                           see section 8, app/src-tauri/ for Tauri
scripts/  .github/workflows/
```

Build and test while developing (each agent uses its own artifacts path so parallel builds do not collide):
- `dotnet build core/AddonStudio.slnx --artifacts-path <your tmp dir>` and `dotnet test core/AddonStudio.slnx --artifacts-path <your tmp dir>`
- `cd app && npx tsc --noEmit` and `npx vite build --outDir <your tmp dir>`
- `cd app/src-tauri && cargo check`

## 11. Decisions made while building v1

Gaps the sections above left open, settled in the code:

- Paths: `ImportItem.path` is relative to the CS2 folder it comes from, prefixed `content/` or `game/`; `target` is repo-relative. `watch.changed` files are repo-relative with forward slashes. Expected outputs keep the source's casing with a lower-case compiled extension; every relative-path comparison ignores case.
- Errors: a publish stopped by build errors is `internal` with `details: { failed: string[] }`. A pack over `maxPackBytes` (export or publish) is `invalid_params` with `details: { limitBytes, totalSize }`, a single file over 2 GiB `invalid_params` with `details: { size }`. `build.run` with the content folder not linked: `not_found` (missing), `conflict` (conflict / wrongTarget), `cs2_missing` (noCs2). `projects.cs2Addons` is `cs2_missing` when CS2 is not found, so the UI can say why.
- `settings.set` validation: `compileParallelism` 1..256, `compilerArgs` must contain `{input}`, `maxPackBytes` at least 1 MiB (`invalid_params`).
- `projects.create` writes `preview: null` when there is no preview.png yet (so a fresh project has no config error). An invalid `addon` in workshop.json: the project opens with `buildDir: null` and link state `missing`, build/pack/publish are `invalid_params`.
- `projects.link` re-points a `wrongTarget` junction without `backup: true` (only the junction is removed, never its target). Off Windows the link is a symlink, for development.
- `git.commit` with `push: true`: a failed push (or no remote) still returns the commit, with `pushed: false` and a `git.progress` event with phase `push failed`.
- Publish: our uploader (`Publish/WorkshopUploader.cs`) mirrors `PublishAsync` on our own `SteamClient(730)` and stages with `Pack/AddonVpk` under `maxPackBytes`; the library's static Steam client is never opened. The VPK is named `<workshopId>_dir.vpk` (a new item is created first, then staged). `pack.export` before the first publish names it `<addon>_dir.vpk`. A new item with no tags gets Valve's default tags ("CS2", "Map").
- Timestamps: deploy tags are UTC (as above); the link backup and export folder names use local time, since the user sees those folders.
- The core ships as an uncompressed single file: the compressed bundle (SDK 10.0.301, osx-arm64) crashed with AccessViolationException when two requests loaded assemblies at once. The app build zstd-compresses it into the exe anyway.
- CLI: `pack <repo> [--out <dir> | --contents]` exports the VPK or lists it; `status <repo>` prints `steam.status`; `publish` also takes `--skip-build --push --github-release --allow-source-change`.
- App: Vite serves on port 1420 (strict), which `tauri.conf.json` devUrl matches. Tauri identifier `dev.addonstudio.desktop` (an id ending in `.app` clashes with the macOS bundle extension). The app version comes from `app/package.json`, the core's from `core/Directory.Build.props`. The capability also allows `core:window:allow-set-theme`, `dialog:allow-open` and `opener:allow-open-url` (steamcommunity.com and steam://).
- Watching is on while the Build tab is open with `autoBuild`; the Changes tab refreshes git.status on window focus and with its refresh button.
