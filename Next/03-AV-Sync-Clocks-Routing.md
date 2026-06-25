# 03 — A/V Sync, Clocks & Routing

The headline requirement: **near-perfect A/V sync with multiple inputs (file + live: NDI, mic/line)
and multiple outputs.** Today this works well for files but live uses a *separate, master-less* path
(the NDI ingest clock is discarded), which is why live sync keeps needing rework. The rewrite
converges everything onto **one** model.

## 1. The model in one paragraph

Each **transport group** (a cue, or a set fired/seeked together) has exactly one **master clock**
(`SessionClock`); a session runs one or more groups concurrently. Within a group, every source — file
or live — is presented through a **`SourceTimeline`** that maps source PTS onto master time via a fixed
**offset** and a **rebase policy**. Correlated streams from one sender/device (for example NDI video +
NDI audio, or camera + embedded audio) live in a **`SourceSyncGroup`** so their shared sender timeline
and per-stream correction offsets are adjusted together. Audio for the master bus drives the clock
(lowest-latency reference); video and all other outputs **present scheduled against master time**.
Multiple inputs are `SourceTimeline`s grouped where needed; multiple outputs are kept phase-locked by
an **output sync group**. There is no second path for live — live is "a source whose rebase policy is
*holdback + rebase-to-latest* instead of *scheduled*."

```
   sources                 timelines / source groups        mix/composite        outputs (scheduled)
  ┌─────────┐   PTS      ┌──────────────────────────┐                          ┌──────────────────┐
  │ file A  │──────────► │ Scheduled  off=0          │──►┐                  ┌──►│ SDL3 window      │
  ├─────────┤            ├──────────────────────────┤   │  AudioRouter     │   ├──────────────────┤
  │ NDI in  │──────────► │ SourceSyncGroup           │──►┼─►(N→M matrix)──► ├──►│ PortAudio device │
  ├─────────┤            ├──────────────────────────┤   │  VideoRouter +   │   ├──────────────────┤
  │ mic in  │──────────► │ Holdback (audio only)     │──►┘  Compositor      └──►│ NDI sender       │
  └─────────┘            └──────────────────────────┘                          └──────────────────┘
                                     ▲                                                   ▲
                                     │                 ┌───────────────┐                 │
                                     └─────────────────│  SessionClock │─────────────────┘
                                        master time    │   (master)    │   present @ master
                                                        └───────────────┘
                                                         ▲ drives from master audio bus
```

## 2. Clocks (`S.Media.Time`)

Salvage the existing clock types — they're the right primitives — and add two concepts that make the
model uniform.

- **`IPlaybackClock` / `IMediaClock` / `IPlayhead`** — unchanged contracts (`MediaClock`,
  `CompositePlaybackClock`, `VideoPtsClock`). `IMediaClock.VideoTick` remains the heartbeat that
  `IVideoOutput.Submit` runs on.
- **`SessionClock` (new, per-group master).** One per transport group (see [08](08-Open-Decisions.md) D4).
  Wraps whichever reference advances time:
  - *File-led group:* the master audio output (a clocked PortAudio/miniaudio device) advances it —
    same as today's "audio is master."
  - *Live-led group (no file):* a free-running monotonic wall clock advances it, disciplined by the
    chosen live master's arrival cadence (PI controller on long-term drift, not per-frame).
  - Each group picks its reference when it starts; when its master output stops, the group idles
    (mastership never floats between unrelated sources). Switching a group's reference is an explicit,
    debounced op.
- **`SourceTimeline` (new).** Per source: `Offset` (TimeSpan, signed) + `RebasePolicy`:
  - **`Scheduled`** — source PTS is authoritative; frame is shown when `master ≥ pts + offset`. Used
    for files and exact-rate live senders. Gives perfect lip-sync.
  - **`Holdback`** — keep a bounded jitter buffer; emit the frame whose rebased PTS brackets master.
    Used for live where the sender's clock ≠ ours.
  - **`RebaseToLatest`** — collapse accumulated drift by snapping the newest frame to "now" when the
    buffer over/underflows beyond a threshold (today's NDI `RebaseToLatest`, but now *expressed as a
    timeline policy against the master*, not a discarded clock).
  - `Offset` absorbs known phase error as a **first-class per-source control** (replacing the
    `HAPLAY_LIVE_AV_SYNC_OFFSET_MS` env hack and the `DelayedVideoSource` shim).
- **`SourceSyncGroup` (new).** Groups related `SourceTimeline`s that originate from the same live
  sender/device or demux session. The group owns the sender-to-session rebase and drift estimate;
  each member timeline has only a small correction offset (for known A/V phase error, capture device
  latency, or manual trim). This avoids treating NDI video and NDI audio as unrelated clocks.

> **Why this fixes the NDI saga.** The recurring live-desync work (memory: `ndi_input_av_sync`,
> `avsync_playback_review`) all stems from live video/audio not sharing a master and not preserving
> their sender-side relationship. Here they do: NDI video and NDI audio are timelines in one
> `SourceSyncGroup` over the same `SessionClock`, so their relative phase is observable and correctable
> instead of invisible. The "reduce audio keep + keep video LatestOnTick + optional trim" tuning
> becomes: group `Holdback` keep-size + per-stream `Offset` — set in one place, testable headless.

## 3. Routing (`S.Media.Routing`)

Unchanged in spirit — these are battle-tested — just moved out of `Core`.

- **`AudioRouter`** — the N→M mixing matrix. Pulls each source chunk at the mixer cadence, applies
  `ChannelMap` (SIMD), sums into output buses, submits to each `IAudioOutput`. Router clocks
  (`OutputSlaved`/`PlaybackSlaved`/`WallClock`) integrate with `SessionClock`: the master bus's
  clocked output *is* the session reference; non-master outputs get adaptive-rate resampling to avoid
  drift (today's `EnableAdaptiveRateOnNonMasterOutputs`, now wired via the registry's resampler
  factory, not a static slot).
- **`VideoRouter`** — fan-out to N video outputs with per-branch pixel-format negotiation
  (`VideoFormatNegotiator` + `VideoOutputFanoutFormats`), per-branch converters for branches that
  can't take the primary format, `VideoOutputPump` for slow outputs (NDI/file). Keep the idempotent
  `Configure`/re-fan behavior (memory: `video_sink_pump_reconfigure`).

## 4. Multiple inputs

A session holds an ordered set of `(IVideoSource | IAudioSource, SourceTimeline)` plus optional
`SourceSyncGroup`s for correlated streams.

- **Audio inputs** (file tracks, mic/line via `IAudioBackend.CreateInput`, NDI audio) are sources on
  the `AudioRouter`. Multi-track files expose multiple audio sources; selection (none/one/many) picks
  which are added (see §6).
- **Video inputs** (file, NDI receiver, capture) become **compositor layers** when a composition is
  active, or route directly to outputs in single-source playback. Each carries its own
  `SourceTimeline`, so a live camera and a file clip stay in sync on the same canvas.
- **Grouped live inputs** (NDI A/V, capture device A/V, future multi-sensor devices) share a
  `SourceSyncGroup`. Group-level rebase tracks the sender/device; per-stream offsets handle known
  capture or processing delays.
- **Mixed file + live** is the normal case, not a special one: a `Scheduled` file timeline and a
  `Holdback` NDI timeline coexist under one master.

## 5. Multiple outputs

- **Fan-out:** `VideoRouter`/`AudioRouter` already send one stream to many outputs.
- **Phase-lock:** `OutputSyncGroup` (audio) and `VideoPresentSyncGroup` (video) keep multiple
  outputs presenting the same master instant together — this is your "Option B / stitched outputs"
  (memory: `multi_output_drift_correction`). In the new model these are **the** answer for combined /
  stitched walls, promoted from optional to the default for any multi-output binding.
- **Combined-into-one-composition** (a single canvas spanning several physical outputs) is handled in
  the compositor via `CompositeMulti` (one composite → N warped outputs), with the outputs in a sync
  group so they never tear relative to each other. See [04](04-Compositor-Warp-GPU.md).
- **Per-output rate:** keep the deliberate oversample-for-local, dedup-for-NDI behavior
  (memory: `haplay_output_2x_fps_by_design`) — but make it a per-binding policy on the output map,
  not an env var.
- **Master output + mix rate (D11):** each transport group designates one **master output** (default:
  the first clocked device) — the group's clock source (D4). It mixes internally at that device's native
  rate and resamples sources on ingress, so the `portaudio_default_device_rate_desync` class can't occur.

## 6. Audio channel remap & multi-track selection

- **Remap** is the `AudioRouter` matrix + `ChannelMap` (already SIMD-accelerated). The UI's
  `AudioMatrix`/`AudioDownmixPresets`/`VirtualAudioChannelAssignment` models become a `RoutingScene`
  in `S.Media.Session`, serialized with the show.
- **Multi-track**: decoder providers enumerate audio tracks (`AudioTrackInfo[]`; `Decode.FFmpeg` is
  the built-in provider). Open options take a selection: `None` (video-only), one track, or several
  tracks (each a source on the router — e.g. separate language stems to separate buses). Subtitles use
  the same select-none/one/many pattern.

## 7. What "near-perfect" means here (acceptance targets)

State these as test gates in `TransportSyncProbe`/`PlaybackSmoke`:

- File A/V lip-sync error: **< ±1 frame** sustained; **< ±5 ms** audio-to-audio across outputs in a
  sync group.
- Live (NDI/mic) steady-state: bounded by the `Holdback` keep size (target **< 1.5 frames** after
  warm-up); no unbounded drift over a 1-hour soak.
- Seek: group seek stays atomic (silence-all → seek → collective unpause — the existing
  `group_seek_barrier`), no cross-output phase pop.
- Format change mid-stream (res/fps): handled by the source + router re-`Configure` without losing the
  master (memory: `video_source_format_change_reconfig`).

These targets are the reason the model is uniform: you can only hit "< 1 frame on a live camera
composited with a file" if both are scheduled against the same clock, and if correlated live A/V
streams preserve their sender-side relationship through `SourceSyncGroup`.
