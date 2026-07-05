#!/usr/bin/env bash
# Builds libmmd_bullet.so and stages it where the .NET runtime's P/Invoke resolver finds it.
# Usage: ./build.sh [output-dir]  (default output-dir = this script's directory)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:-$here}"
build="$here/build"

# Prefer Ninja when present (fast); fall back to the default generator otherwise (CI runners vary).
gen=()
if command -v ninja >/dev/null 2>&1; then
    gen=(-G Ninja)
fi
cmake -S "$here" -B "$build" "${gen[@]}" -DCMAKE_BUILD_TYPE=Release
cmake --build "$build" --config Release -j

lib="$build/libmmd_bullet.so"
if [[ ! -f "$lib" ]]; then
    echo "build failed: $lib not produced" >&2
    exit 1
fi
mkdir -p "$out"
cp -v "$lib" "$out/"
echo "staged libmmd_bullet.so -> $out"
