# 09 — Phase Checklists

Working checklists for the phases in [07](07-Migration-and-Phasing.md). Each item is meant to be
checkable. `(D#)` / `(OQ#)` tags point to the binding decision in [08](08-Open-Decisions.md). A phase is
**done** only when its **Gate** is green and its **Exit** holds.

## Global rules — apply to every phase

- [ ] `dotnet publish -p:PublishAot=true` succeeds for every project touched this phase (D15).
- [ ] Arch-test green: the [01 §3](01-Architecture-and-Principles.md) reference rules hold — deps point
      down only, `Core` has no backend deps (D15).
- [ ] **Zero** references to the old `MediaFrameworkPlugins.*` global — registry only (P2).
- [ ] CI green on **Windows + Linux** (D13); no macOS target.
- [ ] The phase's parity gate(s) pass before starting the next phase.
- [ ] Public types added this phase have XML docs stating thread-ownership where relevant (D5/OQ8).

---

## Phase 0 — Scaffold
**Goal:** an empty `next/` solution that builds, AOT-publishes, and enforces the dependency graph.

- [ ] Create `next/` mirroring `MediaFramework/` + `UI/`; add `MFPlayer.Next.sln` (D1).
- [ ] Create empty projects: `Core`, `Time`, `Routing`, `Gpu`, `Compositor`, `Players`, `Session`;
      module skeletons `FFmpeg.Common`, `Decode.FFmpeg`, `Encode.FFmpeg`, `Audio.PortAudio`,
      `Audio.MiniAudio`, `Present.SDL3`, `Present.Avalonia`, `NDI`, `Images.Skia`, `Subtitles`;
      `S.Control`, `S.Abi`, `S.Media.Interop`.
- [ ] Wire `ProjectReference`s to match the allowed-reference table ([01 §3](01-Architecture-and-Principles.md)) — nothing upward.
- [ ] `next/Directory.Packages.props`: Avalonia 12.x, SDL3-CS, Silk.NET.OpenGL/Core, FFmpeg.AutoGen 8.x,
      SkiaSharp, Mond, CommunityToolkit.Mvvm, STJ, **Vortice 3.8.x** (OQ5). Single TFM `net10.0` (D15).
- [ ] Identical assembly names to old; confirm old `MFPlayer.sln` still builds untouched; write the
      **"one generation per process"** rule into the repo README (D1/OQ6).
- [ ] Add the **arch-test** project asserting the [01 §3](01-Architecture-and-Principles.md) rules (D15).
- [ ] CI: `publish-aot` smoke + arch-test on Windows + Linux (D13).
- [ ] Create empty/stubbed parity-harness apps to fill in later (`PlaybackSmoke`, `VideoPlaybackSmoke`,
      `CompositorSmoke`, `SessionSmoke`, `TransportSyncProbe`, `FormatSwitchProbe`, `GlProbe`, …).

**Gate:** solution builds + AOT-publishes empty; arch-test green; old `MFPlayer.sln` still builds.
**Exit:** green CI on both platforms.

---

## Phase 1 — Core + Time + Routing
**Goal:** the vocabulary, clocks, and routers (no backends yet).

**Core (slim → ~6k LOC):**
- [ ] Move in: frames/formats, `IVideoSource/Output`, `IAudioSource/Output`, `IAudioBackend`, capability
      ifaces, `VideoFormatNegotiator`, `ChannelMap`(+SIMD), HW-backing descriptors, diagnostics.
- [ ] Registry contracts: `IMediaModule`, `IMediaRegistryBuilder`, `IMediaRegistry`,
      `IMediaDecoderProvider` with a **confidence-score** probe (D3) and **URI** open
      (`CanOpen`/`TryOpen*` take a URI — D2).
- [ ] Device contract: backend `SupportsDeviceChangeNotifications` + a uniform `DevicesChanged`; caps
      frozen, devices dynamic (D6/OQ9).
- [ ] **Delete** the `MediaFrameworkPlugins`/`Runtime`/`ExtensionRegistry` concept (replaced).

**Time:**
- [ ] Move clocks → `S.Media.Time`. Add `SessionClock` (one **per transport group** — D4),
      `SourceTimeline` (offset + rebase policy), `SourceSyncGroup` (correlated live A/V), keep
      `OutputSyncGroup`/`VideoPresentSyncGroup`.

**Routing:**
- [ ] Move `AudioRouter`(+matrix/pump/playback), router clocks, `VideoRouter`, `VideoOutputPump`,
      `RetimingVideoOutput`, `SyncPresentVideoOutput` → `S.Media.Routing`.

**Session-threading seam (used by later tiers):**
- [ ] Land the dispatcher primitives: `Post` + `InvokeAsync`, **no blocking `Invoke` from a dispatcher
      callback** + a debug reentrancy guard (D5/OQ8).

**Gate:** `S.Media.Core.Tests` ported green; `TransportSyncProbe` builds against the new clocks.
**Exit:** Core has no backend deps (arch-test); LOC trending toward ~6k.

---

## Phase 2 — First end-to-end playback
**Goal:** a file plays — HW-decoded, audio-synced — entirely through the registry, no globals.

- [ ] Split `FFmpeg.Common` first (runtime init, native load, error/status, stream/format mapping).
- [ ] `Decode.FFmpeg` module: file/stream `IVideoSource`+`IAudioSource`, hw decode (D3D11VA/VAAPI),
      swscale `IVideoCpuFrameConverter`, yadif, resampler, audio-track enumeration; registers a decoder
      provider for `file:`/`http:` with a confidence score (D2/D3).
- [ ] `Audio.PortAudio` module: `IAudioBackend` (output+input), clocked output; device enumeration is
      **poll-based** (PortAudio has no hot-plug callback — OQ9).
- [ ] `Present.SDL3` module: `IVideoOutput` on its own thread, sharing the per-thread GL context (D7).
- [ ] `Players.MediaPlayer`: takes `IMediaRegistry`/`IMediaSourceResolver` (no concrete backend refs —
      02 Tier 4); transport (play/pause/seek/rate); designates a **master output and mixes at its
      rate** (D11).
- [ ] Test composition root: `MediaRegistry.Build(b => b.Use(FFmpeg).Use(PortAudio).Use(Sdl3))`.

**Gate:** `PlaybackSmoke` + `VideoPlaybackSmoke` play a file at parity; `…FFmpeg.Tests`,
`…PortAudio.Tests` green; A/V lip-sync **< ±1 frame** ([03 §7](03-AV-Sync-Clocks-Routing.md)).
**Exit:** a file plays HW-decoded and synced with zero static plugin state.

---

## Phase 3 — GPU + Compositor + Players complete
**Goal:** GPU compositing with mesh warp + multi-output — compositor free of FFmpeg.

- [ ] Move `S.Media.OpenGL` → `S.Media.Gpu`; one GL context **per render thread** (`SharedSdlGlContext`
      pattern) (D7).
- [ ] Move `S.Media.Effects` → `S.Media.Compositor`; **drop the FFmpeg projref** — use
      `IVideoCpuFrameConverter` from the registry (P3 fix).
- [ ] `ICompositorRegistryBuilder.AddLayerSurface` extension (05); layer-surface seam runs in the
      compositor's context.
- [ ] Layers (video/image/text/plugin-surface), transforms/zoom/opacity/blend, transitions; mesh warp
      (`WarpMesh`/`WarpSection`, 2×2 = corner-pin).
- [ ] `CompositeMulti` with three target kinds (04 §4): `GlCompositeTarget` (zero-copy),
      `ExternalImageCompositeTarget` (dmabuf/D3D11 + **negotiated sync** — OQ2), `CpuFrameCompositeTarget`
      (readback). **NDI is not an external-image target** (OQ3).
- [ ] Working color space **auto** (8-bit SDR / RGBA16F HDR), chosen at `Configure`/graph-rebuild, with
      promote-eager/demote-at-boundary hysteresis (D12/OQ7).
- [ ] `Players` complete: multi-output fan-out, seek/rate, mid-stream format-change reconfig.

**Gate:** `CompositorSmoke`, `GlProbe`, `FormatSwitchProbe`, `…OpenGL.Tests` green; **compositor builds
+ runs with FFmpeg absent**.
**Exit:** mesh-warp/keystone splitting + one-canvas→many-outputs work on GPU.

---

## Phase 4 — Session (the show, headless)
**Goal:** cues / soundboard / output-mapping in one headless home — collapse the P1 duplication.

- [ ] Merge `S.Media.Playback` (`ClipCompositionRuntime`, `ClipStandbyEngine`, `Soundboard`, `CueGraph`,
      `RoutingScene`, `MediaSession`, …) into `S.Media.Session`.
- [ ] **Move out of the UI** (P1): `CuePlaybackEngine`, `HaPlayPlaybackSession`(+`OutputWiring`),
      `SoundboardEngine`, group-seek barrier, output-mapping/warp wiring → Session, **de-Avalonia'd**.
- [ ] Public `ShowSession` on the dispatcher (Post/InvokeAsync, immutable snapshots, reentrancy guard —
      D5/OQ8); one `SessionClock` **per transport group** (D4); per-group master output (D11).
- [ ] `ShowDocument` persistence — **STJ source-gen**, AOT-safe, loads headless (D10).
- [ ] Output map = binding → warp sections; routing scene = N→M channel remap + multi-track select.

**Gate:** `SoundboardSmoke` + new `SessionSmoke` (headless cue fire / seek / GO) green; `…Playback.Tests`.
**Exit:** a full show runs headless with no Avalonia dependency.

---

## Phase 5 — Live + multi-output + more backends
**Goal:** live sources and stitched outputs hitting the sync targets.

- [ ] `NDI` module: sender (video+audio) + receiver (`IVideoSource`/`IAudioSource`). **NDI video out =
      `CpuFrameCompositeTarget`** (SDK send is CPU `p_data` — OQ3). Source discovery via
      `find_wait_for_sources` (**native** change events — OQ9).
- [ ] `Audio.MiniAudio` module: `IAudioBackend`; **native** device events (`ma_device_notification_type`,
      incl. `rerouted` — OQ9).
- [ ] `Present.Avalonia` module: `OpenGlControlBase`; zero-copy via external-image import
      (`IGlContextExternalObjectsFeature`) where the backend allows, else re-upload fallback (D7/OQ2).
- [ ] Live convergence: NDI/mic on `SourceTimeline` + `SourceSyncGroup` over the session master (03);
      first-class per-source offset.
- [ ] Multi-output: `CompositeMulti` + `VideoPresentSyncGroup` for stitched/combined outputs.

**Gate:** `NDIPlayer`/`NDIReceiver`; multi-output drift soak (1 hr, no unbounded drift); live A/V-sync
targets ([03 §7](03-AV-Sync-Clocks-Routing.md)).
**Exit:** file + live composited on one canvas, in sync, across phase-locked outputs.

---

## Phase 6 — Subtitles + Control + native plugin host
**Goal:** subtitles, data-driven control, and the C-ABI plugin host.

**Subtitles:**
- [ ] `S.Media.Subtitles`: text (SRT/VTT) via Skia; **ASS/SSA via `LibAssLib`** (new Tier-0 wrapper)
      bundling libass + FreeType + FriBidi + HarfBuzz + font provider (fontconfig/DirectWrite) (D14/OQ4);
      bitmap (PGS/DVB) via `Decode.FFmpeg` capability; `SubtitleLayerSource` aligned to master;
      none/one/many selection.

**Control:**
- [ ] Move `S.Control` engine; X32/XTouch → **data-driven profiles** + control registry (P6); X32 meter
      decode as a registered capability.
- [ ] Mond host API targets the `ShowSession` action façade (headless-drivable).

**Plugin host (`S.Abi`) — the forever-surface:**
- [ ] Define `include/mfp_plugin.h`: **full vtable surface** — source/output/audio-backend/layer-surface
      (GL)/subtitle/control-decoder (D9).
- [ ] **Per-kind tagged frame union** incl. GPU handles: `MfpCpuFrame`/`MfpDmaBufFrame`/`MfpD3D11Frame`/
      `MfpGlTextureFrame` (GL = same-context only), mirroring Core's `Dmabuf*`/`Win32Shared*` (D8/OQ1).
- [ ] **Negotiated `MfpSync`** (keyed-mutex / semaphore / fence via capability query — OQ2).
- [ ] Managed adapters → scoped registries; ABI version gate; append-only structs.
- [ ] **Per-platform conformance plugin** exercising every vtable, in CI (D8/D9).
- [ ] ⚠️ **Review `mfp_plugin.h` hard before tagging v1** — it's expensive to change after (OQ1/D9).

**Gate:** subtitle render test; `OSCLib.Tests`/`PMLib.Tests`; sample C-ABI plugin loads + provides a
source **and** a control decoder.
**Exit:** a third-party native plugin adds a video source + a GL layer surface without touching the host.

---

## Phase 7 — Outbound C ABI
**Goal:** `s_media_player` drives the new session.

- [ ] Retarget `S.Media.Interop`: init **builds a registry** (`UseFFmpeg().UsePortAudio().UseMiniAudio()…`)
      and drives the new `ShowSession`; keep ABI conventions (opaque handles, status codes, 100-ns ticks).
- [ ] Update `s_media_player.h` to the new session surface; keep it stable/append-only.

**Gate:** C-ABI smoke (open / play / close via `s_media_player.h`) green on Windows + Linux.
**Exit:** a headless host can run a show entirely through the C ABI.

---

## Phase 8 — UI port (separate effort, firewalled)
**Goal:** rebuild HaPlay on `ShowSession`; retire the old app.

- [ ] `HaPlay.Core` / `HaPlay.Controls` / `HaPlay.App` / `HaPlay.Desktop` — thin MVVM over `ShowSession`.
- [ ] Decompose the god-VMs (`MediaPlayerViewModel`, `ControlWorkspaceViewModel`, `CuePlayerViewModel`) (P5).
- [ ] UI persists only **view-state** on top of Session's `ShowDocument` (D10).
- [ ] Strangle the old app **workspace by workspace**; old + new never share a process (OQ6).

**Gate:** `HaPlay.Tests` ported; manual parity per workspace.
**Exit:** old HaPlay retired; the new UI is at feature parity.

---

## Ordering & framework definition-of-done

Phases 1–3 are the spine (a file plays HW-decoded + GPU-composited + perfectly synced, no globals);
4–5 restore the show/live feature set; 6–7 add the new capabilities + C ABI; 8 is the UI, last and
decoupled. The framework "done" bar is [07 §6](07-Migration-and-Phasing.md#6-definition-of-done-framework).
