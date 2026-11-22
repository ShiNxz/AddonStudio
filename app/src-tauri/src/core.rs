//! The embedded core: unpacked once per version into the app's local data folder, started as `addonstudio-core rpc`,
//! and bridged to the frontend. JSON lines go webview -> core_write -> stdin, and stdout -> "core://stdout" events.
//! stderr lines go out as "core://stderr", and the end of the process as "core://exit". Every event carries the id
//! core_spawn returned, so the frontend ignores lines from a core it already replaced.

use std::{
    fs,
    io::{BufRead, BufReader, Read, Write},
    path::{Path, PathBuf},
    process::{Child, ChildStdin, Command, Stdio},
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex,
    },
    thread,
};

use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, State};

const CORE_ZST: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/addonstudio-core.zst"));
const CORE_HASH: &str = env!("ADDONSTUDIO_CORE_HASH");
const CORE_FILE: &str = concat!("addonstudio-core", env!("ADDONSTUDIO_CORE_EXT"));

/// Windows CREATE_NO_WINDOW: the core is a console program, it must not flash a console.
#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[derive(Default)]
pub struct CoreState {
    running: Mutex<Option<Running>>,
    next_id: AtomicU64,
}

struct Running {
    id: u64,
    child: Child,
    stdin: ChildStdin,
}

#[derive(Clone, Serialize)]
struct LineEvent {
    id: u64,
    line: String,
}

#[derive(Clone, Serialize)]
struct ExitEvent {
    id: u64,
    reason: String,
}

/// Unpacks the embedded core into `base/<hash>/` unless it is there already, and removes older versions.
pub fn unpack_into(base: &Path) -> Result<PathBuf, String> {
    let dir = base.join(CORE_HASH);
    let exe = dir.join(CORE_FILE);

    if exe.is_file() {
        return Ok(exe);
    }

    fs::create_dir_all(&dir).map_err(|e| format!("could not create {}: {e}", dir.display()))?;

    // decompress next to the target and rename, so a half-written file is never started
    let tmp = dir.join(format!("{CORE_FILE}.{}.tmp", std::process::id()));
    let written = (|| -> std::io::Result<()> {
        let mut out = fs::File::create(&tmp)?;
        zstd::stream::copy_decode(CORE_ZST, &mut out)?;
        out.sync_all()?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&tmp, fs::Permissions::from_mode(0o755))?;
        }
        Ok(())
    })();

    if let Err(error) = written {
        let _ = fs::remove_file(&tmp);
        return Err(format!("could not unpack the core into {}: {error}", dir.display()));
    }

    if let Err(error) = fs::rename(&tmp, &exe) {
        let _ = fs::remove_file(&tmp);
        // a second window may have won the race; its copy is just as good
        if !exe.is_file() {
            return Err(format!("could not unpack the core into {}: {error}", dir.display()));
        }
    }

    // older versions: best effort, one still running is locked and stays until next time
    if let Ok(entries) = fs::read_dir(base) {
        for entry in entries.flatten() {
            if entry.file_name() != CORE_HASH && entry.path().is_dir() {
                let _ = fs::remove_dir_all(entry.path());
            }
        }
    }

    Ok(exe)
}

/// A "portable.txt" next to AddonStudio.exe keeps settings in a "data" folder beside it instead of %APPDATA%.
fn portable_home() -> Option<PathBuf> {
    let dir = std::env::current_exe().ok()?.parent()?.to_path_buf();
    dir.join("portable.txt").is_file().then(|| dir.join("data"))
}

/// Starts `<exe> rpc` with piped stdio and no console window.
pub fn start(exe: &Path) -> Result<Child, String> {
    let mut command = Command::new(exe);
    command.arg("rpc").stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped());

    if let Some(home) = portable_home() {
        command.env("ADDONSTUDIO_HOME", home);
    }

    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(CREATE_NO_WINDOW);
    }

    command.spawn().map_err(|e| format!("could not start {}: {e}", exe.display()))
}

/// Calls `emit` with every line the reader gives (UTF-8, lossy), without the line ending.
pub fn pump_lines<R: Read>(reader: R, mut emit: impl FnMut(String)) {
    let mut reader = BufReader::new(reader);
    let mut buffer = Vec::new();

    loop {
        buffer.clear();
        match reader.read_until(b'\n', &mut buffer) {
            Ok(0) | Err(_) => break,
            Ok(_) => {
                while matches!(buffer.last(), Some(b'\n' | b'\r')) {
                    buffer.pop();
                }
                emit(String::from_utf8_lossy(&buffer).into_owned());
            }
        }
    }
}

/// Starts the core (unpacking it first if needed) and returns its id. A core that was running is stopped.
#[tauri::command]
pub fn core_spawn(app: AppHandle, state: State<'_, CoreState>) -> Result<u64, String> {
    let base = app
        .path()
        .app_local_data_dir()
        .map_err(|e| format!("no local data folder: {e}"))?
        .join("core");
    let exe = unpack_into(&base)?;
    let mut child = start(&exe)?;

    let id = state.next_id.fetch_add(1, Ordering::SeqCst) + 1;
    let stdout = child.stdout.take().ok_or("the core has no stdout")?;
    let stderr = child.stderr.take().ok_or("the core has no stderr")?;
    let stdin = child.stdin.take().ok_or("the core has no stdin")?;

    let previous = state
        .running
        .lock()
        .map_err(|_| "core state is poisoned")?
        .replace(Running { id, child, stdin });

    if let Some(mut old) = previous {
        let _ = old.child.kill();
        let _ = old.child.wait();
    }

    let out_app = app.clone();
    thread::spawn(move || {
        pump_lines(stdout, |line| {
            let _ = out_app.emit("core://stdout", LineEvent { id, line });
        });

        // stdout closed: the process is ending, collect how it ended
        let reason = match out_app.state::<CoreState>().running.lock() {
            Ok(mut running) if running.as_ref().map(|r| r.id) == Some(id) => match running.take() {
                Some(mut gone) => match gone.child.wait() {
                    Ok(status) => format!("exit code {}", status.code().map_or("none".to_string(), |c| c.to_string())),
                    Err(error) => format!("error: {error}"),
                },
                None => "stopped".to_string(),
            },
            _ => "stopped".to_string(),
        };

        let _ = out_app.emit("core://exit", ExitEvent { id, reason });
    });

    let err_app = app.clone();
    thread::spawn(move || {
        pump_lines(stderr, |line| {
            let _ = err_app.emit("core://stderr", LineEvent { id, line });
        });
    });

    Ok(id)
}

/// Writes one protocol line to the core's stdin.
#[tauri::command]
pub fn core_write(state: State<'_, CoreState>, id: u64, line: String) -> Result<(), String> {
    let mut running = state.running.lock().map_err(|_| "core state is poisoned")?;

    match running.as_mut() {
        Some(core) if core.id == id => {
            let text = if line.ends_with('\n') { line } else { format!("{line}\n") };
            core.stdin
                .write_all(text.as_bytes())
                .and_then(|()| core.stdin.flush())
                .map_err(|e| format!("could not write to the core: {e}"))
        }
        _ => Err("The core is not running.".to_string()),
    }
}

/// Stops the core with that id (no-op when it is already gone).
#[tauri::command]
pub fn core_kill(state: State<'_, CoreState>, id: u64) -> Result<(), String> {
    let taken = {
        let mut running = state.running.lock().map_err(|_| "core state is poisoned")?;
        if running.as_ref().map(|r| r.id) == Some(id) {
            running.take()
        } else {
            None
        }
    };

    if let Some(mut core) = taken {
        let _ = core.child.kill();
        let _ = core.child.wait();
    }

    Ok(())
}

/// Called when the app exits: the core never outlives the window.
pub fn kill_all(app: &AppHandle) {
    if let Ok(mut running) = app.state::<CoreState>().running.lock() {
        if let Some(mut core) = running.take() {
            let _ = core.child.kill();
            let _ = core.child.wait();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The embedded core unpacks, starts and answers app.info over stdin/stdout.
    #[test]
    fn embedded_core_unpacks_and_answers() {
        let base = std::env::temp_dir().join(format!("addonstudio-core-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&base);

        let exe = unpack_into(&base).expect("unpack");
        assert!(exe.is_file());
        assert_eq!(unpack_into(&base).expect("second unpack is a no-op"), exe);

        let mut child = start(&exe).expect("start");
        let mut stdin = child.stdin.take().unwrap();
        stdin.write_all(b"{\"id\":1,\"method\":\"app.info\"}\n").unwrap();
        stdin.flush().unwrap();

        // one response line, then close stdin so the core shuts down
        let mut reader = BufReader::new(child.stdout.take().unwrap());
        let mut line = String::new();
        reader.read_line(&mut line).expect("a response line");
        drop(stdin);
        let _ = child.wait();

        assert!(line.contains("\"id\":1") && line.contains("\"result\""), "unexpected response: {line}");
        let _ = fs::remove_dir_all(&base);
    }
}
