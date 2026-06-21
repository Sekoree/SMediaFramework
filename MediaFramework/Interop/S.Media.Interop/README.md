# S.Media.Interop — C ABI for MFPlayer

A NativeAOT shared library that exposes the media framework over a flat C ABI
(`[UnmanagedCallersOnly]`), so it can be driven from C, C++, Rust, Python (ctypes/cffi),
Go (cgo), etc. Because it ships as a native library, the framework's .NET garbage collector
runs entirely behind the boundary and never interferes with the host program's memory
management — the original motivation for this surface.

## Build

```sh
dotnet publish MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj \
    -c Release -r <rid> -p:PublishAot=true
```

`<rid>` is e.g. `linux-x64`, `win-x64`, or `osx-arm64`. The native library lands in
`bin/Release/net10.0/<rid>/publish/`:

| Platform | File |
|----------|------|
| Linux    | `s_media_player.so` |
| Windows  | `s_media_player.dll` |
| macOS    | `s_media_player.dylib` |

The native FFmpeg / PortAudio / SDL3 dependencies are copied alongside it.

The exported symbols can be confirmed with `nm -D --defined-only s_media_player.so | grep mfp_`.

## ABI

The full surface is declared in [`include/s_media_player.h`](include/s_media_player.h). In short:

- `mfp_initialize()` / `mfp_shutdown()` — call once at start / end.
- `mfp_open_file(path, with_video_window, audio_device_index, &handle)` — open a file; audio
  device `-1` = default, `-2` = no audio; video window opens an SDL window when the file has video.
- `mfp_play` / `mfp_pause` / `mfp_seek(handle, ticks)` — transport.
- `mfp_get_position_ticks` / `mfp_get_duration_ticks` / `mfp_get_state` / `mfp_is_ended` — queries.
- `mfp_close(handle)` — release the player.
- `mfp_last_error(buf, len)` — thread-local UTF-8 message for the last failed call.

Functions never throw across the boundary: they return `MFP_OK` (0) or a negative `MFP_ERR_*`
code. Time is in 100-ns ticks (10,000,000 = 1 second).

## Minimal C example

```c
#include "s_media_player.h"
#include <stdio.h>
#include <unistd.h>

int main(int argc, char** argv) {
    if (mfp_initialize() != MFP_OK) { char e[256]; mfp_last_error(e, sizeof e); fprintf(stderr, "%s\n", e); return 1; }

    mfp_player p = NULL;
    if (mfp_open_file(argv[1], /*video*/1, MFP_AUDIO_DEFAULT, &p) != MFP_OK) {
        char e[256]; mfp_last_error(e, sizeof e); fprintf(stderr, "open: %s\n", e); return 1;
    }

    mfp_play(p);
    while (!mfp_is_ended(p)) {
        int64_t pos = mfp_get_position_ticks(p), dur = mfp_get_duration_ticks(p);
        printf("\r%.1f / %.1f s", pos / 1e7, dur / 1e7); fflush(stdout);
        usleep(200000);
    }

    mfp_close(p);
    mfp_shutdown();
    return 0;
}
```

Link against the produced library (e.g. `cc demo.c -L. -ls_media_player` or `dlopen` at runtime).

## Scope

This first surface covers single-file playback (audio to a PortAudio device, optional SDL video
window) plus transport and state. It is intentionally small and additive: new entry points can be
appended without breaking existing callers (state/error enum values are append-only).
