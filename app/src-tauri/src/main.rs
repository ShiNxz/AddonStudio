// Release builds are GUI-subsystem apps on Windows: no console window behind the app.
// The core sidecar is spawned by the shell plugin with CREATE_NO_WINDOW, so it never opens one either.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    addon_studio_lib::run()
}
