# 07 · Triggers, Diagnostics & Runtime

The connective tissue: how external control reaches the engine (`TriggerBus`), how
the framework boots and discovers optional backends (`MediaFrameworkRuntime` +
plugins), and how it reports what's happening (diagnostics + profiling).

## The `TriggerBus` — the universal remote-control socket

`TriggerBus` (`S.Media.Core.Triggers`) is a tiny, stable, allocation-free indirection
between "something happened" (a MIDI note, an OSC message, a script call, a UI click)
and "the engine does a thing." It's deliberately just a string-keyed handler table:

```csharp
public sealed class TriggerBus
{
    void Register(string triggerId, TriggerHandler handler);
    bool Unregister(string triggerId);
    bool Fire(string triggerId, in TriggerPayload payload = default); // false if no handler
}
public delegate void TriggerHandler(in TriggerPayload payload);
```

`Fire` is **non-throwing**: an unregistered id returns `false` rather than throwing,
so a controller mashing buttons for actions that aren't bound yet is harmless.

### `TriggerPayload` — allocation-free argument

A tagged union (`readonly record struct`) so the hot path (a fader streaming CC
values) never allocates:

* `TriggerValueKind` discriminates `None` / `Numeric` (a `double`) / `Text` (a short
  text tail for things like OSC addresses or note names).
* Designed for OSC/MIDI control where you want a number with maybe a small string,
  not a `params object[]` that boxes.

### `AudioTriggerRegistration`

Registers the standard clip triggers on a bus so a host doesn't hand-wire them:
`RegisterAudioClipPlayer(bus, id, player, router, outputId, …)` binds `{id}.fire`,
`{id}.stop`, `{id}.stopAll`, `{id}.loop`.

### Id naming convention

Use dot-separated paths so OSC addresses and MIDI maps line up with trigger ids:

| Pattern | Example | Meaning |
|---------|---------|---------|
| `pad.<name>.fire` | `pad.kick.fire` | one-shot clip |
| `pad.<name>.stop` / `.loop` | `pad.hat.loop` | stop / latched loop |
| `out.<id>.gain` | `out.ndi1.gain` | route/output gain (host binds) |
| `transport.play` | `transport.play` | session play (host binds) |
| `layer.<n>.opacity` | `layer.2.opacity` | compositor layer (host binds) |

### The bridges (live in the protocol libs)

The bus stays in Core; the protocol adapters live with their libraries:

* `OscTriggerBridge` (OSCLib) — fires `bus.Fire(oscAddress, payload)` using the OSC
  message address as the trigger id.
* `MidiTriggerBridge` + `MidiTriggerProfile` (PMLib) — map NoteOn / CC /
  ProgramChange to explicit ids.

So the data flow is: **controller → protocol lib → bridge → `TriggerBus.Fire` →
handler the host registered → engine call.** Bindings live in the host (HaPlay), not
in Core. `MediaPlayer.Triggers` exposes a per-player bus instance.

> **ELI5:** the `TriggerBus` is a switchboard. A MIDI pad doesn't know what "fire the
> kick drum" means; it just yells a label ("pad.kick.fire") into the switchboard. The
> host has plugged a wire from that label to the actual sound. Swap the wire and the
> same pad does something else — no engine change.

## Runtime bootstrap: `MediaFrameworkRuntime` + plugins

Core defines *contracts* but ships no FFmpeg/Skia/platform code. The runtime is how
those optional backends get installed at startup. (Recap of [01](01-Overview-and-Data-Flow.md)
with the actual types.)

```csharp
MediaFrameworkRuntime.Init()      // → MediaFrameworkRuntimeBuilder
    .UseFFmpeg()                  // installs decode/resample/swscale/yadif slots
    .UsePortAudio()               // ref-counted device lifetime
    .UseSkiaSharpImages()         // image + text source factories
    .UseNDI();                    // NDI runtime
// ... later, in long-lived hosts/tests:
MediaFrameworkRuntime.Shutdown(); // tears the slots back down
```

* **`MediaFrameworkRuntime`** — one-time, idempotent process-wide init. The fluent
  builder is `MediaFrameworkRuntimeBuilder`; each package contributes a `Use…()`
  extension (`MediaFrameworkRuntimeExtensions` in each backend).
* **`MediaFrameworkPlugins`** — the process-wide slots themselves: file/stream
  decode, audio resample-source wrapper, etc. The `Use…()` hooks populate these; Core
  code calls through them (e.g. `AddSource(..., autoResample:true)` reaches the
  resampler slot) without naming FFmpeg.
* **`MediaFrameworkExtensionRegistry`** — file-extension → source factory (e.g. `.png`
  → the Skia image source), so `VideoSource.OpenImage("slide.png")` resolves the right
  backend.
* **`VideoCpuFrameConverterRegistry` / `VideoDeinterlacerRegistry`** (in
  [05](05-Core-Video-Pipeline.md)) — the same pattern for pixel conversion /
  deinterlacing.

> **Why this matters:** this indirection is what lets the whole app publish as a
> 46 MB NativeAOT binary. The plugin slots are plain delegates, not reflection-based
> DI, so the trimmer/AOT compiler can see exactly what's used. (See the
> `nativeaot-status` note in project memory — ~6 reflection spots remain, all in JSON
> + code-behind bindings, and they warn but compile.)

## Diagnostics

* **`MediaDiagnostics`** — the framework-wide logging seam. `CreateLogger(category)`
  returns the configured `ILogger` (or a no-op); `LogError`/`LogWarning` are used at
  pipeline boundaries (subscriber failures, output errors, dispose failures).
  `SwallowDisposeErrors(action, context)` is the standard "dispose can't crash
  teardown" wrapper you see everywhere. By default it's quiet — a host plugs in real
  `Microsoft.Extensions.Logging` to see the chatter.
* **`INdiOverflowReporter`** — optional overflow counters for live NDI ingest
  (implemented by `NDISource`) so a host can detect a receiver falling behind.

## Profiling (opt-in, near-zero cost when off)

A recurring pattern: a static profiling class behind an env var that costs a single
`Volatile.Read` per item when disabled. Enable for a real workload, read the totals,
turn off.

* **`ChannelRouteMixProfiling`** (`MF_MEDIA_PROFILE_CHANNEL_MAP=1`) — scalar-vs-SIMD
  counters around the audio mix loop. Test-scoped overrides
  (`EnterTestRecordingScope` / `SetTestOverride`) keep parallel xUnit workers from
  sharing static counters.
* **`PassThroughArenaProfiling`** (FFmpeg, `MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1`) —
  counters for the pass-through descriptor arena (the pooled libav metadata arrays).
  It even counts failed Treiber-stack CAS attempts so you can tell whether lock
  contention is worth chasing; `PassThroughArenaSerialization`
  (`MF_MEDIA_PASS_THROUGH_ARENA_SERIALIZE=1`) swaps in a per-arena mutex when it is.
* **`Nv12Win32SharedHandleGpuUploadProfiling`** (OpenGL, `MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD=1`)
  — Windows NV12 GPU-upload timing.

## Cooperative shutdown

Long blocking operations (a slow decode, a paced network send) would otherwise make
Pause/Dispose hang. The framework's answer is *cooperative yielding*:

* **`ICooperativeAudioReadInterrupt` / `ICooperativeVideoReadInterrupt`** — a source
  observes a "please yield" flag and returns promptly (0 samples / no frame) so the
  router/decode thread can honor cancellation. The router raises/clears the yield flag
  around shutdown.
* **`CooperativePlaybackJoin`** — short-slice join helpers (`JoinThreadWhileCancelable`,
  `JoinThread(timeout)`) so callers honor a `CancellationToken` and never block
  unbounded on a wedged thread. You saw these in `MediaClock`, `AudioRouter`, and
  `VideoOutputPump` teardown.

## The internal playback-coordination types (in Core)

These live in `S.Media.Core/Playback` and coordinate combined A/V transport (also
referenced from [06](06-Clocks-and-AV-Sync.md)):

* `AvPlaybackCoordinator` — orders start/stop/seek across an audio side and video side.
* `IAvPlaybackSession` — the internal contract those facades implement.
* `MediaPlaybackSession` — bundles the video player + source (+ optional audio
  router/clock) for coordinated transport.
* `PauseFlushPolicy` — whether coordinated pause/seek runs a shared-mux libav flush
  after quiesce (deadlock-avoidance knob; see [08](08-FFmpeg-Decode-and-Encode.md)).

Next: [08 · FFmpeg Decode & Encode](08-FFmpeg-Decode-and-Encode.md).
