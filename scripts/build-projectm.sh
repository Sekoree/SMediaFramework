#!/usr/bin/env bash
# Builds libprojectM-4 from a disposable copy of the vendored source (Reference/projectm-4.1.6) for
# dev machines whose distro has no projectM 4.x package, installs it under External/projectm/<rid>/,
# and copies the pinned preset pack next to it. Print-and-done: export the MFP_PROJECTM_LIB line it
# prints (the ProjectMLib resolver probes that variable first, then the system library names).
#
# projectM is LGPL-2.1 (Reference/projectm-4.1.6/LICENSE.txt). HaPlay links it DYNAMICALLY via
# dlopen/P-Invoke and never statically embeds it - keep it that way when packaging.
#
# Requirements: cmake >= 3.21, a C++17 compiler, OpenGL headers (mesa), and glm (the vendored tree
# carries fallbacks for the rest via its vendor/ directory).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
vendor_src_dir="$repo_root/Reference/projectm-4.1.6"
src_dir="$repo_root/External/projectm/source/projectm-4.1.6"
build_dir="$repo_root/External/projectm/build"
projectm_eval_revision="da885dcdf33620ef26aa04cac9e215378b80252e"
presets_revision="a3ace8a82b0dd3233ccd59abf5bf634d88a0b07f"

case "$(uname -m)" in
    x86_64|amd64) arch="x64" ;;
    aarch64|arm64) arch="arm64" ;;
    *)
        echo "error: unsupported architecture: $(uname -m)" >&2
        exit 1
        ;;
esac
case "$(uname -s)" in
    Linux)  rid="linux-$arch" ;;
    Darwin) rid="osx-$arch" ;;
    MINGW*|MSYS*|CYGWIN*) rid="win-$arch" ;;
    *)
        echo "error: unsupported operating system: $(uname -s)" >&2
        exit 1
        ;;
esac
install_dir="$repo_root/External/projectm/$rid"

if [[ ! -f "$vendor_src_dir/CMakeLists.txt" ]]; then
    echo "error: projectM source not found at $vendor_src_dir" >&2
    exit 1
fi

# Always build from a fresh generated work tree. The patch and downloaded submodule must never alter
# Reference/: it is the auditable upstream snapshot and repeated builds must start from identical input.
echo "== preparing disposable projectM source tree =="
rm -rf "$src_dir" "$build_dir"
mkdir -p "$(dirname "$src_dir")"
cp -a "$vendor_src_dir" "$src_dir"
rm -rf "$src_dir/vendor/projectm-eval"

# The archive does not include git submodules. Fetch the exact projectm-eval revision recorded above
# rather than whatever happens to be the repository's default branch when this script runs.
echo "== fetching projectm-eval $projectm_eval_revision =="
git init -q "$src_dir/vendor/projectm-eval"
git -C "$src_dir/vendor/projectm-eval" remote add origin \
    https://github.com/projectM-visualizer/projectm-eval.git
git -C "$src_dir/vendor/projectm-eval" fetch -q --depth 1 origin "$projectm_eval_revision"
git -C "$src_dir/vendor/projectm-eval" checkout -q --detach FETCH_HEAD

# Offscreen-embedding patch: upstream hard-codes the final output to framebuffer 0 (the window),
# which renders black when projectM lives inside the compositor's private FBO.
if grep -q "render-to-bound-fbo" "$src_dir/src/libprojectM/ProjectM.cpp"; then
    echo "== render-to-bound-fbo patch already present in source snapshot =="
else
    echo "== applying render-to-bound-fbo patch =="
    patch -p1 -d "$src_dir" < "$repo_root/scripts/patches/projectm-render-to-bound-fbo.patch"
fi

echo "== configuring projectM 4.1.6 ($rid) =="
cmake -S "$src_dir" -B "$build_dir" \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_SHARED_LIBS=ON \
    -DENABLE_PLAYLIST=OFF \
    -DBUILD_TESTING=OFF \
    -DCMAKE_INSTALL_PREFIX="$install_dir"

echo "== building =="
jobs="$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 1)"
cmake --build "$build_dir" --parallel "$jobs"

echo "== installing to $install_dir =="
cmake --install "$build_dir"

# The resolver probes a DIRECTORY containing the library; point it at the lib output.
lib_dir="$install_dir/lib"
[[ -d "$install_dir/lib64" ]] && lib_dir="$install_dir/lib64"

# Presets: the source tree only carries minimal TEST presets; fetch the classic Milkdrop pack
# (552 presets, ~7 MB, projectM-visualizer/presets-milkdrop-original) as the real default.
presets_dst="$install_dir/presets"
presets_stamp="$presets_dst/.mfp-source-revision"
installed_presets_revision="$(cat "$presets_stamp" 2>/dev/null || true)"
if [[ "$installed_presets_revision" != "$presets_revision" ]]; then
    echo "== fetching classic Milkdrop presets $presets_revision =="
    presets_tmp="$presets_dst.tmp.$$"
    rm -rf "$presets_tmp"
    if git init -q "$presets_tmp" \
        && git -C "$presets_tmp" remote add origin \
            https://github.com/projectM-visualizer/presets-milkdrop-original.git \
        && git -C "$presets_tmp" fetch -q --depth 1 origin "$presets_revision" \
        && git -C "$presets_tmp" checkout -q --detach FETCH_HEAD; then
        rm -rf "$presets_tmp/.git" "$presets_dst"
        printf '%s\n' "$presets_revision" > "$presets_tmp/.mfp-source-revision"
        mv "$presets_tmp" "$presets_dst"
    else
        echo "WARN: preset pack fetch failed - falling back to the source tree's test presets" >&2
        rm -rf "$presets_tmp" "$presets_dst"
        cp -r "$src_dir/presets" "$presets_dst"
        printf '%s\n' "fallback-projectm-4.1.6" > "$presets_stamp"
    fi
fi

echo
echo "projectM built. Add this to your environment (fish: set -Ux):"
echo
echo "    export MFP_PROJECTM_LIB=\"$lib_dir\""
echo
echo "Presets (set as the visualizer's preset directory in HaPlay): $presets_dst"
