//! Addon Studio's Tauri shell. All the work happens in the .NET core (addonstudio-core rpc), which is embedded in
//! this exe, unpacked once into the app's local data folder and spoken to over JSON lines (docs/SPEC.md section 7).
//! See core.rs for the bridge: the commands core_spawn / core_write / core_kill and the core://stdout,
//! core://stderr and core://exit events.
//!
//! Plugins:
//! - dialog: folder and file pickers.
//! - opener: Steam Workshop pages (https://steamcommunity.com/*) and steam:// links.

mod core;

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .manage(core::CoreState::default())
        .invoke_handler(tauri::generate_handler![core::core_spawn, core::core_write, core::core_kill])
        .build(tauri::generate_context!())
        .expect("Addon Studio failed to start")
        .run(|app, event| {
            // the core never outlives the window
            if let tauri::RunEvent::Exit = event {
                core::kill_all(app);
            }
        });
}
