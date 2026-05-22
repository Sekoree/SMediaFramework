# HaPlay UI Refactor — Checklist (2026-05-20)

Companion to [`HaPlay-UI-Refactor-Plan-2026-05-20.md`](HaPlay-UI-Refactor-Plan-2026-05-20.md).
Framework-side gaps are tracked separately in
[`HaPlay-Framework-Gaps-Checklist-2026-05-20.md`](HaPlay-Framework-Gaps-Checklist-2026-05-20.md).

Status legend:

- `[ ]` not started
- `[~]` in progress / partial
- `[x]` done

Each item links back to the plan section that motivates it.

---

## Phase A — Foundations (no user-visible change)

- [x] **Output runtime `ReconfigureAsync` support** (§3.2, §9.6)
  - [x] `PortAudioOutputRuntime.ReconfigureAsync`
  - [x] `ILocalVideoPreviewRuntime.ReconfigureAsync` (SDL3 + Avalonia variants)
  - [x] `NDIOutputPreviewRuntime.ReconfigureAsync`
  - [x] `Reconfigured` event on each runtime for Phase B orchestration
- [x] **Hot route addition API** (§4.3.3, §9.6)
  - [x] `HaPlayPlaybackSession.TryAddOutput(line, out error)`
  - [x] `HaPlayPlaybackSession.TryRemoveOutput(line, out error)`
  - [x] Per-line wiring metadata tracking + clean rollback on failure
- [x] **Project file schema + serializers + Save/Load command plumbing** (§7)
  - [x] `HaPlayProject` record + `schemaVersion`
  - [x] `JsonPolymorphic` `OutputDefinition` with discriminator
  - [x] Forward-compat fields: `LocalVideoOutputDefinition.CloneOfId`,
    `NDIOutputDefinition.PixelFormatLock` + `ResolutionLock`
  - [x] `ProjectIO.SaveAsync` / `LoadAsync` + `UnsupportedSchemaVersionException`
  - [x] `MainViewModel.BuildProjectSnapshot()` / `ApplyProjectSnapshot()`
  - [x] `HaPlay.Tests` project with round-trip + schema-version tests

## Phase B — Outputs Editable + Project Save/Load Visible

- [x] **Edit dialogs reuse Add forms** (§3.2)
  - [x] `LoadFromExisting` on each dialog VM
  - [x] Dynamic title + button label ("Add" → "Save")
  - [x] `Id` and forward-compat fields preserved across edit
  - [x] Dialogs resizable; engine combobox locked during edit
  - [x] "Edit while in use" confirm — fires only when a player is actively playing through the line
- [x] **File → Save / Open Project commands** (§7.3)
  - [x] File menu: New / Open / Save / Save As + keyboard shortcuts
  - [x] Recent projects (capped at 8) persisted to `%LocalAppData%/HaPlay/recent-projects.json`
  - [x] Missing-route detection on load surfaces a status banner
  - [x] Window title bound to `ProjectTitle`
  - [x] **Rebind missing outputs dialog** (§7.3) — landed 2026-05-21.
    `RebindMissingOutputsDialog` prompts on project open when player routes reference
    output display names missing from the loaded project; applies replacements via
    `MediaPlayerViewModel.RemapSelectedOutputs`.
- [x] **App-shell sidebar** (§12.1)
  - [x] Collapsible (icon-only ~48 px vs labelled ~180 px)
  - [x] Hamburger toggle + Ctrl+B
  - [x] Ctrl+1 … Ctrl+6 jumps to Players / Cues / Outputs / OSC / MIDI / Project
    (2026-05-21 — Cues was Ctrl+2 before OSC/MIDI split; shortcuts follow sidebar order)
  - [x] State persisted to `%LocalAppData%/HaPlay/app-settings.json`
- [x] **Clone-of UX + PlayerRoutingMirror** (§3.4)
  - [x] Visual nesting (indent + "↳" caption "clone of …")
  - [x] Edit Local Video dialog: "Clone of (optional)" dropdown
  - [x] Clones hidden from per-player checkbox list
  - [x] Parent-tick → wire parent + clones automatically (`SelectedOutputLines`)
  - [x] `RoutingTopologyChanged` event so Edit-time clone-of changes resync players
- [x] **Dialog conventions + terminology rename** (§12.2, §12.3)
  - [x] Title bars use action verbs ("Add X" / "Edit X")
  - [x] Primary action right-aligned, Cancel to its left
  - [x] Validation surfaces inline
  - [x] `KindLabel` shows user-visible names ("Standalone window" / "In-app preview" /
    "Local audio" / "NDI program") with technical names as tooltip
  - [x] Engine combobox carries user-visible label + subtitle
  - [x] "Hold image" → "Idle image"; toggle label → "Show when no media playing"
  - [x] **Dialog size persistence per dialog type** (§12.2 last bullet) — landed 2026-05-21.
    `DialogStatePersister` attached from each resizable dialog's ctor; persists width/height per
    dialog-class-name key into `AppSettings.DialogSizes`. Position deliberately stays
    `CenterOwner` so a saved point can't land on the wrong monitor across sessions. Coverage on
    eight dialogs: PortAudio in/out, NDI in/out, LocalVideo, both Rebind…, ActionCueBuilder.
- [x] **Hot-routing follow-up** (§4.3.3) — landed early
  - [x] `OutputLineRemoving` event so sessions unwire BEFORE the runtime is disposed
    (fixes the spammed `ObjectDisposedException` on remove-during-play)
  - [x] Checkbox toggle during playback hot-wires via `TryAddOutput` / `TryRemoveOutput`

## Phase C — MediaPlayer Redesign

- [x] **Transport bar + master volume** (§4.3.1)
  - [x] Big buttons (Play/Pause, Prev/Next, Stop)
  - [x] Master gain slider + Mute toggle
  - [x] Three-time display (position / remaining / duration)
  - [x] **Seek-on-drag-end with bounded CT** (§4.3.1, §9.5) — verified 2026-05-20:
    `MediaPlayerView.axaml.cs` only fires `SeekToSliderCommand` from
    `OnSeekSliderPointerReleased` and `OnSeekSliderKeyUp` (arrow / Home / End / PgUp / PgDn).
    The slider's `Mode=TwoWay` keeps the visual in sync per-tick without decoding.
    `SeekToSliderAsync` runs through `WithPlaybackArcAsync` with the
    `[[playback-teardown-timing]]` two-tier wall (inner 3 s, outer 5 s).
  - [x] **Keyboard-first transport shortcuts** (§4.3.1) — Space play/pause,
        `[` / `]` prev/next, `,` / `.` jog ±5 s. Tunnel-routed `KeyDown` handler on
        `MediaPlayerView` bails on `TextBox`, `NumericUpDown`, `Slider` sources and
        any modifier-chord (so playlist-tab rename, transition-ms NumericUpDown,
        slider scrubbing, and `Ctrl+S` on `MainView` keep working). New
        `JogBackCommand` / `JogForwardCommand` reuse `SeekToSliderAsync` so the
        bounded-CT teardown timing matches a normal drag-end commit.
- [x] **Playlist tabs** (§4.3.2)
  - [x] `PlaylistTabViewModel` + `PlaylistTabs` collection on the player
  - [x] Tab strip with inline rename TextBox
  - [x] Add / Remove tab commands
  - [x] Save tab / Load tab commands
  - [x] **Save Player as…** (§4.3.2) — verified 2026-05-20: `BuildPlayerConfig`
    bundles every `PlaylistTabs` entry (incl. per-tab Looping / AutoAdvance / SelectedPath),
    `SelectedPlaylistTabIndex`, master `MasterVolumeDb` / `MasterMuted`,
    `OutputPreset` + `TransitionMode` + `TransitionDurationMs`, the ticked
    `SelectedOutputDisplayNames`, and `OutputGains` per route. `MediaPlayerConfig`
    schema is `HaPlayPlayerConfig/v1` with the legacy flat `PlaylistPaths` retained
    so older player/project files still load via the `ApplyPlayerConfig` fallback.
  - [x] **Playlist import (`.m3u` / `.m3u8`)** (§4.3.2 last bullet) — verified
    2026-05-20: `PlaylistIO.LoadAsync` branches on extension and uses
    `ParseM3uAsync` to honor `#EXTM3U`-style comments and resolve relative paths
    against the playlist's directory. The Load-tab picker already lists
    `*.m3u;*.m3u8` as a filter; an imported m3u creates a new tab whose name
    defaults to the filename.
  - [x] **Live items in playlists** (§6 / Phase C.5) — shipped Phase C.5; checklist
    entry was stale.
- [x] **Inline `+ Add output…`** (§4.3.3)
  - [x] Flyout in the Routing expander with PortAudio / Local Video / NDI entries
  - [x] New output auto-wires on tick (via the hot-route follow-up above)
- [x] **Audio matrix** (§4.3.4)
  - [x] Per-output gain + mute (the under-matrix convenience sliders)
  - [x] Edits apply live via per-route `SetRouteGain`
  - [x] **Per-output channel-mix mode** (Stereo / Swap / Mono-L / Mono-R / Silence) —
    landed 2026-05-20. `AudioRouteMixMode` enum on `OutputGainConfig` round-trips;
    `HaPlayPlaybackSession.TrySetOutputChannelMap` rebuilds the route's `ChannelMap`
    in-place. Mix mode now doubles as a preset that rebuilds the matrix below.
  - [x] **Full N×M cell grid with per-(input-channel, output-channel) gain** —
    landed 2026-05-20. `AudioMatrixCellConfig` persisted per output gain;
    `AudioMatrixViewModel.Resize` auto-sizes to source channel count on session
    open (sink is currently 2 — see WireAudio downmix). Per-cell `GainDb` slider
    + Mute toggle in the TreeDataGrid; cell edits push down via
    `HaPlayPlaybackSession.TrySetOutputMatrix`.
  - [x] **Multi-route backing** (one `AudioRouter` route per non-zero cell) —
    framework `AudioRouter.AddRoute(source, sink, routeId, map, gain)` plus
    `RemoveRouteById` / `SetRouteGainById` shipped 2026-05-20. Master/per-output
    gain rides ride every cell route click-free via
    `TrySetOutputMatrixCompoundGain` → `SetRouteGainById`.
  - [x] **Channel labels** along TreeDataGrid columns ("In L / In R" or "In 1..N")
    and rows ("Device · Out L / Out R" or "Device · Out 1..N").
  - [x] **TreeDataGrid grid view** — `MatrixTreeGrid` in `MediaPlayerView`
    (`FlatTreeDataGridSource<AudioMatrixRow>`); columns built dynamically in
    code-behind from `AudioMatrixInputChannelCount` so the layout follows the
    loaded source's channel count.
  - [x] **Virtual output channel numbering (`VOut 1..N`)** — landed 2026-05-21.
    Matrix rows now carry deterministic `VOut` labels in selected-output order
    (`VOut {n} · {Device} · Out {ch}`), shared by the route list below.
  - [x] **Output-management virtual-channel assignment** — landed 2026-05-21.
    New `Virtual Audio Channels` section in Outputs workspace lets operators assign
    each real output channel to a `VOut` number; MediaPlayer matrix/route mapping
    consumes assignments and project save/load round-trips them
    (`HaPlayProject.VirtualAudioChannels`). Duplicate `VOut` picks auto-resolve to
    the next free channel on edit (2026-05-21).
  - [x] **Per-connection route list (TreeDataGrid)** — landed 2026-05-21.
    New `MatrixRoutesTreeGrid` shows one row per active route (`Input`, `VOut`,
    `GainDb`, `Mute`, `Effective`) and edits reuse the same cell VM as the
    matrix, so updates are bidirectionally synced.
  - [x] **Per-channel input column attenuation** (separate per-input trim row) —
    landed 2026-05-21. `AudioMatrixInputTrims` now exposes one gain/mute row per
    input channel; trims are applied to every route cell using that input and are
    persisted via `MediaPlayerConfig.InputTrims`.
  - [x] **TreeDataGrid-only matrix surface** — accepted 2026-05-21: no
    separate non-TreeDataGrid "full matrix" editor is planned.
  - [x] **Matrix sink-channel sizing beyond stereo** — landed 2026-05-21.
    Matrix sizing now uses per-line configured channel counts (`PortAudio.ChannelCount`,
    `NDI.AudioChannelCount`, video-only lines = 0) instead of hardcoded stereo.
- [x] **Output preset + transition** (§4.3.5)
  - [x] UI: preset combobox (`AsSource` / `1080p60` / `720p60` / `Custom`)
  - [x] UI: transition combobox + duration NumericUpDown
  - [x] **Preset wired through `CpuVideoCompositor`** (2026-05-21) —
        `OutputPresetVideoSource` letterboxes decoder video into 1080p60 / 720p60
        BGRA before `MediaPlayer.TryOpen` (`videoSourceOverride`).
  - [x] **Custom preset dimensions** (2026-05-21) —
        `PlayerOutputPreset.Custom` now honors `CustomOutputWidth` /
        `CustomOutputHeight` on `MediaPlayerConfig` and `HaPlayFilePlaybackOptions`
        (default 1920×1080, accepted ≥ 16). View shows W/H NumericUpDowns when
        Custom is selected; values propagate through `OutputPresetFormats.TryResolve`
        and into the cue pre-roll cache key. Project round-trip test added.
  - [x] **Fade transition (file video)** (2026-05-21) —
        `PlayerTransitionMode.Fade` wraps the (possibly preset-scaled) source in
        `FadeFromBlackVideoSource` for the configured duration.
  - [x] **Fade transition audio ramp** (2026-05-21) —
        `BeginTransitionAudioFadeIn` ramps the player's compound gain envelope
        from 0→1 over `TransitionDurationMs` on non-cue Play when
        `TransitionMode == Fade`. Cue playback continues to use `BeginCueFades`
        (which takes the envelope and isn't re-entered when a cue envelope is
        already active). No-op for Cut / IdleImage / zero duration.
  - [x] **"Idle image" as a compositor layer instead of `LogoFallbackVideoSink`
        template frame** (§8.10) — landed 2026-05-21. `LogoFallbackVideoSink`
        now keeps each output sink on its negotiated format and re-renders the
        idle image through `CpuVideoCompositor` (letterbox transform) into that
        format, avoiding image-native sink reconfigure/window-size churn.
  - [x] **Preset compositor frame-ownership fix** (2026-05-21) — fixed
        `OutputPresetVideoSource` slot submission path to transfer ownership to
        `CompositorVideoSink` without premature dispose (could present as black
        live-input video). Regression covered by
        `OutputPresetVideoSourceTests.TryReadNextFrame_DoesNotDisposeSourceBeforeComposition`.
  - [x] **Windowed local-output size stability across source/idle-image size changes** —
        landed 2026-05-21. Local preview runtimes no longer resize windows when
        playback or idle image dimensions change; window size now stays user-defined
        unless explicitly edited in Output configuration.
  - [x] **Compositor BGRA layer conversion for live/file presets** (2026-05-21) —
        `CompositorLayerConverter` converts UYVY/NV12/etc. to BGRA32 before
        `CpuVideoCompositor` slots in `OutputPresetVideoSource` and
        `LockedFormatVideoSink` (fixes psychedelic colours when presets/locks
        were active on NDI inputs).

## Phase C.5 — Live Inputs (shared by Players and Cue Player)

- [x] **`MediaPlayer.TryOpenLive(IAudioSource?, IVideoSource?, …)`** (§6.7, §9.6) — landed in framework
- [x] **`PortAudioInput` disconnect detection** (§6.11, §9.6) — landed in framework
- [x] **Playlist items as `FileItem | NDIInputItem | PortAudioInputItem` discriminated union** (§6.8) —
  landed 2026-05-21. `PlaylistItem` polymorphic record (`kind: "file" | "ndi-input" | "pa-input"`) in
  `Models/PlaylistItem.cs`; `PlaylistConfig.Schema` bumped to `HaPlayPlaylist/v2` with the canonical
  `Items` list and a legacy `Paths` fallback so v1 playlist + player-config files still load via
  `PlaylistTabViewModel.FromConfig`. `MediaPlayerViewModel.PlaylistItems` /
  `SelectedPlaylistItem` / `CurrentMediaDisplay` replace the v1 string-path properties on the view
  side; the playlist `ListBox` template now renders `KindGlyph + DisplayName` per row.
- [x] **PortAudio-input item: dialog, data model, playback** (§6.4) — landed 2026-05-21.
  `AddPortAudioInputDialog` mirrors the output dialog (host API → device list → channels → rate);
  `PortAudioDeviceCatalog.EnumerateInputDevices` enumerates capture devices. Playback opens
  via `HaPlayPlaybackSession.TryCreateLive(PortAudioInputPlaylistItem)` → starts
  `PortAudioInput` → `MediaPlayer.TryOpenLive` with `disposeSourcesOnDispose=true`. Device matched
  by name first, with `GlobalDeviceIndex` as fallback so a USB-port swap doesn't break the binding.
- [x] **NDI-input item — discovery dialog** (§6.3) — landed 2026-05-21. `AddNDIInputDialogViewModel`
  spins up `NDIFinder` and polls every 1 s; the dialog shows the discovered list, a Rescan button,
  and a status line. Connection hints (low-bandwidth, audio/video-only, retry interval) round-trip on
  `NDIInputPlaylistItem`.
- [x] **NDI-input item — manual-name path** (§6.3) — landed 2026-05-21. The dialog's "Live-discovered"
  toggle swaps to a free-text source-name TextBox so items can save with names that aren't on the
  network at design time (camera that powers up at showtime, NDI bridge that connects on cue).
- [x] **NDI-input item — "waiting for source" UI** (§6.9) — landed 2026-05-21. When
  `TryCreateLive(NDIInputPlaylistItem)` can't find the source within ~1 s, the player VM enters the
  waiting state: `IsWaitingForSource=true`, `WaitingForSourceMessage` shows the source name +
  retry countdown, the existing loop timer drives reconnect attempts on the item's
  `RetrySeconds` cadence. Stop cancels the retry loop. PortAudio inputs share the same plumbing
  with a 2 s fixed retry (the device either exists or doesn't — no discovery handshake).
- [x] **Live audio matrix auto-extension on NDI source negotiate** (§6.5) — landed 2026-05-21. The
  session tracks `SourceAudioFormat` for both file (decoder format) and live (negotiated capture /
  NDI format) sources. `SourceChannelCountOrFallback(session)` returns the live channel count so
  matrix sizing tracks the negotiated source channels and the line's configured sink channels on session
  open. Saved cells with
  in-range `(input, output)` indices restore via `ApplyConfig`; out-of-range cells are silently
  dropped (matching §6.5's "preserved-but-inactive" promise when reconnecting at the original
  channel count).
- [x] **`NDIVideoReceiver` backfill** (§8.9) — landed 2026-05-21. `NDIVideoReceiver` +
  `NDIVideoFrameUnpack` in `S.Media.NDI`; HaPlay live open wires audio and/or video via
  `NdiInputConnector` + unified `TryCreateLiveCore` (video-only, audio-only, or both).
- [x] **NDI live audio dropouts (jitter buffer + live PortAudio target + rebase)** —
  landed 2026-05-22. Three-part fix: (a) `NDIAudioReceiver` gains a configurable
  `minBufferedDuration` holdback (default 50 ms, see framework checklist entry) so
  bursty NDI delivery no longer trips the router's silence-pad path; (b)
  `HaPlayPlaybackSession.WireAudio` lowers the wrapped PortAudio's
  `TargetQueueSamples` to ~40 ms while a live session is wired (restored on unwire)
  so the router doesn't fire the startup chunk-burst that overflowed the per-sink
  pump and tripped the output-line Warning LED; (c) the receivers expose
  `RebaseToLatest` (audio: advance read pointer; video: drain queue + reset PTS
  counter) and `HaPlayPlaybackSession.RebaseLiveSourcesForPlay()` is called from
  every `Router.Play` site so the connect-to-Play stale-samples backlog is
  discarded and video starts in sync with the freshly-zeroed playback clock
  (without this last piece, audio plays ~1 s late and video sits black waiting
  for the playhead to catch up to the receiver's pre-advanced PTS counter).

## Phase D — Cue Player

- [x] **Cue list data model** (§5.2) — landed 2026-05-21. Added polymorphic
  `CueNode` tree with `CueGroupNode`, `MediaCueNode`, `ActionCueNode`, and
  `CommentCueNode`, including route-connection overrides keyed by virtual output
  channels (`VOut`).
- [x] **`.haplaycues.json` serialization** — landed 2026-05-21. Added `CueListIO`
  (`.haplaycues`) save/load plus project-file round-trip coverage.
- [x] **TreeDataGrid cue list with row editing + grouping** (§5.4) — landed 2026-05-21.
  New `CuePlayerView` replaces the Cues placeholder workspace with a hierarchical
  `TreeDataGrid` (`HierarchicalTreeDataGridSource<CueNodeViewModel>`), inline
  editable row fields (`No.`, `Label`, `Trigger`, `Pre(ms)`, `Source/Action`,
  `Endpoint Id`, `Extra`, `Notes`), group nesting via expander rows, cue-list
  add/remove and `.haplaycues` load/save actions, plus media-file browse action.
  `+ Media` now immediately opens a file picker (cancel-safe) and writes the
  selected path into the new media cue.
  Project save/load now includes cue lists via
  `MainViewModel.BuildProjectSnapshot()` / `ApplyProjectSnapshot()`.
- [x] **Transport: GO / Standby / Pause / Stop / Panic / Back** (§5.3) — landed
  2026-05-21 as CuePlayer transport state in `CuePlayerViewModel` with commands
  + status line in `CuePlayerView` (`StandbySelected`, `Go`, `Back`, `Pause`,
  `Stop`, `Panic`). This is the cue-stack transport layer; per-cue media/action
  execution remains tracked by the items below.
- [x] **CuePlayer virtual output channel registry (`VOut 1..N`)** (§5.2, §5.4) —
  landed 2026-05-21. Added `CueList.VirtualOutputs` persisted in `.haplaycues`
  and project snapshots, with editable `VOut` registry panel in Cue workspace.
  2026-05-21 follow-up: duplicate channel edits now auto-resolve to the next free
  `VOut` number in the active cue list (collision-prevention UX).
- [x] **Per-cue route-connection list (TreeDataGrid)** — landed 2026-05-21.
  `CueRoutesTreeGrid` now edits per-route `Input`, `VOut`, `Gain dB`, `Mute`
  rows on media cues, persisted via `MediaCueNode.RouteConnections`.
  **+ Route** refresh fix 2026-05-21: grid rebuilds when `RouteConnections`
  mutates (collection-change notifications); `+ Route` auto-seeds default
  `VOut 1/2` when a cue list has no registry yet.
- [x] **Typed cue-node editors by kind** — full kind-aware pass landed 2026-05-21.
  - **Trigger** column: hidden ("—" placeholder) for `Comment` rows (comments
    don't fire). Editable for Group / Media / Action.
  - **Pre(ms)** column: hidden for `Comment` rows (no fire → no pre-wait gate).
    Editable for Group / Media / Action.
  - **Source/Action** column: hidden for `Group` and `Comment` rows. Free text
    for Media (file path) and Action (action payload).
  - **Endpoint Id** column: hidden for Group / Media / Comment. Constrained
    endpoint picker on Action rows (with missing-id preservation).
  - **Extra** column: combo for Group (`CueGroupFireMode`) and Action
    (`CueActionKind`); free text for Media; hidden for Comment.
  - **VOut** per-route column: picker bound to the cue list's virtual-output
    registry.
- [x] **Per-cue route + gain + fade + pre-wait** (§5.2) — landed 2026-05-21.
  Pre-wait scheduling, route overrides on GO, fade-in/out NumericUpDown on media
  cues. Audio: `BeginCueFades` ramps `_cueEnvelope` on compound router gain.
  Video fade-in: `MediaCueNode.FadeInMs` via `HaPlayFilePlaybackOptions` /
  `FadeFromBlackVideoSource` at open (included in pre-roll cache key). Video
  fade-out: `_cueVideoOpacity` synced with audio envelope in `RunCueEnvelopeAsync`;
  `LogoFallbackVideoSink` + `VideoCpuOpacity` fade CPU frames toward black (BGRA/RGB,
  NV12/I420/UYVY/YUYV, etc.); high bit-depth / exotic layouts use swscale via BGRA32.
- [x] **Pre-roll cache (next-N cues)** (§5.7) — landed 2026-05-21.
  `CuePreRollCache` warms file media cues from standby; GO adopts via
  `MediaPlayerViewModel.TryPlayCueMediaAsync` when cache key matches (player
  outputs + preset + item). `CueList.PreRollCount` (default 4) + NumericUpDown
  in Cue workspace. File + NDI + PortAudio inputs warmed when idle (`!IsPlaying`).
- [x] **NDI input pre-connect** (§6.11) — landed 2026-05-21. `NdiInputPreConnectCache`
  holds connected `NDIAudioReceiver` instances for upcoming NDI media cues (same
  N window as file pre-roll); GO uses `TryCreateLive(..., preconnectedReceiver)`.
- [x] **PortAudio input pre-connect** (§6.11) — landed 2026-05-21. `PortAudioInputPreConnectCache`
  + `PortAudioInputConnector`; GO adopts pre-started capture via `TryCreateLive(..., preconnectedInput)`.
- [x] **Cue start offset + loop + end behavior** (§5.2) — landed 2026-05-21.
  `StartOffsetMs` seeks after GO; `Loop` / `EndBehavior` drive player looping,
  freeze-on-end (pause, keep last frame), and existing fade-out path.
- [x] **Auto-follow / auto-continue scheduling** (§5.2, §5.6) — landed 2026-05-21.
  `AutoContinue` cues following the GO anchor are included in the same trigger plan;
  `AutoFollow` chains to the next cue when file playback ends naturally
  (`NaturalPlaybackEnded` on the active player). Live media cues also chain on operator
  **Stop** during cue transport, or when PortAudio capture faults / live sources exhaust
  (`HaPlayPlaybackSession.IsLiveSourceDisconnected`).
- [x] **Group-level overrides + "fire all simultaneously" mode** (§5.2) —
  `CueGroupFireMode.FireAllSimultaneously` honored in `BuildTriggerPlan` (incl. tests).
- [x] **Action-cue emitters** (§5.2, §5.6) — execution path shipped 2026-05-21.
  - [x] OSC out via `OSCLib` — landed first execution path 2026-05-21.
    Cue transport now calls a host action executor; `MainViewModel` sends OSC
    packets through `OSCLib.OSCClient` on cue fire. Current format is
    inline-address syntax (`/addr ...args`, `host:port /addr ...args`, or
    `osc://host:port/addr ...args`) with default endpoint `127.0.0.1:9000`, and
    now also supports endpoint-registry lookup by `ActionCueNode.EndpointId`.
  - [x] MIDI out via `PMLib` — landed first execution path 2026-05-21.
    `MainViewModel` now opens a PortMidi output and sends `noteon/noteoff/cc/pc`
    commands from action cues (channel override supported via `chN` / `ch=N`).
    Endpoint-registry lookup by `ActionCueNode.EndpointId` is wired.
  - [x] Multi-target endpoint registry (§12.6) + rebind-missing-endpoints dialog
    — project persistence and runtime lookup are in place
    (`HaPlayProject.ActionEndpoints`). Dedicated sidebar workspaces landed
    2026-05-21: **OSC** (`OscConnectionsView`) for UDP targets, **MIDI**
    (`MidiDevicesView`) for output endpoints + PortMidi catalog. Per-workspace
    **Test connection** / **Test device** actions landed 2026-05-21; broken
    action-cue endpoint references surface via `IsEndpointBroken` after load.
    **Persistent endpoint-health LEDs** in OSC/MIDI list rows landed 2026-05-21
    (`ActionEndpointRowViewModel` + background `RefreshAllEndpointHealthAsync` on
    startup, project open, and registry edits; Test connection re-probes the row).
    **Rebind missing action endpoints** dialog landed 2026-05-21
    (`RebindMissingActionEndpointsDialog` on project load).
  - [x] **Action-cue builder dialog** (§12.2, §5.2) — landed 2026-05-21.
    `ActionCueBuilderDialog` replaces the inline Cue-workspace panel; Cues
    workspace exposes **Edit action…** for the selected action row.
  - [x] Target Configuration unified dialog (§12.2) — landed 2026-05-21.
    View menu now opens `TargetConfigurationDialog` (OSC/MIDI tabs) while
    retaining dedicated OSC/MIDI sidebar workspaces for quick access.

## Phase E — Polish & Follow-ups (§8)

- [x] **§8.1 Output Health Panel** — per-output-line health LEDs landed 2026-05-21
  (`OutputLineHealthEvaluator` + 1 Hz refresh from video/audio pump metrics during playback).
  Inline per-line throughput sparkline (`SparklineControl` reading the 60-tick ring on
  `OutputLineViewModel`) and an aggregate health chip in the Outputs workspace header landed
  2026-05-21; sparkline tints follow the line's health colour, the chip recolors red on any
  warn/error. `EvaluateWithMetrics` returns the raw pump counters so the refresh path can push
  per-second deltas without re-querying.
- [x] **§8.2 Per-Player Headphones Cue Bus** — first implementation landed 2026-05-21:
  per-player headphones cue controls now target a dedicated PortAudio output line with
  independent cue-send gain and selectable tap point (`PreFader` / `PostFader`), while
  reusing the player's matrix for channel-subset selection.
  - **Cross-player shared bus** — landed 2026-05-21. Project-level
    `SharedHeadphonesBus` definitions (`HaPlayProject.SharedHeadphonesBuses`) are managed
    from a new "Shared Headphones Buses" section in the Outputs workspace (add/remove +
    PA-output picker per bus). The per-player target combo
    (`HeadphonesCueTargets` / `SelectedHeadphonesCueTarget`) mixes direct PA lines and
    shared buses in one list; selecting a bus resolves to its backing PA line at runtime
    (broken-bus state shown when the bus has no target). `MediaPlayerConfig.HeadphonesCueSharedBusId`
    persists alongside the existing direct `HeadphonesCueOutputId`; on load, bus id wins.
- [x] **§8.3 Multi-Player Workspace Layout** (split / stacked, persisted) — landed 2026-05-21.
  New `PlayersLayoutMode` (Tabs/Stacked/Split) persists in `AppSettings.PlayersLayout` via the
  same source-gen contract used by Theme/Density. Players workspace header carries a layout combo;
  Tabs keeps the pre-§8.3 single-player + TabStrip flow, Stacked stacks every player vertically in
  a ScrollViewer, Split tiles them in a `UniformGrid` (1 row × N columns). Layout-aware
  `TabsLayoutContent` / `StackedLayoutContent` / `SplitLayoutContent` projections gate each branch's
  `Content` to null when its layout isn't active so the hidden branches don't materialize duplicate
  `MediaPlayerView` instances for the same VM (Avalonia's `IsVisible=false` keeps subtrees in the
  visual tree).
- [ ] **§8.4 OSC / MIDI Remote Control** (inbound bindings — out for this round)
- [x] **§8.5 Drag-and-Drop File Import** (playlist / cue list / quick-play) —
  landed 2026-05-21 for MediaPlayer playlist (`AddDroppedFilesToPlaylist`),
  Cue tree (`AddMediaFilesFromDrop`), and Players workspace quick-play
  (`MainView` drop host → `MediaPlayerViewModel.QuickPlayDroppedFilesAsync`).
- [x] **§8.6 Theme & Density** (light/dark, compact/comfortable) — landed 2026-05-21.
  `AppThemeMode` (System/Light/Dark) + `AppDensityMode` (Compact/Normal) persist in `AppSettings`
  with `JsonStringEnumConverter` so the JSON stays human-readable. `AppearanceController` applies
  them live by setting `Application.RequestedThemeVariant` and the live `FluentTheme.DensityStyle`.
  Project workspace shows a "Display" section with two combos; the choice persists per machine
  (not part of the project file).
- [x] **§8.7 Window State Persistence** (per-machine app settings) — landed 2026-05-21.
  `WindowStateSnapshot` on `AppSettings.MainWindow` round-trips width/height/x/y + maximized flag
  through `%LocalAppData%/HaPlay/app-settings.json`. `MainWindow` code-behind restores on Opened
  (with off-screen clamp against `Screens.All`), captures the last `WindowState.Normal` sample so
  the un-maximized size survives a maximize→quit cycle, debounces geometry writes by 400 ms, and
  flushes synchronously on Closing. Legacy `app-settings.json` files without the new field load
  cleanly with the default placement.
- [x] **§8.8 Recording Sink** (UI side — Record button on NDI outputs) — landed 2026-05-21.
  Outputs list now shows an NDI-only `Record` / `Stop Rec` toggle and `REC` state badge per line
  (`OutputLineViewModel.IsNdiRecording`). This is the planned UI-side control surface; backend
  FFmpeg recording-sink capture remains a framework follow-up.
- [x] **§8.9 NDI Video Receiver Backfill** — landed 2026-05-21 (`NDIVideoReceiver` +
  `HaPlayPlaybackSession` live video wiring).
- [x] **§8.10 Compositor-Based Hold Image** — landed 2026-05-21 via compositor
  idle-image rendering in `LogoFallbackVideoSink` (fixed sink format + letterboxed
  template re-render to negotiated output raster).
- [x] **§8.11 Output Activity Indicators** — landed 2026-05-21 (same LEDs as §8.1 on Outputs list rows).
- [x] **§12.7 String resource plumbing** — `Resources/Strings.resx` + `HaPlay.Resources.Strings`
  accessor landed 2026-05-21. Initial pass covered Media Player / Output Management output-preset,
  idle-image, and shared-headphones-bus UI text. 2026-05-21 follow-up pass moved Main/Cue/OSC/MIDI
  workspace shell text and tooltips into `Strings.resx` as well. 2026-05-21 follow-up: all dialog
  XAML strings (titles/labels/tooltips/placeholders/buttons) and dialog-VM titles/default labels/
  validation messages were extracted. 2026-05-21 follow-up: Output-management headers/menus/tooltips
  and output-line record/health fallback labels were extracted too. 2026-05-21 follow-up: non-dialog
  views are now fully literal-free (`rg` on `Views/*.axaml` inline `Text/Content/Header/ToolTip/Title`
  returns 0), and large VM status/error surfaces were extracted (Main/MediaPlayer/Output-management,
  output-line summaries, matrix helpers, action-endpoint health/probe text). 2026-05-21 follow-up:
  Cue-player grid/code-behind literals (column headers, endpoint placeholders, route mute short label)
  were extracted, closing the remaining Cue-player residuals.

## Cross-Cutting (§9)

- [x] **§9.1 Output line stable Guid identity** — Phase A persisted IDs
- [x] **§9.2 Routing mirror for clones** — Phase B.4 PlayerRoutingMirror
- [x] **§9.3 Audio matrix bridge** — framework multi-route per `(source, sink)` pair
  shipped 2026-05-20 (`AudioRouter.AddRoute(..., routeId, ...)`, `RemoveRouteById`,
  `SetRouteGainById`). HaPlay installs one router route per non-zero matrix cell
  via `HaPlayPlaybackSession.TrySetOutputMatrix`. Click-free gain rides through
  `SetRouteGainById` per cell. 402 framework tests pass; 80 HaPlay tests pass.
- [x] **§9.4 Project file migration** — `schemaVersion=1` in place; `Migrate(JsonNode, from, to)` will be needed at the next bump
- [x] **§9.5 Threading discipline** — `WithPlaybackArcAsync` + bounded inner CTs preserved
  in Phase A/B/C. Seek-on-drag-end commit point verified 2026-05-20 (see Phase C transport
  bar item). New `JogBack` / `JogForward` jog commands route through the same arc.
- [x] **§9.6 Framework gaps** — tracked in
  [`HaPlay-Framework-Gaps-Checklist-2026-05-20.md`](HaPlay-Framework-Gaps-Checklist-2026-05-20.md)
- [x] **§9.7 Testing strategy** — `HaPlay.Tests` has IO + dialog VM coverage,
  Cue VM tests (tree shape, VOut/route edits, GO/standby, auto-continue,
  media-source round-trip), plus output reconfigure hook ordering coverage
  (`ReconfigureLineAsync_RaisesHooks_AroundDefinitionSwap`). 2026-05-21 follow-up
  added pause-aware timing coverage (`Go_AutoContinueDelay_IsDeferredWhilePaused`).
  2026-05-21 follow-up added Avalonia-headless view interaction coverage
  (`CuePlayerViewInteractionTests`: Add Group and Add Route UI flows).

## Cross-Cutting (§12 App Shell + Dialogs + Terminology)

- [x] §12.1 Sidebar — done in B.3
- [x] §12.2 Dialog convention pass — done in B.5
- [x] §12.3 Terminology cleanup — done in B.5
- [x] §12.4 Acceptance — sidebar collapse persists; no Avalonia/SDL strings leak by default
- [x] §12.5 Open Questions (Resolved) — Ctrl+1..N implemented; English-only ship
- [x] §12.6 **Multi-target OSC/MIDI endpoint registry** — project persistence +
  cue endpoint-id binding landed; OSC/MIDI management moved to dedicated sidebar
  entries (2026-05-21). Rebind dialog + list health LEDs landed. Unified
  `TargetConfigurationDialog` (OSC/MIDI tabs) landed 2026-05-21.
- [x] §12.7 **String resource plumbing** — Phase E polish (`Strings.resx`) landed end-to-end:
  Main/Cue/OSC/MIDI workspace + full dialog pass + output-management follow-up + Cue-player
  code-behind literals are extracted.

---

## Currently uncovered / "will land later" — quick index

| Topic | Plan ref | Status | Notes |
|---|---|---|---|
| Idle image via compositor (image-layer) | §8.10 / §4.3.5 | `[x]` | Landed 2026-05-21: idle image is rendered through compositor letterbox into each sink's negotiated format (no image-native sink reconfigure). |
| Output preset → CompositorVideoSink | §4.3.5, framework gap | `[~]` | Preset/lock compositor path + BGRA layer conversion landed 2026-05-21; remaining follow-up is explicit multi-layer program composition (PiP / lower-thirds UI), not single-layer letterbox. |
| Live inputs (NDI / PortAudio) as media items | §6, Phase C.5 | `[x]` | Shipped in Phase C.5 (`PlaylistItem` DU + NDI/PortAudio dialogs + waiting/retry UX). |
| Cue Player view | §5, Phase D | `[x]` | TreeDataGrid workspace + cue-list IO + cue transport + execution/prefetch/action emitters landed; remaining optimization work is tracked under typed kind-specific editors. |
| Virtual output channel routing model (`VOut`) | §4.3.4, §5.2, §9.3 | `[x]` | MediaPlayer labels/route list + Output-management channel assignments + CuePlayer registry/overrides + project persistence landed; cue `VOut` collision auto-resolution landed 2026-05-21. |
| Recording sink, drag-and-drop, theme, etc. | §8 | `[~]` | Most Phase E UI polish landed (health, layout, drag-drop, theme, window state, NDI record button, per-player + shared headphones cue bus). Remaining: remote control (OSC/MIDI inbound — deferred). |
| Per-dialog size persistence | §12.2 | `[x]` | `DialogStatePersister` + `AppSettings.DialogSizes` shipped on resizable dialogs. |
| `Strings.resx` plumbing | §12.7 | `[x]` | Infrastructure + Main/Cue/OSC/MIDI workspace + full dialog pass + Output-management follow-up + Cue-player code-behind literals landed. |
| Rebind missing devices dialog | §7.3 | `[x]` | `RebindMissingOutputsDialog` on project open (2026-05-21). |
| Drag-and-drop import | §8.5 | `[x]` | Playlist + cue tree + Players quick-play (2026-05-21). |

---

## Maintenance

Update this file alongside any change that lands or moves a checklist item. Keep
section ordering aligned with the plan so a reviewer can read plan → checklist
in lockstep. Framework-side work (anything that needs an `S.Media.*` change)
lives in the framework gaps checklist, *not* here.
