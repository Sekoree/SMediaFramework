#!/usr/bin/env bash
# REL-01: dynamically load every required native from the exact publish directory that will be uploaded.
# Filename presence alone does not catch a wrong architecture, corrupt binary, or missing transitive dependency.
set -euo pipefail

rid="${1:-}"
dir="${2:-}"
if [ -z "$rid" ] || [ -z "$dir" ]; then
    echo "usage: $0 <rid> <publish-dir>" >&2
    exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
manifest="$script_dir/../.github/native-manifest/${rid}.txt"
[ -f "$manifest" ] || { echo "REL-01: missing manifest $manifest" >&2; exit 2; }
[ -d "$dir" ] || { echo "REL-01: missing publish directory $dir" >&2; exit 2; }
dir="$(cd "$dir" && pwd)"

loaded=0
while IFS= read -r line; do
    entry="${line%%#*}"
    entry="${entry//[[:space:]]/}"
    [ -z "$entry" ] && continue
    file="$(find "$dir" -maxdepth 1 -name "$entry*" -print -quit)"
    [ -n "$file" ] || { echo "REL-01: no file for ${entry}*" >&2; exit 1; }

    echo "REL-01 load-probe: $(basename "$file")"
    if [[ "$rid" == win-* ]]; then
        pwsh -NoProfile -NonInteractive -Command '
          $path = $args[0]
          try {
            $handle = [System.Runtime.InteropServices.NativeLibrary]::Load($path)
            [System.Runtime.InteropServices.NativeLibrary]::Free($handle)
          } catch {
            Write-Error "NativeLibrary.Load failed for ${path}: $($_.Exception.Message)"
            exit 1
          }
        ' "$file"
    else
        LD_LIBRARY_PATH="$dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" python3 - "$file" <<'PY'
import ctypes
import sys
ctypes.CDLL(sys.argv[1])
PY
    fi
    loaded=$((loaded + 1))
done < "$manifest"

echo "REL-01: dynamically loaded all $loaded required natives from $dir."
