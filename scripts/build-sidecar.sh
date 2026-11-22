#!/usr/bin/env bash
# Publishes the core (core/src/AddonStudio.Host) as a self-contained single file and copies it to
# app/src-tauri/binaries/addonstudio-core-<target triple>[.exe], where Tauri's bundle.externalBin looks for it.
#
# usage: scripts/build-sidecar.sh [rid] [extra dotnet publish args...]
#   rid: win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64. Default: this machine (from rustc -vV).
#
# The build uses its own temporary artifacts folder, so it never collides with other builds of the solution.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/core/src/AddonStudio.Host/AddonStudio.Host.csproj"
binaries="$root/app/src-tauri/binaries"

rid_to_triple() {
  case "$1" in
    win-x64) echo "x86_64-pc-windows-msvc" ;;
    win-arm64) echo "aarch64-pc-windows-msvc" ;;
    osx-x64) echo "x86_64-apple-darwin" ;;
    osx-arm64) echo "aarch64-apple-darwin" ;;
    linux-x64) echo "x86_64-unknown-linux-gnu" ;;
    linux-arm64) echo "aarch64-unknown-linux-gnu" ;;
    *) return 1 ;;
  esac
}

triple_to_rid() {
  case "$1" in
    x86_64-pc-windows-msvc | x86_64-pc-windows-gnu) echo "win-x64" ;;
    aarch64-pc-windows-msvc) echo "win-arm64" ;;
    x86_64-apple-darwin) echo "osx-x64" ;;
    aarch64-apple-darwin) echo "osx-arm64" ;;
    x86_64-unknown-linux-gnu) echo "linux-x64" ;;
    aarch64-unknown-linux-gnu) echo "linux-arm64" ;;
    *) return 1 ;;
  esac
}

rid="${1:-}"
if [ $# -gt 0 ]; then shift; fi

if [ -z "$rid" ]; then
  if ! command -v rustc >/dev/null 2>&1; then
    echo "rustc not found: pass a rid (e.g. win-x64) or install Rust" >&2
    exit 1
  fi
  triple="$(rustc -vV | sed -n 's/^host: //p')"
  rid="$(triple_to_rid "$triple")" || { echo "no .NET rid known for host triple $triple, pass one" >&2; exit 1; }
else
  triple="$(rid_to_triple "$rid")" || { echo "unknown rid $rid (win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64)" >&2; exit 1; }
fi

ext=""
case "$rid" in win-*) ext=".exe" ;; esac

work="$(mktemp -d "${TMPDIR:-/tmp}/addonstudio-sidecar.XXXXXX")"
trap 'rm -rf "$work"' EXIT

echo "publishing core for $rid ($triple)"
dotnet publish "$project" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true \
  --artifacts-path "$work/artifacts" \
  -o "$work/out" \
  "$@"

built="$work/out/addonstudio-core$ext"
if [ ! -f "$built" ]; then
  echo "publish did not produce $built" >&2
  ls -la "$work/out" >&2 || true
  exit 1
fi

mkdir -p "$binaries"
target="$binaries/addonstudio-core-$triple$ext"
cp "$built" "$target"
chmod +x "$target"

# a single-file publish should leave nothing else next to the exe. If it does, the installer would miss it.
extra="$(find "$work/out" -type f ! -name "addonstudio-core$ext" ! -name '*.pdb' ! -name '*.dbg' ! -name '*.dSYM' | sed "s|^$work/out/||")"
if [ -n "$extra" ]; then
  echo "warning: the publish left files next to the exe that the app will not ship:" >&2
  echo "$extra" >&2
fi

echo "wrote $target ($(wc -c <"$target" | tr -d ' ') bytes)"
