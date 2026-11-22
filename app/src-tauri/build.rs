//! Embeds the .NET core into the app exe so Addon Studio ships as ONE file.
//!
//! The core (binaries/addonstudio-core-<target triple>[.exe], made by scripts/build-sidecar) is zstd-compressed into
//! OUT_DIR and included with include_bytes! in src/core.rs. At runtime it is unpacked once per version into the app's
//! local data folder (%LOCALAPPDATA%\dev.addonstudio.app\core\<hash>\ on Windows) and started from there.

use std::{env, fs, path::PathBuf};

fn main() {
    let target = env::var("TARGET").expect("TARGET is set by cargo");
    let ext = if target.contains("windows") { ".exe" } else { "" };
    let manifest = PathBuf::from(env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR is set by cargo"));
    let core = manifest.join("binaries").join(format!("addonstudio-core-{target}{ext}"));

    println!("cargo:rerun-if-changed={}", core.display());

    let bytes = fs::read(&core).unwrap_or_else(|_| {
        panic!(
            "{} is missing. Build the core first: scripts/build-sidecar.sh (Windows: scripts\\build-sidecar.ps1 -Rid win-x64).",
            core.display()
        )
    });

    // level 15 keeps CI fast while still shrinking the ~90 MB self-contained core to about a third
    let compressed = zstd::stream::encode_all(&bytes[..], 15).expect("compressing the core");
    let out = PathBuf::from(env::var("OUT_DIR").expect("OUT_DIR is set by cargo")).join("addonstudio-core.zst");
    fs::write(&out, compressed).expect("writing the compressed core");

    // the unpack folder is keyed by this, so a new build never runs an old unpacked core
    println!("cargo:rustc-env=ADDONSTUDIO_CORE_HASH={:016x}-{}", fnv1a(&bytes), bytes.len());
    println!("cargo:rustc-env=ADDONSTUDIO_CORE_EXT={ext}");

    tauri_build::build()
}

/// FNV-1a 64: only a version key for the unpack folder, not a security check.
fn fnv1a(bytes: &[u8]) -> u64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}
