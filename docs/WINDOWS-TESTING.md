# Testing Addon Studio on Windows

Compile and publish only work on Windows (Valve's `resourcecompiler.exe` and the Steam client), so this is the real test. Go through it in order. Every step says what you should see. If something does not match, stop and send what is listed in "What to send back".

## 1. Get the app (one exe, nothing to install)

### From GitHub Actions (easiest)

1. On GitHub open the repo, then Actions > app.
2. Open the latest green run. No run yet: click "Run workflow" on `main` and wait about 10 minutes.
3. At the bottom of the run, under Artifacts, download `AddonStudio.exe` (you need to be logged in to GitHub). That one file is the whole app.
4. Put it anywhere and run it. It is not signed, so Windows SmartScreen may say "Windows protected your PC": click "More info", then "Run anyway".
5. Where things go:
   - Settings and the recent projects list: `%APPDATA%\AddonStudio\settings.json`.
   - The .NET core is embedded in the exe and unpacked once per version to `%LOCALAPPDATA%\dev.addonstudio.app\core\`. The first start takes a second or two longer because of that. A new version replaces the old copy.
   - WebView2's browser cache: `%LOCALAPPDATA%\dev.addonstudio.app\EBWebView`.
   - Optional: put an empty `portable.txt` next to `AddonStudio.exe` to keep settings in a `data` folder beside it instead (for a USB stick).
6. It needs the Microsoft Edge WebView2 Runtime. Windows 11 and up-to-date Windows 10 already have it; if the window stays blank, install it from https://developer.microsoft.com/microsoft-edge/webview2/.

A tag `app-v<version>` (for example `app-v0.1.0`) runs the same build and puts `AddonStudio.exe` and `AddonStudio-<version>-win-x64.exe` on a draft release.

### Or build it on the PC

Prerequisites (once):

- Visual Studio 2022 Build Tools (or Visual Studio) with the "Desktop development with C++" workload. Rust needs its linker and the Windows SDK.
- Rust: https://rustup.rs, default options (`x86_64-pc-windows-msvc`, stable).
- .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
- Node.js 24: https://nodejs.org
- Git for Windows: https://git-scm.com/download/win (includes Git LFS).

Then in PowerShell:

```powershell
git clone <repo url> AddonStudio
cd AddonStudio
powershell -ExecutionPolicy Bypass -File scripts\build-sidecar.ps1 -Rid win-x64
cd app
npm ci
npm run tauri build -- --no-bundle
cd ..
powershell -ExecutionPolicy Bypass -File scripts\package-exe.ps1
```

The app lands in `dist\AddonStudio.exe`. For a dev window with hot reload instead: `npm run tauri dev` from `app\` (the core must have been built once with `build-sidecar`, it gets embedded on every build).

## 2. First run: Doctor

Open Addon Studio, then Doctor at the bottom of the sidebar. Every check should be green:

| Check | What it needs | Fix if not green |
| --- | --- | --- |
| os | Windows | - |
| steam | Steam installed | Install Steam |
| cs2 | CS2 installed (found through Steam's library folders) | Install CS2, or set "CS2 path" in app settings to the folder that has `game\` in it |
| workshopTools | `game\bin\win64\resourcecompiler.exe` exists | Steam > CS2 > Properties > DLC > tick "Counter-Strike 2 Workshop Tools" |
| steamRunning | Steam running and logged in | Start Steam and log in |
| git | git on PATH | Install Git for Windows |
| gitLfs | Git LFS | `git lfs install` once in a terminal |
| gh | GitHub CLI, logged in (only for the "Create private GitHub repo" button and GitHub releases) | Install from https://cli.github.com, run `gh auth login` |

`gh` being yellow is fine for this test. Everything else must be green before you go on.

## 3. Import an existing addon

Use an addon you already have in CS2 (the HUD is the good first one).

1. Back up first: copy `<CS2>\content\csgo_addons\<addon>` and `<CS2>\game\csgo_addons\<addon>` somewhere outside the CS2 folder. The import never deletes anything, this is just in case.
2. Sidebar > Add project > Import existing CS2 addon. Pick the addon from the list.
3. Choose a new empty target folder, for example `C:\Addons\<addon>`. Keep "Decompile" on.
4. Check the plan table. Sources should be `copySource`, compiled files that we have sources for should be `skip`, panorama/sounds without sources `decompile`, models/materials/maps `prebuilt`.
5. Run the import. When it finishes:
   - `C:\Addons\<addon>` has `workshop.json`, `content\`, `prebuilt\`, `.gitattributes`, `.gitignore` and is a git repo.
   - The old content folder was renamed to `<addon>.addonstudio-backup-<date>-<time>`.
   - `<CS2>\content\csgo_addons\<addon>` is now a junction to `C:\Addons\<addon>\content`. Check in a terminal: `dir "<CS2>\content\csgo_addons"` shows `<JUNCTION>` next to it. The project header's link chip says linked.
6. Changes tab: everything is listed as new. Commit it ("initial import").

## 4. Build

1. Build tab. On the first build every root is `new`.
2. Click "Build changed". Each file shows its status live. All should end ok (yellow warnings are fine).
3. Click a failed file to see its compiler log. Send that log back if anything fails (see below).
4. Click "Build changed" again: nothing should compile (everything `unchanged`).
5. Edit one source file (for example a `.css` in `content\panorama\`), save. With auto-build on, a build of just that file starts by itself within a second or two. Without it, "Build changed" compiles only that file.
6. Orphans: files in the build dir that nothing explains are listed. "Clean" deletes the build dir except Valve's own files and rebuilds everything. Try it once.

## 5. Check the HUD in game

1. Steam > Library > Counter-Strike 2 > Launch > "Counter-Strike 2 Workshop Tools".
2. Pick the addon in the list and launch it.
3. When the game is up, open the console and load a map (`map de_dust2`, or the map you normally test with).
4. Check that the HUD looks and works like before the import. Anything missing usually means a file that should have been `prebuilt` or `decompile` was skipped: look at the Package tab for it and send the import plan.

## 5b. Check what gets packed

Packing takes everything in the build folder by default. Valve's workshop manager only packs the folders `gameinfo.gi` whitelists, so files elsewhere (custom rank badges under `panorama/images/icons/skillgroups/`, models under `characters/`) never reached the Workshop.

1. Package tab. The card on top says "Packing: everything".
2. It lists the files outside Valve's whitelist ("N files outside Valve's whitelist, packed anyway"). Open "Files": every file there used to be left out. If your addon has rank badges or models outside the `custom_game` folders, they are in this list.
3. In the file table below, search for a few of them (for example `skillgroup` or `characters`): they are in the VPK.
4. "N files left out" (if shown) should only hold junk and source files (`.psd`, `.fbx`, raw `.xml`/`.css`/`.js`, `Thumbs.db`...). Anything there that the game needs: add an include rule for it in Settings > Packing and tell us the path.
5. Export VPK, then open the `_dir.vpk` with any VPK viewer (Source 2 Viewer / ValveResourceFormat) and check `panorama/images/icons/skillgroups/` and `characters/` are inside.
6. Optional: Settings > Packing > turn on "Valve whitelist only", Save. The Package tab now says "Packing: Valve whitelist only" and warns that the whitelist leaves those files out, and Publish shows the same warning. Turn it off again and Save.

Close the game before you publish.

## 6. Publish to a NEW unlisted test item first

Do not touch the real item until the test item works.

1. Settings tab: clear "Workshop ID" (so a new item is created), set Visibility to `unlisted`, add " TEST" to the title. Save.
2. Package tab: check the file list and total size look right, and that it says "Packing: everything" (section 5b).
3. Publish tab: change note "test upload", no version, "push tags" off, "GitHub release" off. Click Publish.
4. Stages go build > pack > upload > tag > done. The result shows the new item's link. Its id is written into `workshop.json`.
5. If it says you need to accept the Workshop agreement: open https://steamcommunity.com/sharedfiles/workshoplegalagreement, accept, publish again.
6. Open the item page. The Steam status card goes from processing to live once Steam has processed the upload (can take a few minutes).
7. Subscribe to the test item, start CS2 normally (not the tools) and check the HUD from the Workshop copy. Files from the 5b list (rank badges on the scoreboard, models) must show up there too.
8. History tab: the commit now has a "Deployed" badge with the test item. The local `deploy/...` tag points at the test item; delete it if you like with `git tag -d deploy/<date>-<time>`.

## 7. Publish the real item

1. Settings tab: set "Workshop ID" to the real item's id (the number in its Steam URL), set Visibility and title back to what the real item uses. Save and commit.
2. Publish tab: write the real change note, set the version if you use one, "push tags" on if the repo has a remote. Publish.
3. If it asks about a source folder change: the real item was published from a different addon folder name before. Confirm only if that is expected (for example you renamed the addon).
4. Check the item page and the Steam status card like for the test item. Then delete or hide the test item on Steam.

## What to send back if something fails

- A screenshot of the screen with the error, and of Doctor.
- Compile errors: the per-file compiler log. Build tab > click the failed file > copy the log from the modal, or zip the repo's `.addonstudio\logs\` folder (one `.log` per compiled file, newest run).
- The core's log (its stderr). In the app press F12 (or Ctrl+Shift+I), open Console, set the level filter to include "Verbose", type `[core]` in the filter box, then right-click the log > "Save as...". If the app does not start far enough for that, run the same thing from a terminal, which prints the log directly:

  ```powershell
  cd "$env:LOCALAPPDATA\Addon Studio"
  .\addonstudio-core.exe doctor > doctor.json 2> core-stderr.txt
  .\addonstudio-core.exe build "C:\Addons\<addon>" > build.json 2> core-stderr.txt
  .\addonstudio-core.exe publish "C:\Addons\<addon>" --note "test upload" > publish.json 2> core-stderr.txt
  ```

  Send the `.json` and `core-stderr.txt` files. (`plan`, `pack <repo> --contents` and `status <repo>` work the same way.)
- `C:\Addons\<addon>\workshop.json` and the `.addonstudio\build-state.json` next to it.
- `%APPDATA%\AddonStudio\settings.json`.
- Your Windows version (run `winver`) and the app version (bottom of Doctor or app settings).
