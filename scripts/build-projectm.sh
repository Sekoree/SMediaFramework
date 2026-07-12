#!/usr/bin/env bash
# Builds libprojectM-4 from the vendored source (Reference/projectm-4.1.6) for dev machines whose
# distro has no projectM 4.x package, installs it under External/projectm/<rid>/, and copies the
# bundled preset pack next to it. Print-and-done: export the MFP_PROJECTM_LIB line it prints (the
# ProjectMLib resolver probes that variable first, then the system library names).
#
# projectM is LGPL-2.1 (Reference/projectm-4.1.6/LICENSE.txt). HaPlay links it DYNAMICALLY via
# dlopen/P-Invoke and never statically embeds it - keep it that way when packaging.
#
# Requirements: cmake >= 3.21, a C++17 compiler, OpenGL headers (mesa), and glm (the vendored tree
# carries fallbacks for the rest via its vendor/ directory).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src_dir="$repo_root/Reference/projectm-4.1.6"
build_dir="$repo_root/External/projectm/build"

case "$(uname -s)" in
    Linux)  rid="linux-x64" ;;
    Darwin) rid="osx-$(uname -m | sed 's/x86_64/x64/;s/arm64/arm64/')" ;;
    *)      rid="win-x64" ;;
esac
install_dir="$repo_root/External/projectm/$rid"

if [[ ! -f "$src_dir/CMakeLists.txt" ]]; then
    echo "error: projectM source not found at $src_dir" >&2
    exit 1
fi

# The vendored tree was copied without git submodules; projectm-eval (the Milkdrop expression
# compiler) is required. Fetch it once when the directory is empty.
if [[ ! -f "$src_dir/vendor/projectm-eval/CMakeLists.txt" ]]; then
    echo "== fetching missing projectm-eval submodule =="
    git clone --depth 1 https://github.com/projectM-visualizer/projectm-eval.git \
        "$src_dir/vendor/projectm-eval"
fi

# Offscreen-embedding patch: upstream hard-codes the final output to framebuffer 0 (the window),
# which renders BLACK when projectM lives inside the compositor's private FBO. Idempotent.
if ! grep -q "render-to-bound-fbo" "$src_dir/src/libprojectM/ProjectM.cpp"; then
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
cmake --build "$build_dir" --parallel "$(nproc 2>/dev/null || sysctl -n hw.ncpu)"

echo "== installing to $install_dir =="
cmake --install "$build_dir"

# The resolver probes a DIRECTORY containing the library; point it at the lib output.
lib_dir="$install_dir/lib"
[[ -d "$install_dir/lib64" ]] && lib_dir="$install_dir/lib64"

# Presets: the source tree only carries minimal TEST presets; fetch the classic Milkdrop pack
# (552 presets, ~7 MB, projectM-visualizer/presets-milkdrop-original) as the real default.
presets_dst="$install_dir/presets"
if [[ ! -d "$presets_dst" ]]; then
    echo "== fetching the classic Milkdrop preset pack =="
    if git clone --depth 1 https://github.com/projectM-visualizer/presets-milkdrop-original.git "$presets_dst.tmp"; then
        rm -rf "$presets_dst.tmp/.git"
        mv "$presets_dst.tmp" "$presets_dst"
    else
        echo "WARN: preset pack fetch failed - falling back to the source tree's test presets" >&2
        cp -r "$src_dir/presets" "$presets_dst"
    fi
fi

echo
echo "projectM built. Add this to your environment (fish: set -Ux):"
echo
echo "    export MFP_PROJECTM_LIB=\"$lib_dir\""
echo
echo "Presets (set as the visualizer's preset directory in HaPlay): $presets_dst"
