#!/usr/bin/env bash
# REL-01: fail an artifact build when its published directory is missing a REQUIRED native library.
#
# Many native-staging steps are best-effort (continue-on-error / "WARN: not found" fallbacks), so a missing
# Windows/Linux native could otherwise be uploaded as a silently-broken release. This gate reads the per-RID
# manifest in .github/native-manifest/<rid>.txt and verifies every required library is present in the
# publish dir. Entries are PREFIX matches, so versioned sonames (libavcodec.so.62.6.100, avcodec-62.dll)
# still satisfy their base entry.
#
# Usage: scripts/check-native-manifest.sh <rid> <publish-dir>
set -uo pipefail

rid="${1:-}"
dir="${2:-}"
if [ -z "$rid" ] || [ -z "$dir" ]; then
    echo "usage: $0 <rid> <publish-dir>" >&2
    exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
manifest="$script_dir/../.github/native-manifest/${rid}.txt"

if [ ! -f "$manifest" ]; then
    echo "REL-01: no native manifest for RID '$rid' (expected $manifest)" >&2
    exit 2
fi
if [ ! -d "$dir" ]; then
    echo "REL-01: publish directory '$dir' does not exist" >&2
    exit 2
fi

missing=0
present=0
while IFS= read -r line; do
    entry="${line%%#*}"                       # strip trailing comment
    entry="${entry//[[:space:]]/}"            # strip all whitespace (filenames have none)
    [ -z "$entry" ] && continue
    # Prefix match: any file whose name starts with the entry satisfies it.
    if compgen -G "$dir/$entry*" > /dev/null; then
        present=$((present + 1))
    else
        echo "REL-01 MISSING required native for $rid: ${entry}*"
        missing=$((missing + 1))
    fi
done < "$manifest"

if [ "$missing" -ne 0 ]; then
    echo "REL-01: $rid artifact is missing $missing required native librar$([ "$missing" -eq 1 ] && echo y || echo ies) — failing the build." >&2
    exit 1
fi

echo "REL-01: all $present required natives present for $rid."
