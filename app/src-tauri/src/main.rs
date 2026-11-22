// Release builds are GUI-subsystem apps on Windows: no console window behind the app.
// The embedded core is started with CREATE_NO_WINDOW (src/core.rs), so it never opens one either.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    addon_studio_lib::run()
}
