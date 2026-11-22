//! Addon Studio's Tauri shell. All the work happens in the core sidecar (binaries/addonstudio-core rpc),
//! which the frontend spawns through the shell plugin and talks to over JSON lines (docs/SPEC.md section 7).
//!
//! Plugins:
//! - shell: spawns the sidecar, writes its stdin, kills it. capabilities/default.json allows only that one binary
//!   with the single argument "rpc". On Windows the plugin creates child processes with CREATE_NO_WINDOW, and when
//!   the app exits it kills the children it spawned, so the core never outlives the window.
//! - dialog: folder and file pickers.
//! - opener: Steam Workshop pages (https://steamcommunity.com/*) and steam:// links.

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .run(tauri::generate_context!())
        .expect("Addon Studio failed to start");
}
