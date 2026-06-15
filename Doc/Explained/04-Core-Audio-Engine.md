# 04 · Core Audio Engine

The audio engine is `AudioRouter` plus the source/output interfaces it connects and
the clip/voice primitives layered on top. It's an **N→M mixer**: any number of
sources summed into any number of outputs, with explicit per-route channel maps and
gains. It runs on one dedicated thread, allocation-free in steady state.

## The two interfaces (everything plugs into these)

### `IAudioSource` — pull-based producer

```csharp
AudioFormat Format { get; }
int ReadInto(float[] buffer);   // fill packed float32, return count written
bool IsExhausted { get; }
string Id { get; }
```

The router *pulls*: once per mix cycle it calls `ReadInto` on each routed source.
A source that doesn't have a full chunk returns a partial count (the router
silence-pads the rest). Implementers: FFmpeg decoders, NDI receivers, PortAudio
capture, `PcmBufferAudioSource` (in-memory PCM), the resampling wrappers.

Optional capabilities a source *may* also implement:

* `ISeekableSource` — jump to a timeline position. File decoders implement it; live
  sources (NDI, capture) don't.
* `ICooperativeAudioReadInterrupt` — observe a "please yield" flag and return
  promptly (usually 0 samples) so Pause/Dispose isn't blocked behind a long read.

### `IAudioOutput` — push-based consumer

```csharp
AudioFormat Format { get; }
void Submit(AudioFrame frame);   // accept samples already in this output's layout
```

The router *pushes* frames already mapped into the output's channel layout.
Implementers: `PortAudioOutput` (device), `NDIAudioOutput` (network), file muxers,
`DiscardingAudioOutput` (drops everything — placeholder/headless/test).

Optional output capabilities:

* `IClockedOutput` — "I can tell you my real consumption rate." This is the seam to
  the clock model: an output that owns hardware exposes `WaitForCapacity` so the
  router paces production to actual playback. (See [06](06-Clocks-and-AV-Sync.md).)
* `IFlushableOutput` — drop buffered audio downstream for immediate silence.
* `IAudioOutputChannelCapabilities` — channel-width limits and whether the output can
  renegotiate channel count in place.
* `IAudioOutputPlaybackStats` — device-level played/underrun counters.
* `IAdaptiveRateWrappedOutput` — marker for the FFmpeg adaptive-rate wrapper.

## `AudioRouter` — the mixer

The public surface is split across three partial files: `AudioRouter.cs` (core +
run loop + pump), `AudioRouter.Matrix.cs` (matrix/preset application),
`AudioRouter.Playback.cs` (host conveniences). Events are in `AudioRouterEvents.cs`.

### Building a graph

```csharp
using var router = new AudioRouter(sampleRate: 48000);
var srcId = router.AddSource(decoder.Audio, autoResample: true);
var outId = router.AddOutput(portAudioOutput);
router.Route(srcId, outId);              // identity map, gain 1.0
// or: router.AddRoute(srcId, outId, new ChannelMap([...]), gain);
// or: router.ApplyMatrix(srcId, outId, AudioChannelLayoutPresets.Downmix(6, 2));
router.SlaveTo(outId);                   // pace the loop to this output's clock
router.Play();
```

* **Source** = `AddSource` → stable id. Registering does **not** start draining it
  (so a cue can preload a clip before it's routed).
* **Output** = `AddOutput` → stable id, with an internal `OutputPump` (below).
* **Route** = an `AudioRoute` connecting `(sourceId, outputId)` with a mandatory
  `ChannelMap` and a `RouteGainSlot`. Multiple routes can target the same output —
  the output **sums** them. You can install **multiple routes per (source, output)
  pair** with explicit `routeId`s (this is how HaPlay's per-cell audio matrix puts
  one route per non-zero matrix cell). `Route(...)` is the identity shorthand;
  `RouteLast()` is the soundboard one-shot using the most recent source+output.
* **Gain** = `RouteGainSlot` holds a `Target` (set by `SetRouteGain`) and a `Current`
  (what the loop last applied). The loop ramps `Current`→`Target` each chunk so gain
  changes are click-free.
* **Matrix** = `ApplyMatrix` / `AudioMixPreset` install one route per non-zero cell
  and reconcile atomically on re-apply (add/remove/regain the diff).

### The mix loop — ELI5

The run loop lives on a dedicated `AboveNormal`-priority thread. Here's the cycle,
plain-language:

> Every "chunk" (a fixed number of samples, e.g. 480 = 10 ms @ 48 kHz):
> 1. **Take a photo of the graph.** The router keeps its whole state — sources,
>    outputs, routes — in one immutable `RouterState`. Any change (add a route, set a
>    gain) builds a *new* state and swaps it in atomically. The loop reads one photo
>    per chunk, so it never sees a half-applied change and never needs a lock.
> 2. **Read each source once.** Even if a source feeds five outputs, it's read once
>    into its scratch buffer (`ReadInto`). Short reads are silence-padded; a
>    misbehaving source that returns a bad count gets its chunk cleared (it can't
>    crash the loop).
> 3. **Clear each output's working buffer**, then **add every route's contribution**
>    into it via `ApplyRoute` (channel map + gain, accumulating). Five sources into
>    one output = five additive passes into that output's buffer.
> 4. **Hand each buffer to its pump** (`Commit`). The pump's drainer thread does the
>    actual `Submit`, so a slow output never blocks the mix.
> 5. If routes exist and *every* routed source is exhausted, the loop stops itself
>    (`CompletedNaturally`). With no sources, or sources but no routes yet, it keeps
>    running (silence) so dynamic "route-last" graphs aren't killed prematurely.

The actual mixing in step 3 (`ApplyRoute` → `ChannelMap.ApplyAdditive`) is the SIMD
cascade described in [03](03-Core-Data-Primitives.md): silent routes are skipped,
constant-gain routes take a fast path, and the matching vectorized accumulator runs.
The hot-path review measured this allocation-free with zero GC at 4K60.

### `OutputPump` — bounded async delivery + the backpressure rule

Each output gets an `OutputPump`: a bounded SPSC (single-producer/single-consumer)
queue of chunk buffers with a dedicated drainer thread that calls `IAudioOutput.Submit`.

* **Zero-copy:** the router mixes directly into the pump's `WorkingBuffer`. `Commit`
  publishes that buffer and rotates a fresh one in from a small free-pool. No copy on
  the producer thread.
* **Lazy threads:** the drainer `Thread` object is created at `AddOutput` but only
  `Start()`ed via `EnsureStarted()` when the router actually runs — creating OS
  threads for outputs whose router never starts was a source of thread pressure.
* **The drop rule (important):** when the free-pool is empty (consumer behind):
  * **Non-primary outputs** evict the oldest queued chunk and count the drop, so one
    slow output (a second device, an NDI sender) can never stall the shared router.
  * **The primary output** (the one wired via `SlaveTo`, which *is* the master clock)
    instead **waits briefly** for the drainer to recycle a buffer (bounded by
    `BackpressureCapMs = 1000`). Dropping a chunk on the master would skip audio
    *content* forward while the sample-counted clock keeps advancing — permanently
    desyncing A/V. The pump exists to absorb that jitter, not discard it. If the
    device is truly wedged, the cap lets it fall back to dropping so a dead device
    can't hang the router thread.

> This primary-output backpressure is the fix for the "AOT GC audio drops" bug:
> under NativeAOT scheduling the drainer could fall behind and the old code dropped
> master chunks, causing periodic drops and progressive desync. The rule above —
> *commit to the pump, pace the master, drop only on secondaries* — solved it.

### Pacing: `IRouterClock`

The loop's "when is the next chunk due?" comes from an `IRouterClock` (`WaitForNextChunk`):

* `WallClockRouterClock` — free-running deadline pacing (chunk every `chunkSamples /
  sampleRate` of wall time). The default when no output owns an authoritative clock.
* `OutputSlavedRouterClock` — defers to a specific output's `IClockedOutput`
  (`SlaveTo(outputId)`); falls back to wall clock if that output disappears, so
  removing the slaved output doesn't stall the loop.
* `PlaybackSlavedRouterClock` — paces from an `IPlaybackClock` media timeline (e.g.
  `NDIIngestPlaybackClock` for receive). `SlaveToIngest` wires this.

### Events & diagnostics

* `AudioRouter.PumpPressure` (`AudioRouterPumpPressureEventArgs`, a readonly record
  struct so sustained drops stay zero-alloc) — fires per drop with the output id and
  running total. Hosts react by backing off a route or surfacing a HUD warning.
* `OutputErrored` (`AudioRouterOutputErrorEventArgs`) — an output's `Submit` threw.
* `Faulted` (`AudioRouterFaultedEventArgs`) — the loop hit an unhandled error and
  stopped; the host decides policy (swap source, restart, surface to UI). A bad
  source read never crashes the host.
* `GetPumpStats(id)` / `GetAggregatePumpStats()` → `OutputPumpStats` /
  `AudioRouterAggregatePumpStats` for HUDs. Aggregate is *hints only* — it does not
  synthesize a coordinated master or drop policy across outputs (that's host-owned).
* `ChannelRouteMixProfiling` — opt-in scalar-vs-SIMD counters around the mix loop
  (`MF_MEDIA_PROFILE_CHANNEL_MAP=1`, or a test-scoped override). One volatile read
  per chunk when off.

### Multi-output drift (the honest limitation)

Only the `SlaveTo` output paces the router. Every *other* output runs off its own
physical crystal (PortAudio hardware, NDI's pace) and drifts at ~±50 ppm. Over
minutes this accumulates: a non-slaved output either fills and drops, or starves and
clicks. The framework deliberately does **not** provide a single coordinated master
PPM policy or lock-step drop/repeat across outputs. Mitigations are host-owned:
subscribe to `PumpPressure`; wrap a slow output in FFmpeg's `AdaptiveRateAudioOutput`;
or read `GetAggregatePumpStats`. See [06](06-Clocks-and-AV-Sync.md) and
`Doc/MediaFramework-Architecture.md`.

### Resampling at the source boundary

* `AudioRouterAutoResample` — process-wide hook; with `autoResample: true`,
  `AddSource` transparently wraps a source whose rate ≠ the router's nominal rate.
* `ResamplingAudioSource` / `SeekableResamplingAudioSource` (FFmpeg, doc 08) are the
  shipping implementations the hook installs.
* `PumpPressurePlaybackHintMonitor` — watches `PumpPressure` and maintains a bounded
  ppm hint (negative = "slow the master clock a touch so we produce fewer chunks
  ahead of a slow output"). Feeds the adaptive-rate wrapper.

## Clip / voice / bus primitives (the soundboard foundation)

These are the *low-level* building blocks the product-tier `Soundboard`
([11](11-Playback-Product-Tier.md)) is built on. They live in Core because they're
reusable engine pieces.

* **`AudioClip`** — an in-memory PCM clip, shared by many simultaneous plays (a
  soundboard pad triggered repeatedly). Load once, play many.
* **`AudioClipVoice`** — one playback cursor over a shared `AudioClip` buffer.
  **Zero heap allocations on read** after construction — designed for "fire a pad
  100 times" without GC churn. `AudioClipVoiceOptions` sets gain/loop/etc.
* **`AudioClipPlayer`** — a per-pad helper owning one `AudioClip` and triggering
  voices into an `AudioRouter`. `AudioClipPlayerMode` controls retrigger behaviour
  when a voice is already playing (restart / overlap / ignore).
* **`AudioBus`** — a sub-mix bus that is *both* an `IAudioOutput` (routes target it)
  and an `IAudioSource` (routes pull from it). Lets you build mixer chains like
  "drum group → comp → master out" without custom plumbing.
* **`PcmBufferAudioSource`** — a one-shot interleaved-PCM source (used when loading
  an `AudioClip`).
* **`DiscardingAudioOutput`** — accepts any format, drops everything. The quickstart
  placeholder and headless-test sink (`DiscardingAudioOutput.ForRouter(router)`).

> The voices being allocation-free on the hot read path is why a soundboard can fire
> dozens of overlapping one-shots without audio glitches — see the choke-group /
> reaping logic in `Soundboard` (doc 11).

Next: [05 · Core Video & Compositor Pipeline](05-Core-Video-Pipeline.md).
