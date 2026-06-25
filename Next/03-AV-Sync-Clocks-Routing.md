# 03 — A/V Sync, Clocks & Routing

The headline requirement: **near-perfect A/V sync with multiple inputs (file + live: NDI, mic/line)
and multiple outputs.** Today this works well for files but live uses a *separate, master-less* path
(the NDI ingest clock is discarded), which is why live sync keeps needing rework. The rewrite
converges everything onto **one** model.

## 1. The model in one paragraph

There is exactly one **master clock** per session (`SessionClock`). Every source — file or live —
is presented through a **`SourceTimeline`** that maps source PTS onto master time via a fixed
**offset** and a **rebase policy**. Audio for the master bus drives the clock (lowest-latency
reference); video and all other outputs **present scheduled against master time**. Multiple inputs are
just multiple `SourceTimeline`s feeding the routers/compositor; multiple outputs are kept phase-locked
by a **sync group**. There is no second path for live — live is "a source whose rebase policy is
*holdback + rebase-to-latest* instead of *scheduled*."

```
   sources                 timelines (offset + rebase)      mix/composite        outputs (scheduled)
  ┌─────────┐   PTS      ┌──────────────────────────┐                          ┌──────────────────┐
  │ file A  │──────────► │ Scheduled  off=0          │──►┐                  ┌──►│ SDL3 window      │
  ├─────────┤            ├──────────────────────────┤   │  AudioRouter     │   ├──────────────────┤
  │ NDI in  │──────────► │ Holdback+RebaseLatest     │──►┼─►(N→M matrix)──► ├──►│ PortAudio device │
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
- **`SessionClock` (new, master).** Wraps whichever reference advances time:
  - *File-led session:* the master audio output (a clocked PortAudio/miniaudio device) advances it —
    same as today's "audio is master."
  - *Live-led session (no file):* a free-running monotonic wall clock advances it, disciplined by the
    chosen live master's arrival cadence (PI controller on long-term drift, not per-frame).
  - The session picks the reference once at start; switching reference is an explicit, debounced op.
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

> **Why this fixes the NDI saga.** The recurring live-desync work (memory: `ndi_input_av_sync`,
> `avsync_playback_review`) all stems from live video/audio not sharing a master. Here they do: NDI
> video and NDI audio are two `SourceTimeline`s over the *same* `SessionClock`, so their relative
> phase is observable and correctable instead of invisible. The "reduce audio keep + keep video
> LatestOnTick + optional trim" tuning becomes: `Holdback` keep-size + per-source `Offset` — set in
> one place, testable headless.

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

A session holds an ordered set of `(IVideoSource | IAudioSource, SourceTimeline)`.

- **Audio inputs** (file tracks, mic/line via `IAudioBackend.CreateInput`, NDI audio) are sources on
  the `AudioRouter`. Multi-track files expose multiple audio sources; selection (none/one/many) picks
  which are added (see §6).
- **Video inputs** (file, NDI receiver, capture) become **compositor layers** when a composition is
  active, or route directly to outputs in single-source playback. Each carries its own
  `SourceTimeline`, so a live camera and a file clip stay in sync on the same canvas.
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

## 6. Audio channel remap & multi-track selection

- **Remap** is the `AudioRouter` matrix + `ChannelMap` (already SIMD-accelerated). The UI's
  `AudioMatrix`/`AudioDownmixPresets`/`VirtualAudioChannelAssignment` models become a `RoutingScene`
  in `S.Media.Session`, serialized with the show.
- **Multi-track**: `Decode.FFmpeg` enumerates audio tracks (`AudioTrackInfo[]`). Open options take a
  selection: `None` (video-only), one track, or several tracks (each a source on the router — e.g.
  separate language stems to separate buses). Subtitles use the same select-none/one/many pattern.

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
composited with a file" if both are scheduled against the same clock.
