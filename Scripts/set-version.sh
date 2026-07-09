#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage:
  scripts/set-version.sh <major.minor[.patch]> [options]

Updates Directory.Version.props for all first-party projects. External/ projects
are not edited and do not import the root Directory.Build.props when they have
their own nearer Directory.Build.props.

Examples:
  scripts/set-version.sh 1.1
  scripts/set-version.sh 1.1.0 --pmlib-native 2.0.8
  scripts/set-version.sh 1.1 --from-system-libs
  scripts/set-version.sh 1.1 --dry-run

Options:
  --from-system-libs             Use installed native library versions when detectable.
                                  Explicit --*lib-native values override detection.
  --use-system-native-versions   Alias for --from-system-libs
  --pmlib-native <version>       PortMidi version used by PMLib (default: 2.0.8)
  --palib-native <version>       PortAudio version used by PALib (default: 19.7.0)
  --malib-native <version>       miniaudio version used by MALib (default: 0.11.25)
  --ndilib-native <version>      NDI SDK version used by NDILib (default: 6.3.2)
  --libasslib-native <version>   libass version used by LibAssLib (default: 0.17.5)
  --dry-run                      Print the generated props instead of writing it
  -h, --help                     Show this help
USAGE
}

fail() {
  printf 'set-version: %s\n' "$*" >&2
  exit 1
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
props_file="$repo_root/Directory.Version.props"

base_version=''
pmlib_native='2.0.8'
palib_native='19.7.0'
malib_native='0.11.25'
ndilib_native='6.3.2'
libasslib_native='0.17.5'
use_system_native_versions=false
pmlib_native_set=false
palib_native_set=false
malib_native_set=false
ndilib_native_set=false
libasslib_native_set=false
dry_run=false

is_dot_version() {
  [[ "$1" =~ ^[0-9]+(\.[0-9]+)*$ ]]
}

first_dot_version() {
  grep -Eo '[0-9]+(\.[0-9]+)+' | head -n 1
}

probe_pkg_config_version() {
  command -v pkg-config >/dev/null 2>&1 || return 1

  local package version
  for package in "$@"; do
    if pkg-config --exists "$package" 2>/dev/null; then
      version="$(pkg-config --modversion "$package" 2>/dev/null | first_dot_version || true)"
      if [[ -n "$version" ]] && is_dot_version "$version"; then
        printf '%s\n' "$version"
        return 0
      fi
    fi
  done

  return 1
}

probe_portmidi_version() {
  # PortMidi does not expose a runtime version function; package metadata is the only non-invasive source.
  probe_pkg_config_version portmidi PortMidi
}

probe_portaudio_version() {
  if command -v python3 >/dev/null 2>&1; then
    python3 <<'PY' && return 0
import ctypes
import ctypes.util
import sys


class PaVersionInfo(ctypes.Structure):
    _fields_ = [
        ("versionMajor", ctypes.c_int),
        ("versionMinor", ctypes.c_int),
        ("versionSubMinor", ctypes.c_int),
        ("versionControlRevision", ctypes.c_char_p),
        ("versionText", ctypes.c_char_p),
    ]


for name in ("portaudio", "libportaudio.so.2", "libportaudio.so", "portaudio.dll", "libportaudio.dylib"):
    path = ctypes.util.find_library(name) or name
    try:
        lib = ctypes.CDLL(path)
    except OSError:
        continue
    try:
        get_info = lib.Pa_GetVersionInfo
    except AttributeError:
        continue
    get_info.restype = ctypes.POINTER(PaVersionInfo)
    info = get_info()
    if info:
        v = info.contents
        print(f"{v.versionMajor}.{v.versionMinor}.{v.versionSubMinor}")
        sys.exit(0)

sys.exit(1)
PY
  fi

  probe_pkg_config_version portaudio-2.0 portaudio
}

probe_miniaudio_version() {
  if command -v python3 >/dev/null 2>&1; then
    python3 <<'PY' && return 0
import ctypes
import ctypes.util
import re
import sys


for name in ("miniaudio", "libminiaudio.so", "miniaudio.dll", "libminiaudio.dylib"):
    path = ctypes.util.find_library(name) or name
    try:
        lib = ctypes.CDLL(path)
    except OSError:
        continue

    try:
        version_string = lib.ma_version_string
    except AttributeError:
        version_string = None
    if version_string is not None:
        version_string.restype = ctypes.c_char_p
        raw = version_string()
        text = raw.decode("utf-8", "replace") if raw else ""
        match = re.search(r"\d+(?:\.\d+)+", text)
        if match:
            print(match.group(0))
            sys.exit(0)

    try:
        version = lib.ma_version
    except AttributeError:
        continue
    major = ctypes.c_uint32()
    minor = ctypes.c_uint32()
    revision = ctypes.c_uint32()
    version(ctypes.byref(major), ctypes.byref(minor), ctypes.byref(revision))
    print(f"{major.value}.{minor.value}.{revision.value}")
    sys.exit(0)

sys.exit(1)
PY
  fi

  probe_pkg_config_version miniaudio
}

probe_ndi_version() {
  if command -v python3 >/dev/null 2>&1; then
    python3 <<'PY' && return 0
import ctypes
import ctypes.util
import re
import sys


for name in ("ndi", "Processing.NDI.Lib.x64", "Processing.NDI.Lib.x86", "libndi.so.6", "libndi.so", "libndi.dylib"):
    path = ctypes.util.find_library(name) or name
    try:
        lib = ctypes.CDLL(path)
    except OSError:
        continue
    try:
        version = lib.NDIlib_version
    except AttributeError:
        continue
    version.restype = ctypes.c_char_p
    raw = version()
    text = raw.decode("utf-8", "replace") if raw else ""
    matches = re.findall(r"\d+(?:\.\d+)+", text)
    if matches:
        print(matches[-1])
        sys.exit(0)

sys.exit(1)
PY
  fi

  probe_pkg_config_version ndi libndi
}

probe_libass_version() {
  if version="$(probe_pkg_config_version libass)"; then
    printf '%s\n' "$version"
    return 0
  fi

  command -v python3 >/dev/null 2>&1 || return 1
  python3 <<'PY'
import ctypes
import ctypes.util
import sys


def bcd(byte):
    return ((byte >> 4) * 10) + (byte & 0x0F)


for name in ("ass", "libass.so", "libass.so.9", "ass.dll", "libass.dylib"):
    path = ctypes.util.find_library(name) or name
    try:
        lib = ctypes.CDLL(path)
    except OSError:
        continue
    try:
        get_version = lib.ass_library_version
    except AttributeError:
        continue
    get_version.restype = ctypes.c_int
    value = get_version()
    major = bcd((value >> 24) & 0xFF)
    minor = bcd((value >> 16) & 0xFF)
    patch = bcd((value >> 8) & 0xFF)
    print(f"{major}.{minor}.{patch}")
    sys.exit(0)

sys.exit(1)
PY
}

use_detected_native_version() {
  local name="$1"
  local explicit="$2"
  local current="$3"
  local probe="$4"
  local version=''

  if [[ "$explicit" == true ]]; then
    printf '%s\n' "$current"
    return 0
  fi

  if version="$("$probe")" && [[ -n "$version" ]] && is_dot_version "$version"; then
    printf 'Detected %s native version: %s\n' "$name" "$version" >&2
    printf '%s\n' "$version"
    return 0
  fi

  printf 'Using configured %s native version: %s (system version not detected)\n' "$name" "$current" >&2
  printf '%s\n' "$current"
}

while (($# > 0)); do
  case "$1" in
    --from-system-libs|--use-system-native-versions)
      use_system_native_versions=true
      shift
      ;;
    --pmlib-native)
      (($# >= 2)) || fail '--pmlib-native requires a value'
      pmlib_native="$2"
      pmlib_native_set=true
      shift 2
      ;;
    --palib-native)
      (($# >= 2)) || fail '--palib-native requires a value'
      palib_native="$2"
      palib_native_set=true
      shift 2
      ;;
    --malib-native)
      (($# >= 2)) || fail '--malib-native requires a value'
      malib_native="$2"
      malib_native_set=true
      shift 2
      ;;
    --ndilib-native)
      (($# >= 2)) || fail '--ndilib-native requires a value'
      ndilib_native="$2"
      ndilib_native_set=true
      shift 2
      ;;
    --libasslib-native)
      (($# >= 2)) || fail '--libasslib-native requires a value'
      libasslib_native="$2"
      libasslib_native_set=true
      shift 2
      ;;
    --dry-run)
      dry_run=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --*)
      fail "unknown option: $1"
      ;;
    *)
      [[ -z "$base_version" ]] || fail "unexpected argument: $1"
      base_version="$1"
      shift
      ;;
  esac
done

[[ -n "$base_version" ]] || { usage >&2; exit 2; }

if [[ "$base_version" =~ ^[0-9]+\.[0-9]+$ ]]; then
  base_version="${base_version}.0"
elif [[ ! "$base_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  fail "base version must be numeric major.minor or major.minor.patch, got '$base_version'"
fi

if [[ "$use_system_native_versions" == true ]]; then
  pmlib_native="$(use_detected_native_version PMLib "$pmlib_native_set" "$pmlib_native" probe_portmidi_version)"
  palib_native="$(use_detected_native_version PALib "$palib_native_set" "$palib_native" probe_portaudio_version)"
  malib_native="$(use_detected_native_version MALib "$malib_native_set" "$malib_native" probe_miniaudio_version)"
  ndilib_native="$(use_detected_native_version NDILib "$ndilib_native_set" "$ndilib_native" probe_ndi_version)"
  libasslib_native="$(use_detected_native_version LibAssLib "$libasslib_native_set" "$libasslib_native" probe_libass_version)"
fi

for native_version in "$pmlib_native" "$palib_native" "$malib_native" "$ndilib_native" "$libasslib_native"; do
  is_dot_version "$native_version" \
    || fail "native binding versions must be dot-separated numbers, got '$native_version'"
done

assembly_version="${base_version}.0"

generate_props() {
  cat <<EOF
<Project>
  <!-- Updated by scripts/set-version.sh. Root first-party projects import this through Directory.Build.props;
       External/ projects use their own props and are not stamped by this file. -->

  <PropertyGroup Label="MFPlayer Version">
    <MFPlayerVersion>${base_version}</MFPlayerVersion>
    <MFPlayerAssemblyVersion>${assembly_version}</MFPlayerAssemblyVersion>

    <Version>\$(MFPlayerVersion)</Version>
    <PackageVersion>\$(MFPlayerVersion)</PackageVersion>
    <AssemblyVersion>\$(MFPlayerAssemblyVersion)</AssemblyVersion>
    <FileVersion>\$(MFPlayerAssemblyVersion)</FileVersion>
    <InformationalVersion>\$(MFPlayerVersion)</InformationalVersion>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>

  <PropertyGroup Label="Native Binding Versions">
    <MFPlayerPMLibNativeVersion>${pmlib_native}</MFPlayerPMLibNativeVersion>
    <MFPlayerPALibNativeVersion>${palib_native}</MFPlayerPALibNativeVersion>
    <MFPlayerMALibNativeVersion>${malib_native}</MFPlayerMALibNativeVersion>
    <MFPlayerNDILibNativeVersion>${ndilib_native}</MFPlayerNDILibNativeVersion>
    <MFPlayerLibAssLibNativeVersion>${libasslib_native}</MFPlayerLibAssLibNativeVersion>
  </PropertyGroup>

  <PropertyGroup Condition="'\$(MSBuildProjectName)' == 'PMLib'">
    <MFPlayerNativeBindingName>PortMidi</MFPlayerNativeBindingName>
    <MFPlayerNativeBindingVersion>\$(MFPlayerPMLibNativeVersion)</MFPlayerNativeBindingVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'\$(MSBuildProjectName)' == 'PALib'">
    <MFPlayerNativeBindingName>PortAudio</MFPlayerNativeBindingName>
    <MFPlayerNativeBindingVersion>\$(MFPlayerPALibNativeVersion)</MFPlayerNativeBindingVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'\$(MSBuildProjectName)' == 'MALib'">
    <MFPlayerNativeBindingName>miniaudio</MFPlayerNativeBindingName>
    <MFPlayerNativeBindingVersion>\$(MFPlayerMALibNativeVersion)</MFPlayerNativeBindingVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'\$(MSBuildProjectName)' == 'NDILib'">
    <MFPlayerNativeBindingName>NDI</MFPlayerNativeBindingName>
    <MFPlayerNativeBindingVersion>\$(MFPlayerNDILibNativeVersion)</MFPlayerNativeBindingVersion>
  </PropertyGroup>
  <PropertyGroup Condition="'\$(MSBuildProjectName)' == 'LibAssLib'">
    <MFPlayerNativeBindingName>libass</MFPlayerNativeBindingName>
    <MFPlayerNativeBindingVersion>\$(MFPlayerLibAssLibNativeVersion)</MFPlayerNativeBindingVersion>
  </PropertyGroup>

  <PropertyGroup Condition="'\$(MFPlayerNativeBindingVersion)' != ''">
    <InformationalVersion>\$(MFPlayerVersion).\$(MFPlayerNativeBindingVersion)</InformationalVersion>
  </PropertyGroup>

  <ItemGroup Condition="'\$(MFPlayerNativeBindingVersion)' != ''">
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>NativeBinding</_Parameter1>
      <_Parameter2>\$(MFPlayerNativeBindingName)</_Parameter2>
    </AssemblyAttribute>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>NativeBindingVersion</_Parameter1>
      <_Parameter2>\$(MFPlayerNativeBindingVersion)</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
EOF
}

if [[ "$dry_run" == true ]]; then
  generate_props
  exit 0
fi

tmp="$(mktemp "${props_file}.XXXXXX")"
trap 'rm -f "$tmp"' EXIT
generate_props > "$tmp"
mv "$tmp" "$props_file"
chmod 0644 "$props_file"
trap - EXIT

printf 'Updated %s\n' "${props_file#$repo_root/}"
printf 'Base version: %s\n' "$base_version"
printf 'PMLib informational version: %s.%s\n' "$base_version" "$pmlib_native"
