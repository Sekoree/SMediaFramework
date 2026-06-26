# 09 — Phase Checklists

Working checklists for the phases in [07](07-Migration-and-Phasing.md). Each item is meant to be
checkable. `(D#)` / `(OQ#)` tags point to the binding decision in [08](08-Open-Decisions.md). A phase is
**done** only when its **Gate** is green and its **Exit** holds.

## Global rules — apply to every phase

- [ ] `dotnet publish -p:PublishAot=true` succeeds for every project touched this phase (D15).
- [ ] Arch-test green: the [01 §3](01-Architecture-and-Principles.md) reference rules hold — deps point
      down only, `Core` has no backend deps (D15).
- [ ] **Zero code** references to the old `MediaFrameworkPlugins.*` global (it is deleted) — registry only (P2).
      Historical mentions in comments/docs that explain the replacement are fine; this rule is about code, not text search.
- [ ] CI green on **Windows + Linux** (D13); no macOS target.
- [ ] The phase's parity gate(s) pass before starting the next phase.
- [ ] Public types added this phase have XML docs stating thread-ownership where relevant (D5/OQ8).

---

## Phase 0 — Scaffold ✅ *(local gates green; CI pending first run)*
**Goal:** an empty `next/` solution that builds, AOT-publishes, and enforces the dependency graph.

- [x] Create `next/`; add `MFPlayer.Next.sln` (D1). *(UI/ mirror deferred to Phase 8.)*
- [x] Create the project graph (20 skeletons): `Core`, `Time`, `Routing`, `Gpu`, `Compositor`, `Players`,
      `Session`; modules `FFmpeg.Common`, `Decode.FFmpeg`, `Encode.FFmpeg`, `Audio.PortAudio`,
      `Audio.MiniAudio`, `Present.SDL3`, `Present.Avalonia`, `NDI`, `Images.Skia`, `Subtitles`;
      `S.Control`, `S.Abi`, `S.Media.Interop`. *(NuGet/native refs deferred to each project's phase.)*
- [x] Wire `ProjectReference`s to match the allowed-reference table ([01 §3](01-Architecture-and-Principles.md)) — nothing upward.
- [x] `next/Directory.Build.props` (net10.0, Nullable, `IsAotCompatible`) + `Directory.Build.targets`
      (isolates next/ from the old root targets) + `next/Directory.Packages.props` (pins incl. **Vortice 3.8.x**, OQ5). Single TFM (D15).
- [x] Carried-forward assembly names preserved where applicable; split/renamed module names are
      intentional; old `MFPlayer.sln` untouched (git-verified); **"one generation per process"** rule
      written into `next/README.md` (D1/OQ6).
- [x] `S.Media.Arch.Tests` asserts the [01 §3](01-Architecture-and-Principles.md) rules (D15) — **verified
      it fails on an injected upward ref**, not vacuously green.
- [x] CI `.github/workflows/next-build.yml`: build + arch-test + AOT-publish on Windows + Linux (D13).
      *(Triggers on push to `next`; first green run pending.)*
- [x] Parity harness: `Tools/AotSmoke` (the AOT gate). Phase-specific smokes (PlaybackSmoke, …) added in their phases.

**Gate:** ✅ `dotnet build` — 22 projects, 0 warnings/0 errors · ✅ arch-test 4/4 · ✅ AOT-publish →
native ELF runs (`MFPlayer.Next AOT smoke OK`) · ✅ old tree untouched. *(Windows AOT leg runs in CI.)*
**Exit:** green CI on both platforms *(pending the first CI run on push)*.

---

## Phase 1 — Core + Time + Routing ✅ *(salvage + new primitives green; 407 tests pass)*
**Goal:** the vocabulary, clocks, and routers (no backends yet).

**Core (slim):**
- [x] Salvaged primitives: frames/formats, `IVideoSource/Output`, `IAudioSource/Output`, `IAudioBackend`,
      capability ifaces, `VideoFormatNegotiator`, `ChannelMap`(+SIMD), HW-backing descriptors, diagnostics, Triggers.
- [x] Registry contracts: `IMediaModule`, `IMediaRegistryBuilder`, `IMediaRegistry`, `IMediaDecoderProvider`
      (**confidence-score** probe D3 + **URI** open D2) + concrete immutable `MediaRegistry`/`MediaRegistryBuilder`.
- [x] Device contract: `IDeviceChangeNotifier` (`SupportsDeviceChangeNotifications` + `DevicesChanged`); caps frozen, devices dynamic (D6/OQ9).
- [x] **Deleted** `MediaFrameworkPlugins`/`Runtime`/`ExtensionRegistry` (P2) + the static open-factories — opening is via the registry.

**Time:**
- [x] Clocks → `S.Media.Time` (`MediaClock`, `CompositePlaybackClock`, `VideoPtsClock`, `OutputSyncGroup`, `VideoPresentSyncGroup`).
- [x] **New:** `SessionClock` (per transport group, D4), `SourceTimeline` (offset + rebase policy), `SourceSyncGroup` (correlated live A/V).

**Routing:**
- [x] `AudioRouter`(+partials), router clocks, `VideoRouter`, pumps, `RetimingVideoOutput`, `SyncPresentVideoOutput` →
      `S.Media.Routing`, **rewired off the global slots to injected factories** (`ResamplerFactory`, `AdaptiveRateWrapper`,
      `VideoRouterOptions` converter) — P2 removed from the routers.

**Session-threading seam:**
- [x] `SessionDispatcher` in Core (`Post` + `InvokeAsync`, no blocking `Invoke`, `IsOnDispatcherThread` — D5/OQ8).

**Placement refinements (during salvage):** `ISyncPresentableVideoOutput`→Core (pure iface); `Triggers` primitives→Core; new
`IStoppableSource` seam replaces the `AudioClipVoice` router coupling. Deferred to their phases: `AudioClip*`,
`AudioTriggerRegistration`, `PlaybackTimelineClockExtensions`(IAvPlaybackSession overload), `VideoPlayer`, Playback/Compositor code.

**Gate:** ✅ `S.Media.Core.Tests` ported — **403 green** (salvaged Core/Time/Routing behavior + new registry/dispatcher/sync-primitive tests) + arch-test **4/4** = **407 total**; full sln 0/0; AOT smoke runs.
⚠️ `TransportSyncProbe` **reclassified to Phase 2** — it depends on `MediaPlayer`/FFmpeg (full stack), not just clocks; Phase 1's
clocks are covered by the ported Clock tests (`MediaClock`/`CompositePlaybackClock`/`OutputSyncGroup`/`VideoPtsClock`).
**Exit:** ✅ Core has no backend deps (arch-test enforced).

---

## Phase 2 — First end-to-end playback ✅ *(audio-first: build-complete + wiring-verified; runtime + test-ports pending)*
**Goal:** a file plays — HW-decoded, audio-synced — entirely through the registry, no globals.

> **Scope decision:** Phase 2 is **audio-first**. `Present.SDL3` depends on `S.Media.Gpu` **and**
> `S.Media.Effects`, so on-screen video belongs with Phase 3 — **`Present.SDL3`, `VideoPlaybackSmoke`,
> and the `VideoPlayer`/`AvPlaybackCoordinator` salvage move to Phase 3**. Video still *decodes* in
> Phase 2 (`FrameDump`). The FFmpeg adaptive-rate output wrapper couples to `AudioRouter` and is only
> needed for non-master outputs → **defers to Phase 5** (keeps `Decode.FFmpeg` free of `Routing`).

- [x] **`FFmpeg.Common`** — native init, error helpers, AVIO bridge, pixel-format mapping, memory helpers.
- [x] **`Decode.FFmpeg`** — demux + audio/video decode (hw decode, swscale converter, yadif, source
      resampler) + **`FFmpegModule`** / **`FFmpegDecoderProvider`** (confidence + URI, D2/D3) replacing
      the old global slots. The two player/session orchestrators (`MediaContainerSession`/`…PlaybackBundle`)
      deferred to Phase 4.
- [x] **`Audio.PortAudio`** (+ **`PALib`** into `next/`) — `IAudioBackend` (clocked output) + **`PortAudioModule`**;
      the old FFmpeg/Playback couplings dropped/deferred. Device enumeration poll-based (OQ9).
- [x] **`Players.MediaPlayer`** — slim audio-first, registry-driven: open URI → `AudioRouter` → PortAudio
      master output → `MediaClock` (D11). No concrete backend refs.
- [x] **Composition roots:** `Tools/PlaybackSmoke` (audio) + `Tools/FrameDump` (video decode).
- [ ] *(deferred)* port `…FFmpeg.Tests` / `…PortAudio.Tests` (need natives to run → user/CI).

**Gate:** ✅ full sln builds 0/0; arch-test 4/4 + Core.Tests 403; **wiring verified** — `PlaybackSmoke`
runs, loads native FFmpeg + PortAudio, registers both modules, and dispatches `OpenAudio` through the
registry (fails only on a bogus path, as expected). **Pending your runtime test** with real media files
(`dotnet run --project next/MediaFramework/Tools/PlaybackSmoke -c Release <file>`), plus the deferred
unit-test ports. `VideoPlaybackSmoke` / `TransportSyncProbe` → Phase 3 (need video / full coordinator).
**Exit:** a file plays HW-decoded and synced with zero static plugin state *(audio confirmed once you test)*.

---

## Phase 3 — GPU + Compositor + Players complete
**Goal:** GPU compositing with mesh warp + multi-output — compositor free of FFmpeg.

- [x] Move `S.Media.OpenGL` → `S.Media.Gpu` (4.7k LOC); one GL context **per render thread**
      (`SharedSdlGlContext`) (D7). Builds 0/0, AOT-analyzer clean.
- [x] Move `S.Media.Effects` → `S.Media.Compositor` (5k LOC); **dropped the FFmpeg projref** — CPU
      converter injected via `VideoCompositorOptions.CpuFrameConverterFactory` (registry-wired
      `IVideoCpuFrameConverter`); `StaticFrameSource` uses Core's `VideoFrameCpuClone`; the deleted
      `VideoCpuFrameConverterRegistry` replaced by the same factory (P3 fix). Compositor = [Core, Gpu],
      **zero FFmpeg refs**. The compositor's clock is now a `Func<TimeSpan>` `MasterTimeProvider` so it
      stays off `S.Media.Time`.
- [x] Move `S.Media.SDL3` → `Present.SDL3` (output, [Core, Gpu]) **+** new `Present.SDL3.Compositor`
      bridge ([Core, Gpu, Compositor, Present.SDL3]) for the GL compositor backend (decision: keep
      presenters output-only; arch-test updated). Old global `MediaFrameworkRuntime` registration dropped
      (P2) — wire `SDL3GLVideoCompositor.TryCreate` via `VideoCompositorOptions.AutoBackends`.
- [x] `ICompositorRegistryBuilder.AddLayerSurface` extension (05): `IVideoCompositorLayerSurface`
      (GL `ConfigureGl`/`Render`, mirrors `MfpLayerSurfaceVTable`) + `ICompositorRegistry`/`Builder`
      (scoped, no globals). Registration seam **+ GL render-into-canvas wiring** done —
      `GlVideoCompositor.CompositeWithSurfaces` renders surface layers on top of the frame layers into the
      canvas FBO; verified on real GL (`CompositeTargetsSmoke`: a surface fills the centre green over a red frame).
- [x] Layers (video/image/text/plugin-surface), transforms/zoom/opacity/blend, transitions; mesh warp
      (`WarpMesh`/`WarpSection`, 2×2 = corner-pin) — moved intact with the `S.Media.Effects` salvage.
- [x] `CompositeMulti` three target kinds (04 §4) — `CompositeMultiToTargets` + `ICompositeOutputTarget`:
      **`GlCompositeTarget`** (zero-copy GL→GL blit) and **`CpuFrameCompositeTarget`** (readback) both verified
      on real GL (`CompositeTargetsSmoke`). **`ExternalImageCompositeTarget`** dmabuf export implemented
      (`EGL_MESA_image_dma_buf_export`) and produces a well-formed handle (fd/fourcc/stride) on real Mesa;
      `MfpSync` currency in `ExternalImageHandle` (OQ2). *Caveat:* radeonsi exports a tiled/`INVALID`-modifier
      buffer that isn't portably re-importable without modifier negotiation — the working cross-API round-trip
      pairs with the Phase-5 Avalonia consumer + the Windows D3D11-NT-handle leg (untestable on this box).
      **NDI is not an external-image target** (OQ3).
- [x] Working color space **auto** (8-bit SDR / RGBA16F HDR), chosen at `Configure`/graph-rebuild, with
      promote-eager/demote-at-boundary hysteresis (D12/OQ7) — `CompositorWorkingSpaceController` + tests.
- [x] `Players` complete: `VideoPlayer` + `AvPlaybackCoordinator` salvaged → `S.Media.Players`; multi-output
      fan-out + mid-stream format-change reconfig (`VideoRouter`); verified on real h264 + broken-PTS media.

**Gate:** ✅ `GlProbe` (real AMD GL 4.6/Mesa) · ✅ `CompositorSmoke` (GL composite + readback pixel-perfect,
**FFmpeg absent**) · ✅ `CompositeTargetsSmoke` (zero-copy GL target + CPU target + layer surface + dmabuf
export, all on real GL) · ✅ `FormatSwitchProbe` (router reconfigures output mid-stream) · ✅ `…OpenGL.Tests`
→ `S.Media.Gpu.Tests` (114) · ✅ `VideoPlaybackSmoke` (decode→sync→present at exact 24fps on real h264 +
broken-PTS `mambo.mp4`) · full sln **0/0**, **540 tests**, arch-test enforces the new bridge.
**Exit:** ✅ mesh-warp/keystone splitting + one-canvas→many-outputs work on GPU; zero-copy GL fan-out proven.
*Open (Phase-5-coupled):* portable external-image (dmabuf modifier negotiation + D3D11/Windows) zero-copy
into a foreign API, and a sync-fd fence upgrade for `ExternalImageCompositeTarget` (OQ2).

---

## Phase 4 — Session (the show, headless)
**Goal:** cues / soundboard / output-mapping in one headless home — collapse the P1 duplication.
**Status (2026-06-26):** framework spine **done + gated** (cue engine merged, `ShowSession` + `ShowDocument`,
cue→video-composition + output-mapping, 594 tests). **No Phase-4-only work remains.** The two items below
that aren't framework work have been **re-filed to their proper phases** — routing/multi-track → **Phase 5**,
the UI god-object collapse → **Phase 8** — because each depends on a later phase (decode-layer/multi-output,
and the UI port) rather than being a finishing touch here.

- [x] Merge `S.Media.Playback` (`ClipCompositionRuntime`, `ClipStandbyEngine`, `Soundboard`, `CueGraph`,
      `RoutingScene`, `MediaSession`, …) into `S.Media.Session`. **Done** — 13 files decoupled
      (FFmpeg→registry, Effects→Compositor); `MediaPlayer` itself decoupled to `S.Media.Players` (the
      container-open/`MediaContainerPlaybackBundle` half removed; new registry `MediaPlayer.OpenFile(registry,…)`
      builder). Genlock `AdaptiveRateAudioOutput` deferred to Phase 5 per its own note.
- [→] **Move out of the UI** (P1) — **re-filed to Phase 8 (UI port).** The playback *engine* embedded in the
      UI god-objects (`CuePlaybackEngine` 2425 LOC, `HaPlayPlaybackSession`(+`OutputWiring`), `SoundboardEngine`)
      is now the headless `ShowSession` + `CueGraph` + `Soundboard` + `ClipCompositionRuntime` — **built fresh,
      not ported**, so the framework duplication is already collapsed. What's left isn't a framework pass: the
      old god-objects live in `./UI/HaPlay/` (next/ has no UI yet), and auditing them for any engine logic
      `ShowSession` still lacks + retiring them happens *as the UI is rebuilt on `ShowSession`* (Phase 8).
- [x] Public `ShowSession` on the dispatcher (Post/InvokeAsync, immutable snapshots, reentrancy guard —
      D5/OQ8); one `SessionClock` **per transport group** (D4); per-group master output (D11). **Done**
      — shared `SessionDispatcher`, `AsyncLocal` reentrancy guard, `TransportSnapshot` + cue-log immutable
      queries; no live mutable `CueGraph` escape hatch from `ShowSession`.
- [x] `ShowDocument` persistence — **STJ source-gen**, AOT-safe, loads headless (D10). **Done** —
      `ShowDocumentJsonContext`; `SessionSmoke` round-trips the show through JSON before driving it.
- [x] Output map = binding → warp sections. **Done (affine, headless)** — cue→video-composition wired
      (composition-bound clip mints a `ClipCompositionRuntime` layer, opened with the layer output as the
      video negotiation lead; CPU compositor composites it). `ShowComposition.OutputMapping` carries a
      `ClipOutputMappingSpec` applied at build + `ShowSession.ApplyCompositionMappingAsync` updates it live;
      `SessionSmoke` composites through an affine section headless. *Mesh **warp** stays GL-only (verify
	      under xvfb).*
- [→] Routing scene = N→M channel remap + **multi-track select** — **re-filed to Phase 5.** *Update
      (2026-06-25, review fix): the decode-layer + `MediaPlayer` track-select plumbing is now **wired** —
      `Audio`/`VideoSourceOpenOptions` carry a stream index, `MediaPlayer.TryOpen` forwards it (and honours
      `DisabledStreamIndex` to disable a stream), and `FFmpegModule` elects the chosen streams. What stays
      Phase 5: exposing it per-clip in `ShowDocument`/`ShowClipBinding` + the N→M multi-output matrix, driven
      from `ShowSession`.*

**Gate:** `SoundboardSmoke` + new `SessionSmoke` (headless cue fire / seek / GO + video composite) green;
`S.Media.Session.Tests`. ✅ Both smokes green on real hardware (2026-06-25); **594 unit tests** (incl.
26-test `S.Media.Session.Tests`: CueGraph / ShowDocument / ShowSession dispatcher) + arch-test green.
**Exit:** a full show runs headless with no Avalonia dependency. ✅ **Proven** — `SessionSmoke` loads a
JSON show and drives GO → fire → seek → GO → switch-clip with zero Avalonia on the path.

---

## Phase 5 — Live + multi-output + more backends
**Goal:** live sources and stitched outputs hitting the sync targets.

**Status (2026-06-26):** the three **backend modules are salvaged, build 0/0, and registry-wired** (no
globals, P2). Native wrappers `MALib` + `NDILib` brought into `next/`. Verified at runtime by the new
`BackendsSmoke`: FFmpeg + NDI decoders and PortAudio + miniaudio backends all register, both audio
backends enumerate devices (PortAudio 14/11, miniaudio 4/5), and the NDI runtime initialises (libndi
6.3.2.0). **Paused here for review** — the cross-cutting items (live convergence, multi-output sync,
N→M routing) and the per-backend refinements noted below are the next slice.

- [~] `NDI` module — **receiver + discovery done**: `NDISource`/receiver expose `IVideoSource`/`IAudioSource`;
      `NDIDecoderProvider` claims the `ndi:` scheme (`ndi://<name>`), discovery via `NDISource.Find`
      (`find_wait_for_sources`, OQ9); `NDIModule` acquires the ref-counted runtime. *Remaining:* the
      **sender** wired as a `CpuFrameCompositeTarget` (SDK send is CPU `p_data` — OQ3) + one-`NDISource`→
      `SourceSyncGroup` A/V correlation (live slice). Sender code (`NDIVideoSender`/`NDIOutput`) is salvaged
      + building.
- [~] `Audio.MiniAudio` module — **`IAudioBackend` done**: `MiniAudioModule` registers it (lazy native load);
      enumerates 4 out / 5 in on this box. *Remaining:* **native** device-change events
      (`ma_device_notification_type`, incl. `rerouted` — OQ9) via `IDeviceChangeNotifier`.
- [~] `Present.Avalonia` module — **`OpenGlControlBase` done**: `VideoOpenGlControl : OpenGlControlBase,
      IVideoOutput` salvaged, shares `S.Media.Gpu`'s `YuvVideoRenderer` with the SDL3 presenter; builds
      against Avalonia 12 (CPU-upload path). *Remaining:* zero-copy external-image import
      (`IGlContextExternalObjectsFeature`, D7/OQ2); runtime needs a display.
- [~] Live convergence: NDI/mic on `SourceTimeline` + `SourceSyncGroup` over the session master (03);
      first-class per-source offset. **Mechanism done + verified:** `LiveTimelineDriver` (S.Media.Time) is
      the consumer that anchors sender↔master on the first frame, maps each frame to a master due-time, and
      collapses drift (RebaseToLatest) — `SourceTimeline.Offset` is the first-class per-source phase trim;
      `SourceSyncGroup` keeps correlated NDI A/V in their sender relationship. 9 unit tests; **verified
      against the live OBS NDI source** by `LiveReceiveProbe` (2560×1440@60: schedule lead held at
      ~18 ms ≈ 1 frame, range [0, 21.5]ms, **no drift over 16 s**, 1 warm-up anchor — meets 03 §7).
      **On screen too:** `ILiveVideoSource.RebaseToLatest` seam (Core) implemented on the NDISource video
      adapter; `LivePlaybackSmoke` plays the OBS source in an SDL3 window via `VideoPlayer` Scheduled — **ran
      on the display at 60 fps, 3 late drops in the first second then zero**. *Remaining:* a packaged live
      `MediaPlayer.OpenLive` (the smoke wires it by hand) + mic input.
- [~] Multi-output: independent-output **fan-out done + verified on screen** — `MediaPlayer.AttachVideoOutput`
      → `VideoRouter` fans one source to N outputs; `MultiOutputSmoke` drove **two phase-locked SDL3 windows
      at 1080p60, 722 frames/12 s, zero late drops** (both present on the one master VideoTick). The
      `VideoPresentSyncGroup`/`OutputSyncGroup` genlock primitives + `SyncPresentVideoOutput` adapter are
      built + unit-tested for the independent-pump case. *Remaining:* `CompositeMulti` per-output crops for a
      single stitched canvas across outputs + the 1-hour drift soak; the texture-mirror zero-copy path is
      CPU-frame-only on this Mesa/EGL setup (independent outputs are the portable path).
- [~] **(Re-filed from Phase 4)** Routing scene = N→M channel remap + multi-track select. **Session model
      done + tested:** `ShowClipBinding.AudioStreamIndex` selects an audio track (03 §6) and is wired into
      `ShowSession.PlayClipAsync` → `MediaPlayer` open options (decode track-select was already wired);
      `OutputPatchRoute.ChannelMatrix` carries the N→M remap as serializable show data with
      `ToChannelMap()` materializing it (round-trips through `ShowDocument` JSON). +3 Session tests.
      *Remaining:* drive the N→M matrix across the **multi-output** path below (clip→route application over
      several outputs) — depends on the multi-output integration.

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
- [ ] **(Re-filed from Phase 4)** Retire the old playback god-objects (`CuePlaybackEngine` 2425 LOC,
      `HaPlayPlaybackSession`, `SoundboardEngine`): their engine is superseded by the headless `ShowSession`
      built in Phase 4 — audit each for any logic `ShowSession` still lacks, then delete it as its workspace
      is strangled onto `ShowSession`.
- [ ] UI persists only **view-state** on top of Session's `ShowDocument` (D10).
- [ ] Strangle the old app **workspace by workspace**; old + new never share a process (OQ6).

**Gate:** `HaPlay.Tests` ported; manual parity per workspace.
**Exit:** old HaPlay retired; the new UI is at feature parity.

---

## Ordering & framework definition-of-done

Phases 1–3 are the spine (a file plays HW-decoded + GPU-composited + perfectly synced, no globals);
4–5 restore the show/live feature set; 6–7 add the new capabilities + C ABI; 8 is the UI, last and
decoupled. The framework "done" bar is [07 §6](07-Migration-and-Phasing.md#6-definition-of-done-framework).
