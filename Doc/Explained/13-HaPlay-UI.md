# 13 · HaPlay UI

HaPlay is the Avalonia demo / show-control application that drives the whole
framework. It started as a quick way to test playback and grew into a real
multi-deck media server. The app library is the largest single module (~39k
lines, 339 production types), with a small desktop entry-point project on top
(5 more types). It is **MVVM** (CommunityToolkit.Mvvm) and **NativeAOT-published**.

HaPlay is also the integration surface with the most workflow churn. Treat this doc
as a map of what is there today, and see [15](15-Issues-and-Improvements.md) for the
remaining decomposition work.

## App shell & the six workspaces

* **`HaPlay.Desktop/Program.cs`** — the NativeAOT entry point; configures
  `MediaDiagnostics.LoggerFactory` before `BuildAvaloniaApp` so static framework
  loggers resolve to the app logger. CLI flags include `--media-log off`,
  `--media-log-level`, `--media-log-dir`, `--media-log-queue`, `--media-log-retain`,
  and `--media-log-first-chance` for targeted exception-noise crash hunts.
* **`HaPlay.Desktop/RollingFileLogger.cs`** — `RollingFileLoggerProvider`,
  `RollingFileLoggerOptions`, and the internal `RollingFileLogger`; a bounded,
  drop-oldest background writer that keeps one log file per process and prunes old
  files on startup.
* **`HaPlay.Desktop/DesktopCrashDiagnostics.cs`** — process-level crash diagnostics:
  AppDomain unhandled exceptions, Avalonia dispatcher exceptions, unobserved task
  exceptions, and `NativeResourceHealth` stuck-resource records are written to both the
  rolling logger and a synchronous `haplay-crash-*.log` file.
* **`App.axaml(.cs)`** — Avalonia app, theme, DI-less composition root.
* **`MainWindow` / `MainView`** (+ `.axaml.cs`) — the shell. `MainViewModel` (~1,527
  lines) owns the workspace switching and the sub-view-models.

The sidebar selects one of six **workspaces** (`WorkspaceItem`, `Ctrl+1..6`):

| Workspace | ViewModel | What it is |
|-----------|-----------|------------|
| **Players** | `MediaPlayerViewModel` (×N) via `PlaylistTabViewModel` | One or more media-player decks with playlists. |
| **Cues** | `CuePlayerViewModel` | The cue stack — compositions, layers, GO list, output mapping. |
| **Soundboard** | `SoundboardWorkspaceViewModel` | Tabs of trigger-pad grids. |
| **Control** | `ControlWorkspaceViewModel` | The live MIDI/OSC control system + script editor. |
| **I/O** | `OutputManagementViewModel` | Output lines (PortAudio/NDI/SDL), inputs, routing/matrix. |
| **Project** | (MainViewModel) | Save/open the whole show. |

`ViewModelBase` is the MVVM base; `AppearanceController` handles theme/density;
`ToastCenter`/`ToastViewModel` are the notification system; `Strings` (~861 lines) is
the localized text. Converters (`EnumDisplayConverter`, `BrushFromHexConverter`,
`AggregateBrushConverter`, `FilenameConverter`) bridge model values to XAML.

## The playback engine layer (the bridge to the framework)

This is the most important non-UI part of HaPlay — it wires framework objects to UI
lines and is where the real complexity lives.

* **`HaPlayPlaybackSession`** (~1,829 lines) — per-line wiring metadata so
  `TryAddOutput`/`TryRemoveOutput` can unwind *exactly* what they wired without
  disturbing other lines. This is the object that connects a `MediaPlayer` to the set
  of selected output lines (audio routers, video routers, NDI senders) and tears it
  down cleanly.
* **`CuePlaybackEngine`** (~2,360 lines) — the cue-side runtime: manages N concurrent
  media cues plus two shared-resource pools — per active **composition** (shared video
  mixer + acquired outputs) and per active **audio output line** (shared `AudioRouter`
  so N cues mix into one device). Independent of player tabs; only shares the registry
  of physical lines. Emits `CuePlaybackProgress`, drift warnings
  (`CueCompositionDriftWarning`), and pump-pressure warnings.
* **`SoundboardEngine`** — one audio-only `AudioClipPlayer` per playing tile, mixed
  into the same per-line pool as cue audio (via `ClipAudioOutputRuntime` — the device
  lease is exclusive, so a tile and a cue through one line must share the pool). Loop
  wrap + fade reuse the cue engine's primitives.
* **`PlaybackVideoPipeline`** / **`CueCompositionRuntime`** — adapt HaPlay output-line
  VMs into framework compositor/output leases and pick the GL-vs-CPU backend.
* **Pre-roll caches** (mirror the framework standby idea per source type):
  `CuePreRollCache` (FFmpeg decoders), `PlaylistDecoderCache` (adjacent playlist
  items), `NdiInputPreConnectCache` / `PortAudioInputPreConnectCache` (warm live
  inputs). These make GO / track-change instant.
* **Connectors:** `NdiInputConnector`, `PortAudioInputConnector` resolve/open live
  inputs; `CueMediaProbe` (`CueMediaProbeResult`) inspects a file's streams so the cue
  drawer hides irrelevant tabs.
* **Cue helpers:** `CueClipWindow` (builds a framework `ClipWindow` from trim offsets),
  `CueFrameRatePolicy` (compare source vs canvas fps), `CuePreviewSession` (transient
  audition path outside the shared pools).
* **Output wrappers** (framework `IVideoOutput` decorators specific to HaPlay needs):
  * `BgraConvertingVideoOutput` — declares BGRA-only so the router inserts a converter
    before the CPU compositor.
  * `LockedFormatVideoOutput` — pins the pixel format/resolution an NDI output presents
    regardless of source (the "NDI format lock" framework gap).
  * `LogoFallbackVideoOutput` — substitutes a static logo during live faults; caches the
    last real frame to restore on un-hold (important for single-frame cover-art audio).
  * `HeldFrameVideoSource` — emits copies of one BGRA frame for image/text cues.
  * `OutputPresetVideoSource` — scales decoder video into a fixed program raster.
  * `MeteringAudioOutput` — peak metering decorator feeding the level meters.
  * `TextFrameRenderer` (Skia) / `FallbackImageLoader` / `WaveformExtractor`
    (background peak extraction for the scrubber) / `IdleLogoSlateSession`,
    `MappingTestPattern`, `PlaybackThroughputDiagnostics`, `OutputLineHealthEvaluator`.

## Persistent output-preview runtimes (a key idea)

These keep outputs *alive across idle↔playback transitions* so receivers/devices don't
churn on every track change:

* **`NDIOutputPreviewRuntime`** (~508 lines) — holds an open `NDIOutput` for the
  lifetime of an NDI line, continuously emitting black video + silent audio so
  receivers stay locked on. Playback temporarily *acquires part of* the carrier (only
  the side being wired pauses) so e.g. an audio-only file keeps the black video going.
* **`PortAudioOutputRuntime`** — opens one `PortAudioOutput` once and keeps it open
  across sessions; the callback drains silence between sessions so ALSA/PulseAudio
  doesn't release the device. Sessions acquire/release; the stream closes only when the
  line is removed.
* **`LocalVideoPreviewRuntime`** / **`PortAudioLiveMonitoring`** — local SDL preview and
  low-latency monitoring sizing.

> **ELI5:** instead of dialing the phone fresh for every call (re-opening the device /
> re-advertising NDI, which receivers see as a drop), HaPlay keeps the line open and
> just hands the microphone over when there's something to play.

## Project & models (persistence)

The `Models/` namespace is the persisted show data, serialized AOT-safely via
`ProjectIO` (source-generated JSON, no reflection):

* **`HaPlayProject`** — the top-level show file; one save/open is the whole session.
* **`ProjectSections`** / `ProjectIO` — section ids for scoped save/export and
  section-aware load (the 2026-06-10 save/load rework).
* **Players:** `MediaPlayerConfig`, `PlaylistItem` (discriminated union:
  `FilePlaylistItem`, `ImagePlaylistItem`, `TextPlaylistItem`, `NDIInputPlaylistItem`,
  `PortAudioInputPlaylistItem` — files, stills, title cards, and live inputs all in one
  list), `ChannelPresetRule` (auto-apply a downmix when an N-channel file loads).
* **Audio:** `AudioMatrix` / `AudioMatrixCellConfig` (the per-output N×M mix matrix —
  rows = device channels, cols = source channels, each cell gain+mute;
  `AudioMatrixDefaults` is the audible threshold below which a cell installs no route),
  `AudioDownmixPresets` (`AudioDownmixPreset` + `DownmixContribution`), `AudioRouteMixMode`,
  `SharedHeadphonesBus` (a project bus aliasing a PortAudio output so several decks
  monitor on one pair), `VirtualAudioChannelAssignment` (VOut N → physical channel).
* **Cues:** `CueList` / `CueListsCollectionDocument` (a cue list is a self-contained tree
  of groups+cues + its own compositions+outputs), `CueOutputMapping` /
  `CueOutputMappingSection` (output mapping per composition→output binding),
  `CueCompositionsDocument` (shareable composition sets), `CueColorTagPalette`.
* **Soundboard:** `SoundboardConfig` / `SoundboardTileConfig` /
  `SoundboardsCollectionDocument`.
* **Outputs / control:** `OutputDefinitions` (`OutputDefinition`), `ControlGraphConfig`,
  `ActionEndpoint`, `WorkspaceItem`.
* **App-level (per machine, not per project):** `AppSettings` (sidebar state,
  `WindowStateSnapshot`, `DialogSizeSnapshot`, `AppThemeMode`, `AppDensityMode`) under
  `%LocalAppData%/HaPlay/`.

## Dialogs, controls & views

* **Dialogs** (`ViewModels/Dialogs/` + `Views/Dialogs/`) — a large set: add/edit
  outputs and inputs (PortAudio/NDI/local video), audio matrix editor, cue list
  settings, layer editor, **mapping editor** (the warp-mesh calibration UI,
  `MappingEditorViewModel` + `MappingTestPattern`), MIDI/OSC device dialogs, periodic
  send, rebind-missing-* (relink saved outputs/devices/endpoints when hardware moved),
  rename/renumber, routing, target config, project export. `DialogStatePersister` /
  `PopoutWindow`/`PopoutRegion` / `DetachedPlayerWindow` handle pop-out/detach.
* **Custom controls** (`Views/Controls/`): `LevelMeterControl` (dB meters fed by
  `MeteringAudioOutput`), `WaveformControl` (scrubber waveform), `SparklineControl`
  (per-line throughput history), `StatusLineControl`, `CompositionPlacementCanvas` (the
  drag-to-place composition layer editor).
* **Health VMs:** `OutputLineViewModel` / `OutputLineHealthState`,
  `ActionEndpointRowViewModel` / `ActionEndpointProbe` / `…HealthState`,
  `ActiveCueViewModel` / `ActiveGroupViewModel`.

## Remote control (REST API)

A headless control surface so show controllers (Companion, touch panels, `curl`) can
drive HaPlay:

* **`RemoteApiDispatcher`** — transport-agnostic routing of API paths onto the view
  models. Every handler hops to the UI thread, validates its target, and **kicks off
  the command without awaiting playback** (a remote controller needs the request to
  return immediately, but a transport can block for seconds on prefill).
* **`RestApiServer`** — a minimal `HttpListener`-based front end (no web-framework dep,
  for NativeAOT). It binds loopback by default and can be explicitly switched to a LAN
  wildcard bind. LAN wildcard binding still falls back to loopback when the platform
  refuses the prefix (for example without a Windows URL ACL). `RemoteApi` /
  `RemoteApiEndpointDoc` / `RemoteApiResult` are the contract. URL scheme (1-based
  indices matching UI labels):
  `/api/v1/cues/go|pause|...`, `/api/v1/players/{p}/play|volume|hold|...`,
  `/api/v1/soundboards/{b}/{tile}/tap|play|stop|fade`, `/api/v1/control/arm|disarm`.

### REST API security model

The API is off by default. When enabled, every request must carry the generated
per-machine token, either as `?key=...` / `?token=...`, `Authorization: Bearer ...`, or
`X-HaPlay-Api-Key`. The Project workspace shows the active bind URL, the token, and the
loopback/LAN mode. Copied operator URLs include the token. CORS is not opened by default;
browser-based controllers should use explicit token-bearing URLs or a trusted local
bridge.

## NativeAOT specifics

* `AotBinding` (`Views/AotBinding.cs`) — compiled-binding helpers for the few spots that
  would otherwise need reflection.
* `ProjectIO` uses `System.Text.Json` source generation so project save/load is
  trim/AOT-safe. (Project memory: the whole app publishes to a ~46 MB native binary;
  the ~6 remaining reflection spots — JSON + code-behind bindings — warn but compile.)

> Verification note (project memory): HaPlay has a headless `xvfb` smoke run; package
> DLLs land in the Desktop `bin`, not the library; `StyleInclude` `avares://` URIs use
> the **assembly name**.

For the complete HaPlay class list, see
[16 · Type Coverage Appendix](16-Type-Coverage-Appendix.md#uihaplay).

Next: [14 · Tools & Probes](14-Tools-and-Probes.md).
