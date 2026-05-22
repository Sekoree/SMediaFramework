# HaPlay Framework Gaps Checklist (2026-05-20)

Companion checklist for `HaPlay-UI-Refactor-Plan-2026-05-20.md` §9.6.
Status legend:

- `[ ]` not started
- `[~]` in progress
- `[x]` done

## Phase A/B Foundations

- [x] **Hot route addition to a running playback session** — framework routers
  already support dynamic sink/output/route changes; HaPlay now owns the
  session-level `TryAddOutput` / `TryRemoveOutput` bridge and clone mirroring.
- [x] **Output runtime reconfigure-in-place** — PortAudio, NDI, and local-video
  runtimes expose `ReconfigureAsync`; HaPlay drops and reacquires active routes
  around hot edits.
- [x] **Explicit media-player default options** — `MediaPlayerOpenOptions.Default`
  now uses the intended constructor defaults instead of struct zero defaults,
  so the default file/live graph includes audio routing.

## Phase C / C.5 Blockers

- [x] **`MediaPlayer.TryOpenLive(IAudioSource?, IVideoSource?, ...)`** —
  bypass `MediaContainerDecoder` for already-decoded live sources while keeping
  the same `AudioPlayer` / `VideoRouter` / `VideoPlayer` surface.
- [x] **`PortAudioInput` disconnect detection** — expose stream active/error
  state and callback-fault diagnostics so UI can enter "waiting for source".
- [x] **NDI input video receiver** — landed 2026-05-21. `NDIVideoReceiver` +
  `NDIVideoFrameUnpack` (`S.Media.NDI`); HaPlay opens video-only, audio-only, or
  combined NDI input items via `TryCreateLive`.
- [x] **NDI input audio jitter buffer** — landed 2026-05-22. `NDIAudioReceiver`
  now takes a `minBufferedDuration` (default 50 ms) and serves
  `max(0, available − holdback)` from `ReadInto`, so the burst pattern of NDI's
  per-video-frame audio delivery (≈16.7 ms at 60p, 33.3 ms at 30p) no longer
  drains the ring below the router's 10 ms chunk size and the silence-pad path
  in `AudioRouter.RunLoop` is avoided. Capped at half the ring capacity so the
  holdback can never starve the entire ring. `NDIAudioReceiverHoldbackTests`
  pins the policy.
- [x] **Live source `RebaseToLatest`** — landed 2026-05-22. The holdback alone
  doesn't unblock the connect-to-Play stale-samples symptom: NDI/PortAudio
  receivers run from connect, so by the time Play fires their rings already
  hold seconds of FIFO-ordered samples (and the video-frame PTS counter has
  advanced past the freshly-zeroed playback clock, leaving video black while it
  waits for the playhead to catch up). `NDIAudioReceiver.RebaseToLatest` /
  `PortAudioInput.RebaseToLatest` advance the read pointer to keep ≤ 100 ms
  buffered; `NDIVideoReceiver.RebaseToLatest` drains its queue and resets
  `_nextPts = 0` under a per-frame lock so a concurrent capture can't enqueue a
  stale-PTS frame. HaPlay calls these via
  `HaPlayPlaybackSession.RebaseLiveSourcesForPlay()` at every `Router.Play`
  site (initial play, seek-with-resume, playlist advance, loop wrap).
  - 2026-05-21 follow-up fix: live open now wires `HaPlayPlaybackSession` through
    `MediaPlayer.PlaybackSession` (instead of container-only `Session`) so NDI/live
    sessions no longer throw `InvalidOperationException` during post-open wiring.
  - 2026-05-21 follow-up fix: live playback paths no longer read `MediaPlayer.Decoder`
    during output priming / hot output wiring. Live sessions now use
    `LiveHasVideo` / `SourceAudioFormat` guards, which prevents
    `This MediaPlayer was opened from live sources and has no container decoder.`
  - 2026-05-21 follow-up fix: live transport stop no longer routes through seek.
    `Stop` now pauses live sessions and `PlaybackRouter` treats zero-seek on live
    as pause, preventing non-seekable-source exceptions (`does not implement
    ISeekableSource`).
  - 2026-05-21 follow-up fix: packed-422 GL shaders renamed sampler uniform
    `packed` → `packedTex` (`uyvy422` / `yuyv422`) to avoid GLSL reserved-token
    compile failures on Mesa (`unexpected PACKED_TOK`).
  - 2026-05-21 follow-up fix: `NDIVideoFrameUnpack` now unpacks
    `NDIFourCCVideoType.Yv12` (Y + V + U native layout mapped to
    `VideoFrame` planes Y/U/V). This closes the live NDI "audio present, black
    video" path when senders negotiate YV12.
  - 2026-05-21 follow-up fix: `NDIVideoReceiver` now requests
    `NDIRecvColorFormat.UyvyBgra` (instead of `Fastest`) so receiver-side
    conversion stays in known ingest formats; added unpack fallback mappings
    for `Bgrx`/`Rgbx` and first-drop diagnostics when unpack fails.

## Phase C Polish / Follow-Ups

- [x] **NDI pixel-format / resolution lock** — landed 2026-05-21.
  `LockedFormatVideoSink` wraps each NDI output's `VideoSink` whenever the
  definition has a `PixelFormatLock` / `ResolutionLockWidth` / `Height` set;
  filters `AcceptedPixelFormats` so the router negotiator picks the locked
  format, and letterboxes incoming frames into the locked raster via the
  existing `CpuVideoCompositor` pipeline (same path `OutputPresetVideoSource`
  uses for the file-open preset). Add/Edit NDI Output dialog now exposes
  editable pixel-format and resolution combos backed by `NDIPixelFormatChoice`
  / `NDIResolutionChoice`; "Auto" entries map to null locks so the negotiator
  picks per-source.
- [x] **Output preset compositor path** (HaPlay file open, 2026-05-21) —
        `OutputPresetVideoSource` + `MediaPlayer.TryOpen(..., videoSourceOverride)`.
        Idle-image-as-compositor-layer (§8.10) still open.
- [ ] **Output preset compositor path (idle layer / PiP)** — wire `CompositorVideoSink` /
  `IVideoCompositor` as the fixed-format program output path for 1080p60,
  720p60, custom, and idle-image composition. Blocks: Output preset (§4.3.5
  preset combobox is UI-only today), fade transition, idle-image as
  compositor layer (§8.10).
- [~] **Dynamic output-channel capability handshake** — partially landed
  2026-05-21. `HaPlayPlaybackSession.TryGetEffectiveOutputChannelCount(...)`
  now exposes runtime-confirmed sink widths from active line wiring
  (`LineWiring.SinkChannelCount`), and `MediaPlayerViewModel` matrix sizing now
  consumes that before falling back to output-definition defaults. Remaining
  follow-up: promote this to an explicit framework-level capability contract for
  sinks that can renegotiate channels dynamically at runtime.
  - 2026-05-21 follow-up: framework contract introduced as
    `IAudioSinkChannelCapabilities` (`AudioSinkChannelCapabilities`), implemented
    on `PortAudioOutput`, `NDIAudioSink`, and common wrappers (`ResamplingAudioSink`,
    `AdaptiveRateAudioSink`, `NDIAudioAggregatingSink`, `BusSink`). HaPlay now
    consults live sink capabilities via `AudioRouter.TryGetSink(...)` in
    `TryGetEffectiveOutputChannelCount(...)` before using definition-time defaults.
- [x] **Per-cell channel-mix matrix** — shipped 2026-05-20.
  `AudioRouter` now supports multiple routes per `(source, sink)` pair via
  `AddRoute(source, sink, routeId, map, gain)` + `RemoveRouteById` /
  `SetRouteGainById`. The run loop already summed routes additively; the
  change is purely in key management (route id replaces compound `(source, sink)`
  key for `_currentGains` / `_routeTargetGains`). Back-compat: the legacy
  `AddRoute(source, sink, map, gain)` overload synthesizes a stable route id
  from the pair so existing one-pair-one-route callers keep working;
  `RemoveRoute(source, sink)` and `SetRouteGain(source, sink, gain)` now sweep
  every route between the pair so the legacy contract still holds. 5 new
  framework tests cover multi-route, gain-by-id, remove-by-id, and route-id
  collision rejection. HaPlay's per-cell matrix UI ships against this API in
  the same session.

## Phase D Blockers

- [x] **Cue-player pre-roll hooks** (2026-05-21) — file cues via `CuePreRollCache`;
        NDI audio pre-connect via `NdiInputPreConnectCache` (§6.11).
  to keep a bounded cache of ready-to-fire sessions.
- [~] **Action-cue endpoint health** — first emitter path landed in HaPlay:
  action cues now execute OSC (`OSCLib`) and MIDI (`PMLib`) with project-level
  endpoint registry lookup (`HaPlayProject.ActionEndpoints`) and surfaced error
  strings on trigger. OSC/MIDI sidebar workspaces expose management + **Test
  connection/device** probes (2026-05-21); broken cue endpoint refs flag
  `IsEndpointBroken` after load. Persistent health LEDs + rebind-missing-endpoints
  dialog for action targets landed 2026-05-21.
