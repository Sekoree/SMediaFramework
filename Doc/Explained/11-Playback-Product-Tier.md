# 11 · Playback (Product) Tier

`S.Media.Playback` is the **show-control / product** layer — the high-level facades a
host app *drives* instead of hand-wiring decoders, routers, and clocks. It references
Core + FFmpeg. The rule (from `Doc/MediaFramework-Architecture.md`): an orchestration
facade an app drives (a player, a cue stack, a soundboard, a routing scene) lives
here; reusable engine/voice primitives stay in Core.

This is the layer HaPlay ([13](13-HaPlay-UI.md)) is built on.

## `MediaPlayer` — the one-object playback facade

`MediaPlayer` opens a shared-mux decoder with a `VideoRouter` (always), an optional
`AudioRouter` wired to the decoder's audio, and a `MediaClock` — all torn down safely
together. It references **no** PortAudio/SDL/NDI; you add outputs from the optional
packages.

### Builders (the modern surface)

```csharp
using var player = await MediaPlayer.OpenFile("clip.mkv").OpenAsync();
player.AttachAudioOutput(myAudioOutput);   // add + identity-route in one call
player.AttachVideoOutput(myVideoOutput);   // rolls back on failure
player.Play();
```

`MediaPlayerOpen` exposes the verbs, each returning a typed builder
(`MediaPlayerOpenBuilder` is the shared fluent base):

* `OpenFile(path)` → `MediaPlayerOpenFileBuilder`
* `OpenUri(uri)` → `MediaPlayerOpenUriBuilder` (http/rtsp/file:)
* `OpenStream(stream)` → `MediaPlayerOpenStreamBuilder` (AVIO, or spooled per options)
* `Open(decoder)` → `MediaPlayerOpenDecoderBuilder` (reuse an opened decoder)
* `OpenLive(audio, video)` → `MediaPlayerOpenLiveBuilder` (capture/mock graphs, no
  container)

Each builder ends in `.TryBuild(out player, out error)` (non-throwing) or
`.OpenAsync()`, with `.WithOptions(...)` (`MediaPlayerOpenOptions`: `SpoolStreamToDisk`,
`StreamIsSeekable`, queue depths, live presentation, `IncludeAudioRouter`) and
`.WithDecoderOwnership(...)` (`MediaPlayerDecoderOwnership` — who disposes the decoder).

> The older `MediaPlayer.TryOpen*` overloads remain `[Obsolete]` shims for back-compat
> (builder internals + HaPlay still use some); prefer the builders.

Operational: `GetMetrics()` → `MediaPlayerMetrics` (a one-call snapshot for HUDs/health
endpoints). Script hooks: `Triggers` (a per-player `TriggerBus`). `PlaybackAudioStartup`
holds the one-shot prefill + hardware-start callbacks for coordinated start.

## Ownership & lifecycle facades

* **`MediaSession`** — a single owning handle around a `MediaPlayer` plus the resources
  wired around it (outputs, companion hosts, app-scoped disposables). Dispose the
  session and everything tears down in a safe order: the player first (stops routers/
  decoder so nothing is still pushing to an output), then registered resources in
  reverse order. This is the "one `using` owns the whole show" object.
* **`MediaGraph` / `MediaGraphBuilder`** — an owning handle for a *built graph* with
  `MediaGraphTopology` presets (e.g. the file-playback preset via `MediaGraphFileBuilder`).
  Centralizes ownership + health snapshots for product hosts; exposes the underlying
  player for transport.
* **`MediaPlayerController`** — a **VLC-style controller facade** over a built
  `MediaGraph`: explicit `MediaPlayerControllerState`, transport methods, and a stable
  `MediaPlayerControllerSnapshot` for UI binding / logs / health endpoints. Low-level
  router/player access stays available for advanced hosts.

So there's a deliberate gradient: `MediaPlayer` (wiring) → `MediaGraph` (ownership +
health) → `MediaPlayerController` (clean transport state machine). Pick the altitude
you need. `ProductApiSamples` shows the composition in code (used by docs/tests).

## HUD & metrics

* `MediaPlayerMetrics` (record) — aggregate operational snapshot.
* `PlaybackHud` + `PlaybackHudSnapshot` — format a single-line status HUD *without*
  referencing SDL/NDI/PortAudio (so it works in any host). The smoke tools use it.

## Clip rebasing: `OffsetPlayhead`

`OffsetPlayhead` is an `IPlayhead` view of an inner playhead shifted by a constant
(`Position = inner + offset`). It's the playhead half of clip rebasing — pair it with
`RetimingVideoOutput` (Core, [05](05-Core-Video-Pipeline.md)) so a clip that starts
mid-source presents on a zero-based timeline. **Always pair the two** or backward seeks
on trimmed cues freeze (the timebase-mismatch trap, [06](06-Clocks-and-AV-Sync.md)).

## Soundboards: from voices to a pad grid

Three altitudes, building on the Core clip/voice primitives ([04](04-Core-Audio-Engine.md)):

* **`Soundboard`** — the engine: owns named cues (a clip + how it plays: mode, choke
  group, output, gain) and triggers them via `Play(...)` which returns a `CueVoice`.
  The router source/route/choke-group/reaping bookkeeping is hidden. (Moved from
  Core/Audio to Playback in 2026-06-02 to colocate with the product tier.)
* **`CueVoice`** — a live handle for one sounding voice: stop it, change its gain,
  observe it — without touching the router.
* **`SoundboardGrid`** — the pad-controller facade: `SoundboardPadDefinition` (per-pad
  config), `SoundboardPadMode`, `SoundboardLedState` + `SoundboardPadFeedback` (LED/
  feedback for a hardware controller's lit buttons), `SoundboardVoiceControl`,
  `SoundboardScheduledFire` (timed fires), and `SoundboardGridSnapshot`/`Binding` for
  UI/controller binding. This is what maps a physical grid controller (an APC, a
  Launchpad) onto a board of sounds.

## Cues: `CueGraph`

The cue-stack model (a "go" list of theatrical/show cues):

* `CueDefinition` (record) — one cue: what to play and how.
* `CueGraph` — the ordered collection + execution engine.
* `CueShowFile` (record) — the persistable show.
* `CueFaultPolicy` / `CueExecutionStatus` / `CueExecutionLogEntry` — what happens when
  a cue fails, current run state, and an execution audit log.

## Clip standby (pre-roll): `ClipStandbyEngine`

Live shows can't afford a cue to *start opening a file* when the operator hits GO.
Standby pre-opens and pre-seeks upcoming clips so GO is instant.

* `ClipStandbyEngine` — opens clips through an `IClipMediaSource`, seeks to the
  requested start, keeps "ready" entries warm, and exposes an explicit **Arm/Start
  barrier** for single or grouped cue starts (so `FireAllSimultaneously` releases
  together).
* `ClipSpec` (record) — what to open + how the host intends to route it.
* `IClipMediaSource` / `ClipMediaSource` — host-agnostic media-source factory backed by
  `MediaPlayer`'s open paths (reuse file/URI/stream/live).
* `ClipKey` — stable identity for a standby entry (host cue id + config key).

## Shared cue runtimes (audio + composition)

A composition can show several cues at once; their audio must mix into one device and
their video into one canvas. These runtimes own those shared resources:

* **`ClipAudioOutputRuntime`** — owns one `AudioRouter` feeding one physical
  `IAudioOutput`; multiple cue clips add/remove routed sources. (The device lease is
  exclusive, so a tile and a cue sounding through one line must share this pool.)
* **`ClipCompositionRuntime`** (~973 lines) — owns the `VideoCompositorSource`, layer
  slots, the output fan-out pump, and the optional clock-mastered presentation cadence
  for one composition canvas.

## Output mapping (product side)

The product-tier wrapper over the warp-mesh engine ([10](10-Effects-and-Compositing.md)):

* `ClipOutputMappingSpec` — the canvas is cut into `ClipOutputMappingSection`s drawn
  back-to-front onto an output canvas (defaults to composition size; unmapped area
  stays black — physical panel gaps fall out naturally).
* `ClipOutputMappingSection` — a normalized source slice + affine destination placement
  (position/size/rotation around the dest center). `ClipMeshPoint` carries warp control
  points in normalized dest-rect space.
* `OutputMappingResolver` — pure math turning sections into crop + affine pairs (and,
  for warp-capable GL backends, `ResolvedMappingSection` with absolute-pixel mesh
  points; the CPU stage ignores the mesh and uses the affine).

## Routing scenes

* `RoutingScene` — a scene/patch model: `OutputPatchRoute`, `SceneLayerDefinition`,
  `RoutingTransition` (+ `RoutingTransitionKind`), `SyncGroupDefinition`,
  `NdiEndpointPreset`, with `RoutingSceneSnapshot` / `RoutingSceneApplyPlan` and
  `OperatorEndpointMetrics`. This is the "recall a routing/output configuration as a
  named scene, transition between scenes" facade.

## Show-control bindings: `TriggerBindingSet`

The declarative glue between controllers and actions, sitting above the raw
`TriggerBus` ([07](07-Triggers-Diagnostics-Runtime.md)):

* `TriggerDescriptor` (`TriggerSourceKind`) — where a trigger comes from (MIDI/OSC/
  timecode/…).
* `TriggerActionDescriptor` (`TriggerActionKind`) — what it does.
* `TriggerBinding` (+ `TriggerRetriggerPolicy`) — one source→action mapping with
  retrigger semantics.
* `TimecodeSyncPlan` (`TimecodeSyncKind`) — bind actions to a timecode timeline.
* `TriggerDispatch` — a resolved dispatch the runtime executes.

> So the cue/soundboard/scene facades define *what can happen*, and `TriggerBindingSet`
> + `TriggerBus` define *what makes it happen*. The actual controller I/O and scripting
> is `S.Control` ([12](12-Control-and-Scripting.md)).

Next: [12 · Control & Scripting](12-Control-and-Scripting.md).
