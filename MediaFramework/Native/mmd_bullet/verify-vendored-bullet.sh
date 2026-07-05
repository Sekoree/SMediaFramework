#!/usr/bin/env bash
# Verifies that External/bullet3 is a faithful vendored copy of upstream Bullet 3.25: it must differ from
# the upstream tag ONLY by patches/constraint-fix.patch (i.e. exactly one file — btGeneric6DofConstraint.cpp).
# Sparse-clones upstream (needs network), diffs the three vendored libraries, and fails loudly on any
# unexpected drift. Run after a Bullet bump or when auditing the vendor. Not part of the build.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$here/../../.."
vendored="$root/External/bullet3/src"
patchfile="$root/External/bullet3/patches/constraint-fix.patch"
tag="3.25"
expected_commit="2c204c49e56ed15ec5fcfa71d199ab6d6570b3f5"
allowed="BulletDynamics/ConstraintSolver/btGeneric6DofConstraint.cpp"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "cloning bullet3 tag $tag (sparse: src only)…"
git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$tag" --filter=blob:none --sparse \
    https://github.com/bulletphysics/bullet3 "$tmp/upstream"
git -C "$tmp/upstream" sparse-checkout set src >/dev/null 2>&1
actual_commit="$(git -C "$tmp/upstream" rev-parse HEAD)"
[[ "$actual_commit" == "$expected_commit" ]] || \
    echo "WARN: upstream tag $tag is at $actual_commit, VENDORING.md records $expected_commit" >&2
up="$tmp/upstream/src"

problems=0

# 1. Files present upstream inside the three vendored libs but ABSENT locally (a trim regression).
for d in LinearMath BulletCollision BulletDynamics; do
    while IFS= read -r f; do
        [[ -z "$f" ]] && continue
        [[ -f "$vendored/$d/$f" ]] || { echo "MISSING from vendored: $d/$f"; problems=$((problems + 1)); }
    done < <(cd "$up/$d" && find . -type f | sed 's|^\./||')
done

# 2. Content differences must be exactly {btGeneric6DofConstraint.cpp}.
for d in LinearMath BulletCollision BulletDynamics; do
    while IFS= read -r rel; do
        [[ -z "$rel" ]] && continue
        if [[ "$rel" == "$allowed" ]]; then
            echo "OK (expected patch): $rel"
        else
            echo "UNEXPECTED MODIFICATION: $rel"; problems=$((problems + 1))
        fi
    done < <(diff -rq "$vendored/$d" "$up/$d" 2>/dev/null \
                | sed -n "s|^Files $vendored/\(.*\) and .* differ$|\1|p")
done

# 3. The sole allowed diff must reproduce exactly from pristine upstream + the tracked patch.
work="$tmp/check"; mkdir -p "$work/$(dirname "$allowed")"
cp "$up/$allowed" "$work/$allowed"
( cd "$work" && git apply -p1 "$patchfile" )
diff -q "$work/$allowed" "$vendored/$allowed" >/dev/null \
    || { echo "MISMATCH: upstream + constraint-fix.patch != vendored $allowed"; problems=$((problems + 1)); }

if [[ "$problems" -ne 0 ]]; then
    echo "FAIL: $problems drift issue(s) — vendored Bullet is not a clean $tag + constraint-fix.patch" >&2
    exit 1
fi
echo "OK: External/bullet3 == upstream Bullet $tag, modified ONLY by patches/constraint-fix.patch"
