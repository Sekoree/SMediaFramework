# S.Media.Interop — C ABI for MFPlayer

A NativeAOT shared library that exposes the media framework over a flat C ABI
(`[UnmanagedCallersOnly]`), so it can be driven from C, C++, Rust, Python (ctypes/cffi),
Go (cgo), etc. Because it ships as a native library, the framework's .NET garbage collector
runs entirely behind the boundary and never interferes with the host program's memory. Audio
output creation is backend-neutral through `mfp_audio_*`; the older `mfp_portaudio_*`
functions are kept as a PortAudio compatibility surface.

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

Native FFmpeg / PortAudio / SDL3 dependencies are copied alongside it. Confirm the exports with
`nm -D --defined-only s_media_player.so | grep mfp_`.

## Two ways to use it

The full surface is declared in [`include/s_media_player.h`](include/s_media_player.h).

### 1. Batteries-included (quick player)

`mfp_open_file(path, with_video_window, audio_device_index, &player)` opens a file *and* wires a
default audio device + optional SDL window in one call. Then `mfp_play` / `mfp_pause` / `mfp_seek` /
`mfp_close`.

### 2. Graph — build your own (like HaPlay)

Open with **no** outputs, then assemble your own pipeline from the framework's building blocks:
players, routers, output factories, routing, device discovery, events. This is what lets a host
build a HaPlay-style app in any language.

```c
#include "s_media_player.h"

mfp_initialize();

mfp_player p;
mfp_player_open_file("clip.mp4", &p);          /* no outputs wired */

/* --- video: route to an SDL window --- */
mfp_video_router vr = mfp_player_video_router(p);
char vin[64];  mfp_player_video_input_id(p, vin, sizeof vin);
mfp_output win; mfp_sdl_window_output_create("Program", 1280, 720, &win);
char vout[64]; mfp_video_router_add_output(vr, win, vout, sizeof vout);
mfp_video_router_add_route(vr, vin, vout);

/* --- audio: route to the default backend's default device --- */
mfp_audio_router ar = mfp_player_audio_router(p);   /* NULL if the file has no audio */
char src[64];  mfp_player_audio_source_id(p, src, sizeof src);
int rate = mfp_audio_router_sample_rate(ar);
mfp_output dev; mfp_audio_output_create(NULL, NULL, rate, 2, &dev);
char aout[64]; mfp_audio_router_add_output(ar, dev, aout, sizeof aout);
mfp_audio_router_connect(ar, src, aout, 1.0f);

mfp_play(p);
/* … run your loop / event callback … */

mfp_close(p);            /* tears down the player + routers (stops feeding the outputs) */
mfp_output_destroy(win); /* you created these, so you free them — after mfp_close */
mfp_output_destroy(dev);
mfp_shutdown();
```

### Events

Push-based via a C callback:

```c
void on_event(mfp_player p, int type, int64_t arg, void* user) {
    if (type == MFP_EVENT_ENDED) { /* play next */ }
    /* MFP_EVENT_POSITION: arg = ticks; MFP_EVENT_FAULTED: decode fault */
}
mfp_player_set_event_callback(p, on_event, /*user_data*/ NULL);
```

The callback fires on framework threads (clock / decode) — marshal to your own thread and don't call
transport from inside it.

### Audio backends and device discovery

Backend-neutral discovery uses backend names and opaque device ids. Pass `backend_name = NULL`
or `""` to use the default registered backend.

```c
int backends = mfp_audio_backend_count();
char backend[64];
for (int i = 0; i < backends; i++) {
    mfp_audio_backend_name(i, backend, sizeof backend);
}

int n = mfp_audio_device_count(NULL);      /* snapshots default-backend output devices */
for (int i = 0; i < n; i++) {
    int ch, is_default; double rate;
    char id[64], name[256];
    mfp_audio_device_get(i, &ch, &rate, &is_default, id, sizeof id, name, sizeof name);
    /* pass id to mfp_audio_output_create; id is backend-specific */
}
```

The legacy PortAudio-only device surface is still available for hosts that already store
PortAudio global device indices:

```c
int n = mfp_portaudio_output_device_count();      /* snapshots the list on this thread */
for (int i = 0; i < n; i++) {
    int idx, ch; double rate; char name[256];
    mfp_portaudio_output_device_get(i, &idx, &ch, &rate, name, sizeof name);
    /* idx is the device_index to pass to mfp_portaudio_output_create */
}
```

## Conventions

- `mfp_initialize()` once at start, `mfp_shutdown()` once at end.
- int-returning functions: `MFP_OK` (0) or negative `MFP_ERR_*`; `mfp_last_error(buf, len)` has the
  message (thread-local). Nothing throws across the boundary.
- Time is in 100-ns ticks (10,000,000 = 1 s).
- **Ownership:** a player owns its decoder + routers (freed by `mfp_close`). Outputs you create with a
  factory you own — free them with `mfp_output_destroy` *after* closing the player / removing them.
  Router handles are borrowed (freed with the player).

## Scope

Building blocks only: players, routers, I/O, device discovery, events, metrics. Higher-level
constructs (cues, soundboards, control mapping) are intentionally left to the host to build on top —
good candidates for separate addon libraries. Entry points and enum values are append-only.

### Roadmap

- **Foundation** (this layer): graph open, routers + routing, backend-neutral audio output creation, PortAudio/SDL compatibility outputs, device discovery, events.
- **Next:** live inputs (PortAudio capture, NDI receiver), NDI sender + file/encoder (recording) outputs, NDI source discovery.
- **Then:** compositions (layers, per-output mapping/warp).
