#!/usr/bin/env bash
# Builds libprojectM-4 from a disposable copy of the pinned source, installs it under
# External/projectm/<rid>/, and copies the pinned preset and texture packs next to it. The source
# comes from the local vendored snapshot (Reference/projectm-4.1.6) when present; otherwise - a
# fresh clone, CI - the pinned upstream release archive is downloaded and its SHA-256 verified, so
# the build is reproducible from any checkout (review P1-3). Print-and-done: export the
# MFP_PROJECTM_LIB line it
# prints (the ProjectMLib resolver probes that variable first, then the system library names).
#
# projectM is LGPL-2.1 (LICENSE.txt in the source tree). HaPlay links it DYNAMICALLY via
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
presets_revision="e03b83e3338d8f1ed6cbcf908c719f249ef24288"
textures_revision="ff8edf2a8fa07e55ad562f1af97076526c484f7d"
# Immutable source fallback (used when Reference/ is absent). 4.1.7 exists upstream but deliberately
# resets output to framebuffer 0, which conflicts with the render-to-bound-fbo patch below - retest
# that patch against 4.1.7 before bumping this pin.
projectm_archive_url="https://github.com/projectM-visualizer/projectm/releases/download/v4.1.6/libprojectM-4.1.6.tar.gz"
projectm_archive_sha256="1b9e6d56c59fe24e5416da4d42e941a34c982811003e43ac88b5aca8afa52c87"

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
    # No local vendored snapshot (fresh clone / CI): fetch the pinned upstream release archive and
    # verify its hash before trusting a single byte of it.
    download_dir="$repo_root/External/projectm/download"
    archive="$download_dir/libprojectM-4.1.6.tar.gz"
    extracted="$download_dir/libprojectM-4.1.6"
    if [[ ! -f "$extracted/CMakeLists.txt" ]]; then
        echo "== downloading pinned projectM source archive =="
        mkdir -p "$download_dir"
        curl -fsSL --retry 5 --retry-delay 5 --retry-all-errors --connect-timeout 30 \
            -o "$archive" "$projectm_archive_url"
        echo "$projectm_archive_sha256  $archive" | sha256sum -c -
        tar xzf "$archive" -C "$download_dir"
        [[ -f "$extracted/CMakeLists.txt" ]] || { echo "error: unexpected archive layout" >&2; exit 1; }
    fi
    vendor_src_dir="$extracted"
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

# Missing random-texture files are a valid runtime condition. projectM 4.1.6 returns an empty
# descriptor for it, but Empty() dereferences that descriptor's null texture. Keep the upstream
# snapshot pristine and fix the contract in the generated build tree.
if grep -Fq "return !m_texture || !m_sampler || m_texture->Empty();" \
    "$src_dir/src/libprojectM/Renderer/TextureSamplerDescriptor.cpp"; then
    echo "== null-safe texture-descriptor patch already present in source snapshot =="
else
    echo "== applying null-safe texture-descriptor patch =="
    patch -p1 -d "$src_dir" < "$repo_root/scripts/patches/projectm-null-safe-texture-descriptor.patch"
fi

# The vendored HLSL shader tokenizer's iss_strtod() strlen()s the whole remaining shader buffer to
# read one number; the buffer is length-bounded but not NUL-terminated at the scan point, so it
# over-reads into an unmapped page - an intermittent SIGSEGV in HLSLTokenizer::ScanNumber while
# loading otherwise-valid Milkdrop presets. Bound the parse to the numeric literal.
if grep -Fq "Bound the parse to the numeric literal" \
    "$src_dir/vendor/hlslparser/src/Engine.cpp"; then
    echo "== hlsl strtod bounds patch already present in source snapshot =="
else
    echo "== applying hlsl strtod bounds patch =="
    patch -p1 -d "$src_dir" < "$repo_root/scripts/patches/projectm-hlsl-strtod-bounds.patch"
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
        rm -rf "$presets_tmp"
        if find "$presets_dst" -type f -name '*.milk' -print -quit 2>/dev/null | grep -q .; then
            echo "WARN: preset pack refresh failed - keeping the currently installed presets" >&2
        else
            echo "WARN: preset pack fetch failed - falling back to the source tree's test presets" >&2
            rm -rf "$presets_dst"
            cp -r "$src_dir/presets" "$presets_dst"
            printf '%s\n' "fallback-projectm-4.1.6" > "$presets_stamp"
        fi
    fi
fi

# Textures: many presets use sampler_randXX and named images. Keep the companion projectM texture
# pack beside the preset directory; the managed visualizer discovers <install>/textures from either
# <install>/presets or a preset-pack subdirectory. A failed refresh keeps any previously installed
# pack; the native null-safety patch above still makes a genuinely texture-less install safe.
textures_dst="$install_dir/textures"
textures_stamp="$textures_dst/.mfp-source-revision"
installed_textures_revision="$(cat "$textures_stamp" 2>/dev/null || true)"
if [[ "$installed_textures_revision" != "$textures_revision" ]]; then
    echo "== fetching Milkdrop textures $textures_revision =="
    textures_tmp="$install_dir/textures.tmp.$$"
    rm -rf "$textures_tmp"
    if git init -q "$textures_tmp" \
        && git -C "$textures_tmp" remote add origin \
            https://github.com/projectM-visualizer/presets-milkdrop-texture-pack.git \
        && git -C "$textures_tmp" fetch -q --depth 1 origin "$textures_revision" \
        && git -C "$textures_tmp" checkout -q --detach FETCH_HEAD; then
        printf '%s\n' "$textures_revision" > "$textures_tmp/textures/.mfp-source-revision"
        cp "$textures_tmp/README.md" "$textures_tmp/textures/README.md"
        rm -rf "$textures_dst"
        mv "$textures_tmp/textures" "$textures_dst"
        rm -rf "$textures_tmp"
    else
        echo "WARN: texture pack fetch failed - presets will use placeholders for missing textures" >&2
        rm -rf "$textures_tmp"
    fi
fi

echo
echo "projectM built. Add this to your environment (fish: set -Ux):"
echo
echo "    export MFP_PROJECTM_LIB=\"$lib_dir\""
echo
echo "Presets (set as the visualizer's preset directory in HaPlay): $presets_dst"
echo "Textures (discovered automatically by HaPlay): $textures_dst"
