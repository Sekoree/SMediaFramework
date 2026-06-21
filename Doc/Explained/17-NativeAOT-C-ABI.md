# 17 · NativeAOT C-ABI (S.Media.Interop)

## The one-sentence version

`S.Media.Interop` compiles the framework into a **native shared library**
(`s_media_player.so` / `.dll` / `.dylib`) that exposes a flat **C API** (`mfp_*`
functions), so a program written in C, C++, Rust, Python, Go, … can drive media
playback without the .NET runtime ever touching *its* memory — the garbage
collector lives entirely behind the C boundary.

## Why it exists

The framework is great if you're writing C#. But two things motivate a C ABI:

1. **Other languages.** A flat C ABI is the lingua franca every language can call.
2. **GC isolation.** When the framework is a native library, its .NET GC manages
   only framework objects. A host program (game engine, DAW, Python app) keeps its
   own memory model; the two never interfere. (This was the original ask.)

It's built on the framework's existing **NativeAOT-readiness** (see
[07 · Triggers, Diagnostics & Runtime](07-Triggers-Diagnostics-Runtime.md)): the
whole engine already AOT-compiles, so wrapping it in a C surface is a thin layer,
not a rewrite.

## How a C API is even possible from C#

Three .NET features do the heavy lifting:

* **`[UnmanagedCallersOnly(EntryPoint = "mfp_…")]`** — marks a `static` method with
  a blittable signature so the AOT compiler exports it as a plain C symbol. That's
  the whole "make a C function" trick. (`NativeApi*.cs`.)
* **`GCHandle`** — pins a managed object and gives you an `IntPtr` to hand out as an
  opaque handle. Resolve it back with `GCHandle.FromIntPtr`. (`Handles.cs` wraps
  this so a garbage pointer returns an error instead of crashing.)
* **`NativeLib=Shared` + `PublishAot=true`** — the publish step that turns the
  managed DLL into the actual native shared library.

## The shape of the API

It deliberately mirrors how HaPlay itself is built — **building blocks**, not one
god-function:

```
mfp_initialize / mfp_shutdown                  ← FFmpeg + PortAudio once
mfp_open_file(...)                             ← convenience: file + default audio + window
mfp_player_open_file / _open_uri / _open_stream← graph mode: open with NO outputs
  mfp_player_video_router / _audio_router      ← reach the routers
  mfp_*_output_create (portaudio / sdl window) ← make outputs (you own them)
  mfp_video_router_add_output / _add_route     ← wire video
  mfp_audio_router_add_output / _connect       ← wire audio
mfp_play / _pause / _seek                      ← transport
mfp_player_set_event_callback                  ← C function-pointer events
mfp_portaudio_*_device_count / _get            ← device discovery
mfp_get_position/duration/state/is_ended       ← polling queries
mfp_last_error                                 ← thread-local message for the last failure
```

So a host can assemble any audio/video topology the same way HaPlay does: open a
player, grab its routers, create + attach outputs, route, play. (Full C example in
`MediaFramework/Interop/S.Media.Interop/README.md`; declarations in
`include/s_media_player.h`.)

## The four rules a C consumer must know

1. **Lifecycle.** `mfp_initialize()` once at the start, `mfp_shutdown()` once at the
   end.
2. **Errors never cross the boundary.** Every `int`-returning function returns
   `MFP_OK` (0) or a negative `MFP_ERR_*`; the message is in `mfp_last_error`. No
   exception or abort escapes into the host.
3. **Ownership.** A *player* owns its decoder + routers (freed by `mfp_close`).
   *Outputs you create* with a factory **you** own — free them with
   `mfp_output_destroy` *after* closing the player. Router handles are borrowed
   (freed with the player). The framework never disposes an output the host made.
4. **Events run on framework threads.** The callback (`MFP_EVENT_POSITION` / `ENDED`
   / `FAULTED`) fires on the clock/decode thread — the host must marshal to its own
   thread and must not call transport from inside it.

## What's there vs. planned

The **foundation** layer is implemented (graph + I/O + routing + PortAudio/SDL
outputs + device discovery + events + transport), builds, and AOT-publishes with
~35 exported `mfp_*` symbols. Planned next, as **building blocks only** (cues /
soundboards / control are intentionally left to host apps): live inputs (PortAudio
capture, NDI receiver) for `open_live`; NDI sender + file/encoder outputs; NDI
source discovery; and compositions (`ClipCompositionRuntime` layers + mapping).

## Build & verify

```sh
dotnet publish MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj \
    -c Release -r <rid> -p:PublishAot=true
nm -D --defined-only s_media_player.so | grep mfp_     # confirm the exports
```

See [02 · Native Bindings](02-Native-Bindings.md) for the *other* direction (the
framework calling native libs) and [11 · Playback Product Tier](11-Playback-Product-Tier.md)
for the `MediaPlayer` facade this ABI wraps.
