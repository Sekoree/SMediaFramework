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
- [ ] **NDI input video receiver** — implement `NDIVideoReceiver` to pair with
  the existing `NDIAudioReceiver`; until then NDI input items remain audio-only.

## Phase C Polish / Follow-Ups

- [ ] **NDI pixel-format / resolution lock** — propagate output-definition
  locks through video routing/conversion so NDI senders present a predictable
  receiver format. UI side already stores `PixelFormatLock` / `ResolutionLockWidth`
  / `ResolutionLockHeight` on `NDIOutputDefinition` and round-trips through the
  project file; the router-side branch-format pick still needs to honour them.
- [ ] **Output preset compositor path** — wire `CompositorVideoSink` /
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

- [ ] **Cue-player pre-roll hooks** — use the live/file player open paths above
  to keep a bounded cache of ready-to-fire sessions.
- [~] **Action-cue endpoint health** — first emitter path landed in HaPlay:
  action cues now execute OSC (`OSCLib`) and MIDI (`PMLib`) with project-level
  endpoint registry lookup (`HaPlayProject.ActionEndpoints`) and surfaced error
  strings on trigger. OSC/MIDI sidebar workspaces (`OscConnectionsView`,
  `MidiDevicesView`) now expose endpoint management + MIDI device catalog
  refresh, but remaining framework-facing gap is still richer
  endpoint-health telemetry/rebind states surfaced as explicit health signals
  (not just error text).
