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

probe_windows() {
    local kind="$1" path="$2"
    MFP_KIND="$kind" MFP_PATH="$(cygpath -w "$path")" pwsh -NoProfile -NonInteractive -Command '
      Add-Type -Namespace Probe -Name Native -MemberDefinition @"
        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError=true, CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern System.IntPtr LoadLibrary(string path);
        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError=true)]
        public static extern System.IntPtr GetProcAddress(System.IntPtr module, string name);
"@
      $module = [Probe.Native]::LoadLibrary($env:MFP_PATH)
      if ($module -eq [System.IntPtr]::Zero) { Write-Error "LoadLibrary failed for $env:MFP_PATH"; exit 2 }
      switch ($env:MFP_KIND) {
        "libass" {
          $fn = [Probe.Native]::GetProcAddress($module, "ass_library_version")
          if ($fn -eq [System.IntPtr]::Zero) { Write-Error "no ass_library_version export"; exit 1 }
          $del = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($fn, [System.Func[int]])
          $v = $del.Invoke()
          Write-Host ("libass runtime version: 0x{0:x8}" -f $v)
          if ($v -ge 0x01705000) { exit 0 } else { exit 1 }
        }
        "miniaudio" {
          $fn = [Probe.Native]::GetProcAddress($module, "ma_version")
          if ($fn -eq [System.IntPtr]::Zero) { Write-Error "no ma_version export"; exit 1 }
          $del = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($fn, [System.Action[System.IntPtr,System.IntPtr,System.IntPtr]])
          $buf = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(12)
          $del.Invoke($buf, [System.IntPtr]::Add($buf, 4), [System.IntPtr]::Add($buf, 8))
          $maj = [System.Runtime.InteropServices.Marshal]::ReadInt32($buf, 0)
          $min = [System.Runtime.InteropServices.Marshal]::ReadInt32($buf, 4)
          $rev = [System.Runtime.InteropServices.Marshal]::ReadInt32($buf, 8)
          [System.Runtime.InteropServices.Marshal]::FreeHGlobal($buf)
          Write-Host "miniaudio runtime version: $maj.$min.$rev"
          if ($maj -eq 0 -and $min -eq 11 -and $rev -eq 25) { exit 0 } else { exit 1 }
        }
        default { Write-Error "unknown probe kind $env:MFP_KIND"; exit 2 }
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
