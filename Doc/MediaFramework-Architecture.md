# MediaFramework architecture notes

Companion to the inline XML `<remarks>` on the framework's core orchestration types. The source docs cover the day-to-day surface; the longer-form discussion below is split out so IDE hover stays terse.

Types covered here:
- [`AudioRouter`](#audiorouter) — graph routing & per-output pumps
- [`MediaContainerSession`](#mediacontainersession) — FFmpeg-aware playback session pairing
- [`MediaContainerPlaybackBundle`](#mediacontainerplaybackbundle) — single-`using` lifetime owner

---

## AudioRouter

`S.Media.Core.Audio.AudioRouter` (MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs)

### Route-mix profiling (advanced)

The router can record scalar vs. SIMD fast-path counters around its channel-mix loop. Two ways to enable:

- Set environment variable `MF_MEDIA_PROFILE_CHANNEL_MAP=1` before process start — global recording across the whole app. Cheapest when you want one-time numbers from a real workload.
- Programmatically, call `ChannelRouteMixProfiling.SetTestOverride(...)` inside a `ChannelRouteMixProfiling.EnterTestRecordingScope()` `using` block — keeps parallel test workers from sharing counters.

Read the totals out of `ChannelRouteMixProfiling` (see static class for the exact members). Profiling is fully off when neither knob is set; the run loop pays only a single `Volatile.Read` per chunk in that case.

### Multi-output drift (in depth)

When more than one output is attached, only the output wired via `SlaveTo` paces the router via its `IClockedOutput.WaitForCapacity`. All other outputs (a second PortAudio device, an NDI sender, …) run off their own physical clock — PortAudio's hardware crystal, NDI's internal pace, etc.

These secondary clocks drift relative to the master at typical hardware tolerances around ±50 ppm. Over minutes-to-hours this accumulates: the non-slaved output's pump either runs ahead (fills up, then drops oldest chunks once `pumpCapacityChunks` is exceeded) or falls behind (empties out, the output starves and the application hears clicks / sees frame stalls).

Recommended host-side mitigations:

1. **Subscribe to `AudioRouter.PumpPressure`** and react when `Dropped` increments on a specific output id — back off the route, lower its gain, or fan out a "we're dropping audio" diagnostic.
2. **Wrap the slow output in `AdaptiveRateAudioOutput`** (FFmpeg). The wrapper runs a tiny per-output `swresample` driven by `PumpPressurePlaybackHintMonitor` filtered to that output id. It applies a few-ppm rate tweak only on the path into that output, leaving the router's nominal graph rate unchanged.
3. **`GetAggregatePumpStats`** sums every per-output `OutputPumpStats` into a single aggregate suitable for HUD overlays or periodic logging. It does *not* synthesize a global master clock or coordinated drop policy — those are intentionally not provided. Hosts that need coordinated drop/repeat across multiple outputs must implement it on top of these primitives.

### What's not implemented

The router deliberately omits:

- A single coordinated **master** clock ppm policy (every output paced to the slowest one). The current model is "one slaved output + everyone else drifts" + per-output adaptive resampling.
- Synchronized **drop / repeat** across outputs. Each output's pump drops oldest independently. If you need lock-step gap insertion across two outputs, you'll need to build it on top of `PumpPressure` events.

---

## MediaContainerSession

`S.Media.FFmpeg.MediaContainerSession` (MediaFramework/Media/S.Media.FFmpeg/MediaContainerSession.cs)

### The `flushSharedMuxAfterPause` argument

`Pause(CancellationToken, Action?)` and `SeekCoordinated(TimeSpan, CancellationToken, Action?)` both take an optional `Action? flushSharedMuxAfterPause` argument. When omitted (or passed `null`, which is `??`-coalesced), it defaults to `MediaContainerDecoder.FlushCodecPipelines`.

The default is fine for the common case (single decode thread, no other consumers reaching into libav). It can deadlock when:

- The video decode thread is still inside libav (`avcodec_send_packet` / `avcodec_receive_frame`) when pause fires.
- The flush implementation then tries to take the same demux locks the decode thread is holding.

For those situations use the dedicated skip-flush helpers:

- `PauseSkippingSharedMuxFlush` — bypasses the flush entirely.
- `SeekCoordinatedSkippingSharedMuxFlush` — same for coordinated seek.

If you need a custom no-op without depending on the helpers, pass `static () => { }` to the `Action?` parameter.

---

## MediaContainerPlaybackBundle

`S.Media.FFmpeg.MediaContainerPlaybackBundle` (MediaFramework/Media/S.Media.FFmpeg/MediaContainerPlaybackBundle.cs)

### Example: wiring `Tools/VideoPlaybackSmoke`

The smoke tool is the canonical wiring example for "one bundle owns the whole graph until `finally`":

1. Build GL / NDI / PortAudio outputs (each lives on whatever assembly it belongs to — `S.Media.SDL3`, `S.Media.NDI`, `S.Media.PortAudio`).
2. Open the container: `using var decoder = MediaContainerDecoder.Open(path, opts);`
3. Build a `VideoPlayer`, optional `VideoRouter`, and a freerun `MediaClock`.
4. Wrap the lot in a `MediaContainerPlaybackBundle` with `SmokeToolDefaultOwnership` so the bundle disposes (in order) `VideoPlayer → AudioPlayer → VideoRouter → freerun clock → decoder`.
5. The PortAudio device is wired through `S.Media.PortAudio.PortAudioPlaybackHost` using `PortAudioPlaybackHostPlayerOwnership.CallerDisposesPlayer` so the bundle's finally-block can tear down the mux + audio player before the PortAudio host's outer `using` closes the device.

For ownership flags that match the smoke tool exactly, use the `SmokeToolDefaultOwnership` static. For the equivalent wiring driven from `S.Media.Playback.MediaPlayer`, use `DefaultBundledHostOwnership`.

If you only need a single-file playback path with router + optional outputs (no SDL / NDI / PortAudio), prefer the **`MediaPlayer` builder** — it's the library-side entry that returns a configured player with no host-platform assemblies referenced:

```csharp
using var player = await MediaPlayer.OpenFile(path).OpenAsync();
// or: MediaPlayer.OpenFile(path).TryBuild(out var player, out var error);
```

Also: `MediaPlayer.OpenUri`, `OpenStream` (AVIO when `SpoolStreamToDisk` is false), `Open(decoder)`, and `OpenLive(audio, video)` for capture/mock graphs. Operational snapshots: `MediaPlayer.GetMetrics()`; script hooks: `MediaPlayer.Triggers` (`TriggerBus`). See [MediaFramework-Quickstart.md](MediaFramework-Quickstart.md) and [MediaFramework-Triggers.md](MediaFramework-Triggers.md).
