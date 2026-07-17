#!/usr/bin/env bash
# REL-01 (review P0-1/P1-2/P1-3): make native version claims EXECUTABLE. Filename presence and even a
# successful dlopen do not prove the artifact contains the promised builds - query each layout- or
# security-sensitive library for its actual runtime version and fail the release when it lies:
#   libass     >= 0.17.5   (0.17.5 fixes two out-of-bounds writes; distro 0.17.1 must not ship)
#   miniaudio  == 0.11.25  (MALib hand-mirrors this exact ABI; anything else corrupts memory)
#   projectM   == 4.1.6    (custom bound-FBO build staged under External/projectm/<rid>/; Linux full tier)
#
# Usage: check-native-versions.sh <rid> <publish-dir>
set -euo pipefail

rid="${1:?usage: check-native-versions.sh <rid> <publish-dir>}"
dir="${2:?usage: check-native-versions.sh <rid> <publish-dir>}"
[ -d "$dir" ] || { echo "version-gate: missing publish directory $dir" >&2; exit 2; }
dir="$(cd "$dir" && pwd)"

fail=0

probe_linux() {
    LD_LIBRARY_PATH="$dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" python3 - "$@" <<'PY'
import ctypes, sys
kind, path = sys.argv[1], sys.argv[2]
lib = ctypes.CDLL(path)
if kind == "libass":
    v = lib.ass_library_version()
    print(f"libass runtime version: 0x{v:08x}")
    sys.exit(0 if v >= 0x01705000 else 1)
if kind == "miniaudio":
    maj = ctypes.c_uint32(); mnr = ctypes.c_uint32(); rev = ctypes.c_uint32()
    lib.ma_version(ctypes.byref(maj), ctypes.byref(mnr), ctypes.byref(rev))
    print(f"miniaudio runtime version: {maj.value}.{mnr.value}.{rev.value}")
    sys.exit(0 if (maj.value, mnr.value, rev.value) == (0, 11, 25) else 1)
if kind == "projectm":
    lib.projectm_get_version_string.restype = ctypes.c_char_p
    v = lib.projectm_get_version_string().decode()
    print(f"projectM runtime version: {v}")
    sys.exit(0 if v.startswith("4.1.6") else 1)
sys.exit(2)
PY
}

# The probe body lives in a compiled C# helper, not PowerShell marshaling:
# - Marshal.GetDelegateForFunctionPointer rejects GENERIC delegate types, so the previous
#   [Func[int]]/[Action[...]] approach threw (and the ma_version fallback then read garbage).
#   Real Cdecl delegate types fix that.
# - ass.dll's dependents (freetype/harfbuzz/fribidi/...) sit NEXT TO IT in the publish dir, which
#   is not in the probing process's DLL search path - SetDllDirectory + LOAD_WITH_ALTERED_SEARCH_PATH
#   resolve them from the probed DLL's own directory (same reason the REL-01 load-probe passes).
probe_windows() {
    local kind="$1" path="$2"
    MFP_KIND="$kind" MFP_PATH="$(cygpath -w "$path")" pwsh -NoProfile -NonInteractive -Command '
      Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
namespace Probe {
  public static class Native {
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectory(string path);
    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AssLibraryVersion();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void MaVersion(out uint major, out uint minor, out uint revision);

    const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    static IntPtr GetExport(string path, string name) {
      SetDllDirectory(System.IO.Path.GetDirectoryName(path));
      IntPtr module = LoadLibraryEx(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
      if (module == IntPtr.Zero)
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "LoadLibrary failed for " + path);
      IntPtr fn = GetProcAddress(module, name);
      if (fn == IntPtr.Zero)
        throw new EntryPointNotFoundException("no " + name + " export in " + path);
      return fn;
    }

    public static int AssVersion(string path) {
      return Marshal.GetDelegateForFunctionPointer<AssLibraryVersion>(GetExport(path, "ass_library_version"))();
    }

    public static uint[] MiniaudioVersion(string path) {
      uint maj, min, rev;
      Marshal.GetDelegateForFunctionPointer<MaVersion>(GetExport(path, "ma_version"))(out maj, out min, out rev);
      return new uint[] { maj, min, rev };
    }
  }
}
"@
      try {
        switch ($env:MFP_KIND) {
          "libass" {
            $v = [Probe.Native]::AssVersion($env:MFP_PATH)
            Write-Host ("libass runtime version: 0x{0:x8}" -f $v)
            if ($v -ge 0x01705000) { exit 0 } else { exit 1 }
          }
          "miniaudio" {
            $v = [Probe.Native]::MiniaudioVersion($env:MFP_PATH)
            Write-Host ("miniaudio runtime version: {0}.{1}.{2}" -f $v[0], $v[1], $v[2])
            if ($v[0] -eq 0 -and $v[1] -eq 11 -and $v[2] -eq 25) { exit 0 } else { exit 1 }
          }
          default { Write-Error "unknown probe kind $env:MFP_KIND"; exit 2 }
        }
      } catch {
        Write-Error $_.Exception.Message
        exit 2
      }
    '
}

check() {
    local kind="$1" path="$2"
    if [ ! -f "$path" ]; then
        echo "version-gate FAIL: $kind library not found at $path" >&2
        fail=1
        return
    fi
    if [[ "$rid" == win-* ]]; then
        probe_windows "$kind" "$path" || { echo "version-gate FAIL: $kind at $path" >&2; fail=1; }
    else
        probe_linux "$kind" "$path" || { echo "version-gate FAIL: $kind at $path" >&2; fail=1; }
    fi
}

if [[ "$rid" == win-* ]]; then
    check libass "$dir/ass.dll"
    check miniaudio "$dir/miniaudio.dll"
    # projectM is not yet staged on Windows (Linux-first policy); gate it once it is.
else
    check libass "$dir/libass.so"
    check miniaudio "$dir/libminiaudio.so"
    pm="$(find "$dir/External/projectm" -maxdepth 3 -name 'libprojectM-4.so*' -type f 2>/dev/null | head -1)"
    if [ -n "$pm" ]; then
        check projectm "$pm"
    else
        echo "version-gate FAIL: projectM library not staged under $dir/External/projectm (full tier requires it)" >&2
        fail=1
    fi
fi

if [ "$fail" -ne 0 ]; then
    echo "version-gate: one or more native version checks FAILED" >&2
    exit 1
fi
echo "version-gate: all native version checks passed."
