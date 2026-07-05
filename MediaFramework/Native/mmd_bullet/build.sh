#!/usr/bin/env bash
# Builds the mmd_bullet shared library and stages it where the .NET runtime's P/Invoke resolver finds it.
# Cross-platform: libmmd_bullet.so (Linux), libmmd_bullet.dylib (macOS), mmd_bullet.dll (Windows/MSVC).
# Usage: ./build.sh [output-dir]  (default output-dir = this script's directory)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:-$here}"
build="$here/build"

# Generator/platform per host. Windows (git-bash on the CI runner) uses the default Visual Studio generator
# with an explicit x64 platform; elsewhere prefer Ninja when present (fast), else the default generator.
gen=()
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) gen=(-A x64) ;;                         # MSVC x64 (multi-config)
    *) command -v ninja >/dev/null 2>&1 && gen=(-G Ninja) ;;
esac

cmake -S "$here" -B "$build" "${gen[@]}" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build" --config Release --parallel

# The library lands at $build/libmmd_bullet.so (single-config) or $build/Release/mmd_bullet.dll (MSVC
# multi-config) — search for whichever this platform produced.
lib="$(find "$build" \( -name 'libmmd_bullet.so' -o -name 'libmmd_bullet.dylib' -o -name 'mmd_bullet.dll' \) -type f 2>/dev/null | head -1)"
if [[ -z "$lib" ]]; then
    echo "build failed: no mmd_bullet library produced under $build" >&2
    exit 1
fi
mkdir -p "$out"
cp -v "$lib" "$out/"
echo "staged $(basename "$lib") -> $out"
