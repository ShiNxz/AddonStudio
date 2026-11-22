# Third-party notices

Addon Studio uses or adapts code from these projects. Each keeps its own license.

- **CS2 Workshop Manager** by Angel - https://github.com/Angel-foxxo/CS2-Workshop-Manager - MIT License, Copyright (c) 2026 Angel.
  Used as the `CS2WorkshopManager` NuGet package (file selection, Steam client access through its `Steamworks.dll`).
  The VPK packer in `core/src/AddonStudio.Core/Pack/` and the uploader in `core/src/AddonStudio.Core/Publish/WorkshopUploader.cs`
  follow its `AddonPackager` and `WorkshopManager.PublishAsync` with a configurable size cap.
- **ValvePak** by SteamDatabase - https://github.com/ValveResourceFormat/ValvePak - MIT License.
- **ValveResourceFormat (Source 2 Viewer)** by SteamDatabase - https://github.com/ValveResourceFormat/ValveResourceFormat - MIT License.
- **Tauri** - https://tauri.app - MIT or Apache-2.0.
- **HeroUI** - https://heroui.com - MIT License.

Valve, Steam, Counter-Strike 2 and the Steam Workshop are trademarks of Valve Corporation. This project is not affiliated with Valve.
Valve's `resourcecompiler.exe` is not included; it comes from the user's own CS2 Workshop Tools install.
