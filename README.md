# Addon Studio

A Windows desktop app for CS2 Steam Workshop addons, with each addon kept as a git repo. You edit sources, commit, see history, build only what changed, see exactly what goes into the VPK, and publish to the Workshop with one button (or automatically on a version commit).

It is a Tauri 2 app (React + HeroUI v3, styled like a Windows 11 app) with a .NET 10 core that runs as a sidecar process. The full contract between the parts is [docs/SPEC.md](docs/SPEC.md).

## The pipeline

```
content/ (sources, in git)
   | resourcecompiler.exe, only the roots that changed
   v
<CS2>/game/csgo_addons/<addon>/      build dir (Valve's location, regenerated, never in git)
   ^ prebuilt/ copied in as-is (compiled files without sources: models, maps...)
   | pack: Valve's workshop manager rules (VPK v2, 100 MB chunks, 3 GiB cap by default)
   v
<id>_dir.vpk + _000.vpk... + publish_data.txt
   | upload through the running Steam client (ISteamUGC)
   v
Steam Workshop item -> Steam processes it -> live
```

- `content/` is linked into `<CS2>/content/csgo_addons/<addon>` with a directory junction, so the compiler sees the addon where it expects it.
- A file counts as changed when its bytes or any file it references change (images, includes, materials...). Changing the compiler or its arguments rebuilds everything.
- Publishing tags the commit (`deploy/<time>`, optional `v<version>`), so History shows what went live when.
- Build and publish run on your PC. Compiling and publishing need Windows, CS2 with the Workshop Tools and a running Steam client.

## Repo layout

```
docs/SPEC.md               the contract every part follows
docs/WINDOWS-TESTING.md    step-by-step test on Windows, what to send back when it fails
docs/GITHUB-LFS.md         putting an addon on GitHub with Git LFS
core/AddonStudio.slnx
core/src/AddonStudio.Core/ services: Env, Settings, Projects, Import, Build, Pack, Publish, Steam, Git, Watch
core/src/AddonStudio.Host/ addonstudio-core: JSON-lines RPC on stdin/stdout + CLI verbs
core/tests/                xunit tests
app/                       Vite + React 19 + TypeScript + Tailwind v4 + HeroUI v3
app/src/rpc/               typed RPC client, Tauri transport (spawns the sidecar) and a mock transport
app/src-tauri/             Tauri 2 shell: window, plugins (shell, dialog, opener), capabilities, NSIS bundle
scripts/                   build-sidecar.ps1 / build-sidecar.sh, make-icon.mjs
.github/workflows/app.yml  builds the Windows installer
```

## Development

Needs .NET 10 SDK, Node 24 and Rust stable. macOS and Linux work for everything except compiling and publishing addons: those calls fail with `not_windows`, `cs2_missing`, `tools_missing` or `steam_unavailable`.

### UI only, in a browser (mock mode)

```sh
cd app
npm install
npm run dev
```

Open http://localhost:1420. Without Tauri the app uses the mock transport (`app/src/rpc/transport-mock.ts`) with demo projects, so every screen works without the core.

### The real app (Tauri dev)

Build the core sidecar once (and again after core changes), then start Tauri:

```sh
scripts/build-sidecar.sh            # Windows: powershell -ExecutionPolicy Bypass -File scripts\build-sidecar.ps1
cd app
npm run tauri dev
```

`build-sidecar` publishes `core/src/AddonStudio.Host` as a self-contained single file for this machine (target triple from `rustc -vV`, or pass a rid like `win-x64`) and copies it to `app/src-tauri/binaries/addonstudio-core-<triple>[.exe]`, where Tauri's `externalBin` expects it.

### Core

```sh
dotnet build core/AddonStudio.slnx
dotnet test core/AddonStudio.slnx
```

Frontend logic tests (node:test through jiti): `cd app && npm test`.

The core also has CLI verbs that print JSON, handy on Windows without the UI:

```
addonstudio-core doctor
addonstudio-core plan <repo>
addonstudio-core build <repo> [--full]
addonstudio-core pack <repo> [--contents | --out <dir>]
addonstudio-core publish <repo> --note "..." [--version x.y.z]
addonstudio-core status <repo>
```

### Checks

```sh
cd app && npx tsc --noEmit && npx vite build
cd app/src-tauri && cargo check
```

## Building the installer

On Windows: `scripts\build-sidecar.ps1 -Rid win-x64`, then `npm ci` and `npm run tauri build` in `app/`. The NSIS installer lands in `app/src-tauri/target/release/bundle/nsis/`.

CI does the same: [.github/workflows/app.yml](.github/workflows/app.yml) runs on "Run workflow" and on tags `app-v*`. It uploads the installer as an artifact and, for a tag, attaches it to a draft release. A second job runs the core tests and the frontend build on Linux. The app version comes from `app/package.json`.

## App icon

`scripts/make-icon.mjs` draws the 1024 px source (`app/src-tauri/icons/app-icon.png`). To regenerate the set:

```sh
node scripts/make-icon.mjs
cd app && npx tauri icon src-tauri/icons/app-icon.png
```

Then delete the `android/`, `ios/` and `Square*`/`StoreLogo` files it adds, the app is desktop only.
