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

# Presets: the source tree ships a small pack; copy it next to the lib for the UI's default preset dir.
presets_src="$src_dir/presets"
presets_dst="$install_dir/presets"
if [[ -d "$presets_src" && ! -d "$presets_dst" ]]; then
    echo "== copying bundled presets =="
    cp -r "$presets_src" "$presets_dst"
fi

echo
echo "projectM built. Add this to your environment (fish: set -Ux):"
echo
echo "    export MFP_PROJECTM_LIB=\"$lib_dir\""
echo
echo "Presets (set as the visualizer's preset directory in HaPlay): $presets_dst"
