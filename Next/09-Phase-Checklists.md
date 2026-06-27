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
- [→] Routing scene = N→M channel remap + **multi-track select** — **re-filed to Phase 5, done there.**
      *Decode + `MediaPlayer` track-select wired by a review fix (2026-06-25); Phase 5 then exposed it
      per-clip (`ShowClipBinding.AudioStreamIndex` → `PlayClipAsync`) and added the N→M
      `OutputPatchRoute.ChannelMatrix` data model (round-trips JSON). The only N→M residue is the
      multi-output application hook — tracked in Phase 5.*

**Gate:** `SoundboardSmoke` + new `SessionSmoke` (headless cue fire / seek / GO + video composite) green;
`S.Media.Session.Tests`. ✅ Both smokes green on real hardware (2026-06-25); **594 unit tests** (incl.
26-test `S.Media.Session.Tests`: CueGraph / ShowDocument / ShowSession dispatcher) + arch-test green.
**Exit:** a full show runs headless with no Avalonia dependency. ✅ **Proven** — `SessionSmoke` loads a
JSON show and drives GO → fire → seek → GO → switch-clip with zero Avalonia on the path.

---

## Phase 5 — Live + multi-output + more backends
**Goal:** live sources and stitched outputs hitting the sync targets.

**Status (2026-06-26):** the three **backend modules are salvaged, build 0/0, and registry-wired** (no
globals, P2; native wrappers `MALib` + `NDILib` brought into `next/`; `BackendsSmoke` confirms all
register + enumerate, libndi 6.3.2.0 inits). The **cross-cutting items then landed and were verified**
(see the per-item notes): live convergence (`LiveTimelineDriver` + OBS-NDI `LiveReceiveProbe`, no drift
over 16 s), multi-output fan-out (`MultiOutputSmoke`, 2× phase-locked 1080p60), and the N→M routing data
model — **614 tests** green. **Phase 5 DONE (2026-06-26):** Present.Avalonia runtime-verified (`AvaloniaVideoSmoke`,
119 frames; zero-copy wired via the shared SDL3 dma-buf path, Avalonia EGL hardware-blocked on this radeonsi), and
the **1-hour multi-output soak PASSED** — `MultiOutputSmoke` ran a full hour, 107,895 frames, 0 late drops, both
outputs in sync throughout. Gates green: arch-test 4/4, AOT publish (`AotSmoke` → native binary), build 0/0. SDL3
moved off the deprecated `SDL3-CS.Native` to the split `SDL3-CS.Linux`/`.Windows` (3.4.10.4). Only **Windows CI**
remains to be greened (deferred by the user) — no code. Next: Phase 6 (subtitles + control + C-ABI host).

- [x] `NDI` module — **receiver + discovery + sender done**: `NDISource`/receiver expose
      `IVideoSource`/`IAudioSource`; `NDIDecoderProvider` claims the `ndi:` scheme (`ndi://<name>`), discovery
      via `NDISource.Find` (`find_wait_for_sources`, OQ9); `NDIModule` acquires the ref-counted runtime.
      **Receive is full-res** (the video-only bandwidth-downgrade bug is fixed). **Send works**: `NDIOutput`
      (`.Video` `IVideoOutput` / `.Audio`) — `NDILoopbackSmoke` sent a file out and received 481 frames back at
      1920×1080. **A/V correlation done (2026-06-26).** Root cause: `MediaPlayer.TryOpen` does `TryOpenVideo`
      *then* `TryOpenAudio`, and the provider opened a *separate* `NDISource` per call — two receivers anchored
      independently. `NDIAVCorrelationProbe` measured the gap vs the live OBS source: the two-connection open
      began A/V **~35–48 ms apart** (a startup lip-sync offset). **Fix:** `SharedNdiSourceCache` — the provider
      now hands `OpenVideo`/`OpenAudio` ref-counted leases over **one** `NDISource` per source name (both A+V,
      torn down on last release). Since NDI's ingest clock is audio-driven and video is matched to the audio
      ring *within one receiver*, sharing the connection **is** the correlation — no explicit `SourceSyncGroup`
      wiring was needed. Verified: the probe shows the registry path now opens **1** receiver (was 2), releases
      to baseline; `LivePlaybackSmoke` smooth (528 frames, **0 late drops**), `LiveReceiveProbe` green.
      **Compositor→NDI egress done (2026-06-26):** `CompositeToNdiSmoke` composites a layer on a GL canvas →
      `CpuFrameCompositeTarget` readback (OQ3) → `NDIOutput.Video.Submit`, and a loopback receiver gets the
      composited frames back (179 sent → 134 received non-black). **Live A/V on screen with sound:**
      `LivePlaybackSmoke` now plays the NDI source's audio (PortAudio master via `MediaPlayer.OpenLive`'s
      backend arg) alongside the video off the one shared receiver — 409 frames + 697 audio chunks, 0 late
      drops (a `WaitForStreams` warm-up makes the live audio format ready at open). **NDI module complete.**
      **Live A/V sync hardened (2026-06-26):** the PortAudio master clock compensates the output device buffer
      latency (`ElapsedSinceStart` − `Pa_GetStreamInfo().outputLatency` → audible position; fixes video-leads-
      audio everywhere). The **NDI audio jitter buffer is the residual A/V-sync lever** — `NDIModule(audioMinBuffer)`
      shrinks it to bring audio *forward* to the low-latency video (a video-offset approach was built then reverted
      per the user). `NdiAudioBufferProbe.Probe` (public, cancellable, `onStep` progress) auto-finds the per-network
      glitch-free floor (source-side ring-starvation measure) and returns `Lowest/Balanced/Safe` presets for a UI —
      LAN floor ~3 ms. SDL3 `SDL3GLVideoOutput` now defaults to aspect-preserving **`Contain`** (was `Stretch`).
- [x] `Audio.MiniAudio` module — `IAudioBackend` registered (lazy native load); enumerates 4 out / 5 in on
      this box; mic capture verified. Device-change is serviced by the host's coalescing poller (like
      PortAudio); **native** `ma_device_notification_type` push (incl. `rerouted`, OQ9) is a deferred
      optimization — MiniAudio omits `IDeviceChangeNotifier` rather than ship unverified callback marshalling,
      so it is polled like PortAudio (the doc's uniform fallback). Re-enumeration on demand works today.
- [x] `Present.Avalonia` module — **`OpenGlControlBase` + runtime-verified (2026-06-26)**: `VideoOpenGlControl
      : OpenGlControlBase, IVideoOutput` shares `S.Media.Gpu`'s `YuvVideoRenderer` with the SDL3 presenter.
      **Zero-copy is wired** through the shared `YuvDmabufEglInterop` (dma-buf NV12 on an EGL display + the Win32
      D3D11 shared-handle path) — the SAME proven path SDL3 uses, which supersedes the originally-planned
      `IGlContextExternalObjectsFeature` (D7/OQ2). **Runtime-verified on a display** by new `AvaloniaVideoSmoke`
      (hosts the control + plays a video through `MediaPlayer` → presented 119 frames; control exposes
      `RenderedFrameCount`/`HardwareFrameCount`/`DmabufImportAvailable`). The dma-buf import engages only on an EGL
      context; forcing Avalonia X11 EGL **segfaults on this radeonsi/Mesa** setup (the same EGL/dma-buf hardware
      limit noted for the compositor export), so presentation is verified on GLX here and the zero-copy import is
      covered by the shared SDL3 EGL path. `ViewportFit` now defaults to aspect-preserving `Contain` (matches SDL3).
- [~] Live convergence: NDI/mic on `SourceTimeline` + `SourceSyncGroup` over the session master (03);
      first-class per-source offset. **Mechanism done + verified:** `LiveTimelineDriver` (S.Media.Time) is
      the consumer that anchors sender↔master on the first frame, maps each frame to a master due-time, and
      collapses drift (RebaseToLatest) — `SourceTimeline.Offset` is the first-class per-source phase trim;
      `SourceSyncGroup` keeps correlated NDI A/V in their sender relationship. 9 unit tests; **verified
      against the live OBS NDI source** by `LiveReceiveProbe` (2560×1440@60: schedule lead held at
      ~18 ms ≈ 1 frame, range [0, 21.5]ms, **no drift over 16 s**, 1 warm-up anchor — meets 03 §7).
      **On screen too:** `ILiveVideoSource.RebaseToLatest` seam (Core) on the NDISource video adapter +
      packaged `MediaPlayer.OpenLive` (warms the source, auto-rebases at Play) — `LivePlaybackSmoke` plays the
      OBS source in an SDL3 window via `VideoPlayer` Scheduled at full-res 60 fps. **Mic input** done:
      `IAudioBackend.CreateInput` capture verified (`MicCaptureSmoke`, 48 kHz). ✅
- [x] Multi-output: independent-output **fan-out done + verified on screen** — `MediaPlayer.AttachVideoOutput`
      → `VideoRouter` fans one source to N outputs; `MultiOutputSmoke` drove **two phase-locked SDL3 windows
      at 1080p60, 722 frames/12 s, zero late drops** (both present on the one master VideoTick). The
      `VideoPresentSyncGroup`/`OutputSyncGroup` genlock primitives + `SyncPresentVideoOutput` adapter are
      built + unit-tested for the independent-pump case. **Stitched canvas across outputs** is supported by
      composing tested primitives: `CompositeMulti` with one `GlCompositeTarget` per output, each carrying its
      `CompositeViewport` crop (verified in `CompositeTargetsSmoke`). **NDI as a composite egress** is the
      `CpuFrameCompositeTarget.OnFrameReady → NDIOutput.Video.Submit` pattern (both halves verified —
      `CompositeTargetsSmoke` + `NDILoopbackSmoke`). **1-hour soak PASSED (user, 2026-06-26):** `MultiOutputSmoke`
      ran a full hour — **107,895 frames, 0 late drops**, both outputs in sync the whole time, steady ~60 fps with
      no accumulation or drift (the SDL3 middle-click crash, fixed by `NotFocusable`, was the only thing that had
      blocked it). The texture-mirror zero-copy path is CPU-frame-only on this Mesa/EGL setup; independent outputs
      are the portable path.
      **Soak-found crash (2026-06-26, third-party SDL3 bug, worked around):** a 1-hour attempt died at ~134 s with
      no managed exception. The core dump (`coredumpctl`) pinned it to **SDL3's Wayland backend** — a null deref in
      `Wayland_data_offer_add_mime` ← `data_offer_handle_offer` ← `SDL_PumpEvents` (uncatchable SIGSEGV in libc),
      present across natives 3.4.2 / 3.5.0-preview. The deterministic trigger turned out to be a **middle-click on
      the window** (Linux primary-selection paste → the compositor sends the selection data offer). Not the video
      pipeline / not the ProRes file. No SDL hint disables the data device, and forcing X11 fails (XWayland GL —
      GLX visual / GLES surface). **Fix:** create the SDL3 video windows **`NotFocusable`** — the clipboard/primary
      data offer is delivered only to the keyboard-focused surface, so a focus-less output window never receives
      it. Verified the windows still present (mouse + WM resize/close unaffected); **user-confirmed: no crash on
      middle-click.** **Also fixed the package migration:** `SDL3-CS.Native` is deprecated and `SDL3-CS` 3.4.10.4
      dropped the transitive native, so nothing deployed `libSDL3.so` — `S.Media.Present.SDL3` now references the
      split **`SDL3-CS.Linux` / `SDL3-CS.Windows`** (3.4.10.4) and the win-x64 IDE copy uses `$(PkgSDL3-CS_Windows)`.
- [x] **(Re-filed from Phase 4)** Routing scene = N→M channel remap + multi-track select. **Done:** multi-track
      via `ShowClipBinding.AudioStreamIndex` → `PlayClipAsync` → `MediaPlayer` open options; `OutputPatchRoute.ChannelMatrix`
      carries the N→M remap (round-trips `ShowDocument` JSON via `ToChannelMap()`); and `ShowSession` drives
      **per-group multi-output** — `ShowAudioOutput` declares a group's outputs (id/device), each clip attaches
      to all of them (first = master, rest auto-slave with adaptive-rate), and each output's N→M matrix is
      applied (channel count + map). Headless tests cover a 2→4 single-output remap **and** a 2-output group;
      the implicit-master fallback (no outputs declared) is unchanged. *(This is the audio fan-out; the video
      stitched-wall path is the multi-output item above, still pending its 1-hour soak.)*
- [x] **Adaptive-rate audio (drift correction for non-master outputs)** — `AdaptiveRateAudioOutput`
      (swresample, router-agnostic) in Decode.FFmpeg + `ResamplingAudioOutput` (egress rate-match) salvaged
      (Decode.FFmpeg gains a `Time` ref for `IPlaybackClock` forwarding — 01 §3); registry slot
      (`SetAdaptiveRateOutputFactory`/`CreateAdaptiveRateOutput`) wired from `FFmpegModule`; `MediaPlayer`
      auto-enables it on non-master outputs (router `AdaptiveRateWrapper` ← registry + `PumpPressure` bias).
      +5 unit tests; the wiring path runs clean through `PlaybackSmoke`. (Also fixed: audio-only files no
      longer crash the dual-open.)

**Gate:** ✅ NDI receive + send (`LiveReceiveProbe` / `NDILoopbackSmoke`); live A/V-sync targets met
([03 §7](03-AV-Sync-Clocks-Routing.md), `LiveReceiveProbe`). **Re-verified on hardware 2026-06-26** vs the
live OBS source: receive 1201 frames @ 60 fps 1080p (lead 8.4 ms mean, no drift / 20 s), `NDILoopbackSmoke`
send round-trip (194 frames back), and `LivePlaybackSmoke` on-screen (648 frames, 1 late drop).
*Runtime acceptance (user/hardware):* the 1-hour multi-output drift soak — run `MultiOutputSmoke` long-form
on two physical devices.
**Exit:** file + live composited on one canvas, in sync, across phase-locked outputs — primitives in place
and verified; full stitched-wall validation is the runtime soak above.

---

## Phase 6 — Subtitles + Control + native plugin host
**Goal:** subtitles, data-driven control, and the C-ABI plugin host.

**Subtitles:**
- [x] `S.Media.Subtitles`: text (SRT/VTT) via Skia; **ASS/SSA via `LibAssLib`** (new Tier-0 wrapper)
      bundling libass + FreeType + FriBidi + HarfBuzz + font provider (fontconfig/DirectWrite) (D14/OQ4);
      bitmap (PGS/DVB) via `Decode.FFmpeg` capability; `SubtitleLayerSource` aligned to master;
      none/one/many selection.
      **Text slice DONE (2026-06-26):** `SubtitleTextParser` (one parser for SRT + VTT), plus **`SubtitleAssParser`**
      (text-level ASS/SSA: reads `[Events]` Format/Dialogue, strips `{…}` overrides + `\k`/`\t`/`\fad`, converts
      `\N`/`\h`; shared `SubtitleTimecode` handles ASS centiseconds). `SubtitleCue`/`SubtitleDocument` (active-cue
      lookup, none/one/many), `SubtitleTextRenderer` (Skia → Bgra32-premul overlay, bottom-centered + outlined),
      `SubtitleLayerSource` (master-aligned, cached per cue + a fast path that skips re-scanning while a cue holds).
      **13 tests green.** Validated on a real fansub MKV (`THE IDOLM@STER MOVIE` — ASS stream extracted via FFmpeg):
      readable dialogue out, but **64,140 Dialogue lines** of per-char animated typesetting + karaoke = the textbook
      case for full libass. **Font resolution (fc-match):** SkiaSharp's Linux native has no system font manager, so
      the renderer resolves the real default via fontconfig `fc-match` (then known paths), Windows/macOS via the
      platform manager.
      **`LibAssLib` P/Invoke binding DONE (2026-06-26):** new `Subtitles/LibAssLib` — pure `[LibraryImport]`
      binding to libass (`ass_*`, no C shim; analogue of MALib/NDILib) with the `ASS_Image` mirror, managed
      wrappers (`AssLibrary`/`AssRenderer`/`AssTrack`), and `AssImageBlender` (layer list → premultiplied BGRA32,
      VSFilter alpha). **3 tests green** (init → fontconfig → parse → render → blend). **Verified on the real
      karaoke file** at 2:10 → **66 layers, 94,309 px** — the full per-syllable styling the text path can't do.
      Uses system `libass.so.9`.
      **ASS renderer wired into `S.Media.Subtitles` (2026-06-26):** new `AssSubtitleLayerSource` (parallel to
      `SubtitleLayerSource`) renders an ASS document → Bgra32-premul `VideoFrame` via libass — zero-alloc render
      path (one reused buffer + frame; libass's `detect_change` drives a skip-blend fast path via the new safe
      `AssRenderer.RenderInto`, so S.Media.Subtitles stays `unsafe`-free). **15 subtitle tests green; 633 total;
      arch-test 4/4** (`S.Media.Subtitles → LibAssLib` allowed, like `MiniAudio → MALib`).
      **Composition integration as a real LAYER (2026-06-26):** both sources implement Core's new `IVideoOverlaySource`
      (`RenderAt(time)→VideoFrame?`). Per the user's call, subtitles are NOT a bespoke overlay path — `ClipCompositionRuntime.AttachSubtitleOverlay`
      adds a full-canvas **top-z-order layer (a normal mixer slot)** and the pump renders the source each frame +
      pushes it, so the mixer composites it uniformly with video (z-order/opacity/blend; one composite pass). The
      one wrinkle: slots own/dispose pushed frames while the sources hand back borrowed/reused frames, so the pump
      copies into a **pooled, slot-owned** frame (no GC). Builds 0/0, 633 tests (no regression — null-guarded for
      non-subtitle compositions), arch-test green.
      **ShowSession auto-attach + end-to-end PROVEN (2026-06-26):** `ShowClipBinding.SubtitlePath` (round-trips JSON,
      D10) auto-attaches via a **host-wired factory delegate** — `ShowSession(…, Func<path,w,h,IVideoOverlaySource?>)`
      + `S.Media.Subtitles.SubtitleSourceFactory.FromFile` (`.ass`→libass, `.srt`/`.vtt`→Skia), so Session stays
      Subtitles-free (arch allows Session→Core only). Extended **`SessionSmoke`**: factory renders (1280×720), the
      composition composites with **layers=2 (video + subtitle)** through the real pump — the production path, run
      headless.
      **ARCHITECTURE CONSOLIDATED on libass (2026-06-27, user's call):** dropped the whole Skia text path
      (`SubtitleTextParser`/`SubtitleAssParser`/`SubtitleCue`/`SubtitleDocument`/`SubtitleTimecode`/`SubtitleRenderStyle`/
      `SubtitleTextRenderer`/`SubtitleLayerSource` + their tests + the **SkiaSharp dependency**). Rationale: FFmpeg
      *decodes* every text format to ASS events but does **not** rasterize ASS — libass is the renderer (FFmpeg's
      own `subtitles` filter just wraps libass), so a Skia text path was redundant + lower-fidelity. New shape:
      **libass renders ALL subtitles** — sidecar `.ass`/`.ssa` directly (`SubtitleSourceFactory.FromFile`); every
      other format + in-container streams decode to ASS events via FFmpeg → libass. `S.Media.Subtitles` is now just
      `AssSubtitleLayerSource` + `SubtitleSourceFactory` (deps: Core + LibAssLib). 620 tests, SessionSmoke green.
      **FFmpeg subtitle-decode path DONE (2026-06-27):** `FFmpegSubtitleDecoder.Decode(path, streamIndex)` (in
      `S.Media.Decode.FFmpeg`, libav `avcodec_decode_subtitle2`) decodes a sidecar file OR an in-container stream
      → `DecodedSubtitleTrack` (ASS header + timed `SUBTITLE_ASS` events + embedded-font attachments). New
      streaming `AssSubtitleLayerSource` ctor feeds them to libass (`ProcessCodecPrivate` + `ProcessChunk` +
      `AddFont`). **`SubtitleDecodeSmoke` verified** SRT/MicroDVD(`.sub`)/SAMI(`.smi`)/SubViewer(`.sbv`)/ASS/VTT
      from `Reference/TestSubs/` + an **in-container** muxed MKV (karaoke `{\k}` preserved) all decode→render
      (10k–29k visible px).
      **Host glue + ShowSession wiring DONE (2026-06-27):** `S.Media.Interop.SubtitleOverlayFactory.FromFile`
      (Interop is the only host allowed to ref both Decode.FFmpeg + Subtitles) — sidecar `.ass`→libass direct,
      every other format/container→FFmpeg-decode→libass. Wired as ShowSession's factory delegate; **`SessionSmoke`
      now uses a `.srt`** (non-ASS) clip `SubtitlePath` → FFmpeg-decoded → auto-attached as a layer (**layers=2**,
      composited) through the real pump. So a clip's `SubtitlePath` of ANY format renders in a show. arch 4/4
      (Interop→Decode.FFmpeg+Subtitles allowed). **Native provisioning (user's call): Linux = user-provided**
      (system libass + deps via the package manager); **Windows = a host build script (user, later)** — no
      bundling needed from the framework.
      **Bitmap subtitles DONE (2026-06-27):** `FFmpegBitmapSubtitleDecoder.Decode` (in `S.Media.Decode.FFmpeg`,
      libav `SUBTITLE_BITMAP` rects) decodes PGS/DVB/VobSub → palette-indexed images → premultiplied-BGRA cues
      (end = next presentation's start). `BitmapSubtitleLayerSource` (in `S.Media.Interop` — pure compositing, no
      libass) blits the active cue's placed images onto an authored-res overlay (compositor scales the layer);
      `SubtitleOverlayFactory` dispatches text→libass / no-text→bitmap. **`SubtitleDecodeSmoke` verified** the
      `long-movie.sup` PGS (**25 cues @ 1920×1080**, composited) + a text SRT in one run. ✅ **The
      `S.Media.Subtitles` checklist item is COMPLETE** — every text format + ASS + bitmap, sidecar + in-container,
      rendered/composited as a layer + show-wired (`none/one/many` = pass none / one / compose several sources).
      *Only loose end:* the embedded-font `AddFont` path is wired but **untested** (no fonts-attached MKV sample).

**Control:** (foundation-first; the old engine is in old `MediaFramework/Control/S.Control` + `Extras/MIDI|OSC` — copy-salvage)
- [x] Move `S.Control` engine; X32/XTouch → **data-driven profiles** + control registry (P6); X32 meter
      decode as a registered capability.
      **Transport foundation MOVED (2026-06-27):** **OSCLib** → `next/MediaFramework/OSC/OSCLib` (OSC over UDP;
      11 files) + **PMLib** → `next/MediaFramework/MIDI/PMLib` (PortMidi P/Invoke, native user-provided; 40 files
      incl. MessageTypes/Devices/Types/Runtime/Accumulators). Both go in **non-FrameworkDir** category dirs (like
      `Audio/MALib`, `NDI/NDILib`) so the arch-test doesn't require a key — bindings need no `Allowed`-map entry.
      Gates green: **OSCLib.Tests 23 + PMLib.Tests 13** pass, build 0/0, arch 4/4, **656 total**. *Note found:* the
      old engine is already partly data-driven — `BuiltInProfileLoader` loads JSON profiles from disk + embedded
      resources (STJ source-gen); the only hardcoding is `BuiltInControlDeviceProfileFactory` (C# `CreateX32Profile`
      etc.) → that becomes JSON profile files (user's call: NO hardcoded devices, see [[feedback_control_data_driven_profiles]]).
      **S.Control CORE MOVED + VERIFIED (2026-06-27):** the whole ~9.7k-line engine (41 .cs + 4 JSON profiles)
      → `next/MediaFramework/Control/S.Control`, **builds 0 errors unchanged** against next/ Core/Session/PMLib/
      OSCLib/Mond (clean API compat). 22 clean S.Control test files → `next/Test/S.Control.Tests` (UI-coupled ones
      skipped); **192 tests pass** (profiles load from embedded JSON, device managers/matcher, 14-bit CC, X32 meter
      decode, OSC listeners, Mond script runtime) → build 0/0, arch 4/4, **848 total**, old trees untouched. (One
      test had an old-tree relative path to `Profiles/`; fixed for the next/ layout.)
      **DATA-DRIVEN HELPERS — design locked + started (2026-06-27, user's call):** extend "no hardcoded devices"
      to the *script helpers* too. Today the runtime always exposes a fixed `osc`/`midi`/**`x32`** module set
      (`ControlScriptApiLibrary`); the `x32` module's address builders (`channelFaderAddress`…) are 200 lines of
      hardcoded C# (`X32Presets`/`ControlPresets.cs`) that re-derive patterns the profile JSON already carries as
      data (193 `address` entries). **User-confirmed design = "helpers read profile data":** a profile gains a
      `HelperScript` (embedded Mond) + `ScriptModule` name; helpers look addresses up from the profile's own
      commands — `device.command(id).address` — so the address string lives ONCE (in `commands`) and the runtime
      has ZERO device-specific code. Command ids are globally unique (`x32.ch.01.fader`), so a **single global
      `device.command(id)`** resolves across loaded profiles — no per-profile binding needed. **Foundation laid:**
      `ControlDeviceProfile.HelperScript`/`ScriptModule` added (192 tests still green).
      **(a)–(d) DONE + VERIFIED (2026-06-27):** (a) `ControlScriptRuntimeServices.Profiles` + the `devices.command(id)`
      accessor (returns `{address, valueKind, access, cacheKey, min/max}` from the loaded profiles; ids globally
      unique). (b) `ControlScriptFileHost` serves each profile's `HelperScript` as a require-able module and binds it
      to its `ScriptModule` global via the entry script. (c) the X32 `HelperScript` (compact Mond) ports every
      address builder (`channelFaderAddress`…→`devices.command('x32.ch.NN.fader').address`) **and** the fader-curve
      math (`faderToDb`/`dbToFader`/`quantizeFader`). (d) the C# `x32` module (`CreateX32Api`+`RequireIndex`) is
      **deleted** — the runtime has no device-specific script code. *Gotcha fixed:* Mond passes the receiver as the
      first arg for `x32.fn(...)`, so each exported helper takes `(self, …)` (the C# modules skip it via
      `ArgumentOffset`). Built-in profiles load by default so helpers are always available (old always-on parity).
      New `ProfileHelperScriptTests` proves it; **850 total**, build 0/0, arch green.
      **(e)–(f) DONE + VERIFIED (2026-06-27):** (e) `BuiltInControlDeviceProfileFactory` **deleted** — `ExportBuiltInProfiles`
      + the test users now read the shipped JSON via `BuiltInControlDeviceProfileRepository.Instance` (new `TestProfiles`
      helper); the now-unused `XAirPresets` class is also deleted (`X32Presets`/`X32Fader` stay — still used by
      `XTouchMiniX32FaderMapping` + templates). (f) **meter-blob decode is now a registered capability:** new
      `IControlMeterBlobDecoder` + `ControlMeterBlobDecoderRegistry.Default` (keyed by name; `"x32"`→`X32MeterBlobDecoder`
      wrapping `X32MeterCacheDecoder`); `SupportsMeterBlobDecoding` and the runtime dispatch resolve the decoder by the
      profile's `Behaviors.MeterBlobDecoder` name (the hardcoded `=="x32"` and the `/meters` literal are gone — the
      decoder owns its address). **850 total, build 0/0, arch green, old trees untouched.** ✅ **The "no hardcoded
      devices, incl. helpers" line item is complete** — devices are profiles (data + Mond helpers) + registered binary
      capabilities; the runtime has zero device-specific code. (`✓` the `S.Control` move + data-driven-profiles item.)
- [x] Mond host API targets the `ShowSession` action façade (headless-drivable).
      **DONE (2026-06-27):** new `IControlShowActions` (Go/FireCue/Seek/Stop) + `ShowSessionControlActions` adapter
      (posts fire-and-forget to `ShowSession.GoAsync`/`FireCueAsync`/`SeekAsync`/`StopAsync`); the script API exposes
      it as the **`show` global** (`show.go()`/`show.fireCue(id)`/`show.seek(secs)`/`show.stop()`), wired via
      `ControlScriptRuntimeServices.ShowActions` (no-op when no show bound). So a MIDI button / OSC message drives
      cues + playback. `ControlShowBridgeTests` proves dispatch + the no-op path. (`ArgumentOffset` handles the Mond
      receiver-as-arg0.)
      **DEVICE-SPECIFIC C# ELIMINATED (2026-06-27, user's follow-up):** deleted `XTouchMiniX32FaderMapping` (+ test —
      it was only used by its own test; the runtime does XTouch→X32 via the Mond template) and the redundant
      `X32Presets` address builders + preset builders + `X32Fader` (+ `X32PresetTests`) — all superseded by the
      profile `HelperScript`.
      **TRANSPORT WAS DEAD TOO — DELETED (2026-06-27):** investigating the user's "can these be generalized?",
      `X32Session` (the OSC connect / `/xremote` keep-alive / `/subscribe` / `/meters` renewal loop) + `X32Subscription`/
      `X32MeterSubscription` + the endpoint config (`X32EndpointPreset`/`X32Presets`/`ControlPresets.cs`) turned out to
      be **entirely unused** — superseded by the data-driven **profile Tasks** (periodic OSC sends via
      `ControlPeriodicOscSendManager`; `/xremote` is a profile Task with `intervalMs`). Deleted all of it. **The ONLY
      X32-specific C# left is the binary meter-blob *parse*** (`X32Meters` + `X32MeterCacheDecoder` + the registered
      `X32MeterBlobDecoder`, 3 files): deserialize a packed little-endian payload at meter rate → floats; the output
      address comes from the OSC arg before the blob (no protocol state). That's the deliberate registered-capability
      escape hatch — it *could* be a profile "binary-format descriptor + generic decoder", but it's a hot path (~50Hz)
      where C# is right and the gain is marginal. So: the runtime has **zero device-specific *logic*** — a device is
      its profile (data + Mond helpers) + one tiny opt-in binary capability. **812 total, build 0/0, arch green.**

**Plugin host (`S.Abi`) — the forever-surface:**
- [~] Define `include/mfp_plugin.h`: **full vtable surface** — source/output/audio-backend/layer-surface
      (GL)/subtitle/control-decoder (D9).
      **DRAFTED (2026-06-27):** `next/MediaFramework/Interop/S.Abi/include/mfp_plugin.h` (323 lines, valid C11 —
      `gcc -Wall -Wextra -std=c11` clean) realizes the §05 sketch: all **6 capability vtables** (audio-backend /
      video-source + provider / video-output / layer-surface(GL) / subtitle / control-decoder), the per-kind
      **frame union** (CPU/dma-buf/D3D11/GL — mirrors Core's `Dmabuf*`/`Win32Shared*` backings; item below),
      negotiated **`MfpSync`** (keyed-mutex/binary+timeline-semaphore/fence; item below), the host API, the
      registrar, and `mfp_plugin_register`. ABI-hygiene rules baked into the comments (append-only versioning /
      int-status errors + thread-local last-error / 100ns ticks / host-pooled CPU frames + acquire-release+sync for
      GPU / per-call threading / opt-in trust); OQ1 (frame union) + OQ2 (sync) flagged inline. ⚠️ Pending the
      **review-hard-before-v1** pass — that's the gating decision, not the typing.
      **REVISED post-evaluation (2026-06-27, 381 lines, still gcc-clean):** checked the draft against the *real*
      managed interfaces — found + closed v1 gaps so the existing modules **and** a configurable layer are
      expressible without a later ABI change: audio backend gained the **master-clock readout**
      (`output_played_frames` ↔ `PlayedSamples`/`IClockedOutput`) + latency `MfpAudioOpts` + backpressure
      (`output_writable_frames`); video output gained queue-control (`abandon_queued`/`wait_for_idle` ↔
      `IVideoOutputQueueControl`); the source provider became a **media** provider opening correlated video+audio in
      ONE `open()` (so NDI shares the receiver); **layer surfaces became a factory** taking an opaque `config_json`
      (the MMD models/motion). Optional caps = NULL-fn-pointer convention. *Managed follow-on:* `AddLayerSurface`
      needs the config param + `ShowDocument` a `surface`+`surfaceConfig` layer — sketched in §05. (Control transport
      stays framework-only by design — only decoders/profiles are plugin surfaces.)
- [ ] **Per-kind tagged frame union** incl. GPU handles: `MfpCpuFrame`/`MfpDmaBufFrame`/`MfpD3D11Frame`/
      `MfpGlTextureFrame` (GL = same-context only), mirroring Core's `Dmabuf*`/`Win32Shared*` (D8/OQ1).
- [ ] **Negotiated `MfpSync`** (keyed-mutex / semaphore / fence via capability query — OQ2).
- [~] Managed adapters → scoped registries; ABI version gate; append-only structs.
      **HOST LOADER + GATE (load+register half) DONE (2026-06-27):** `S.Abi.AbiPluginHost.Load(path)` —
      `NativeLibrary.Load` + `GetExport("mfp_plugin_register")` + a host-API + registrar built from
      `[UnmanagedCallersOnly]` callbacks (all NativeAOT-safe, no reflection) → calls the entry point, enforces the
      **ABI version gate**, and **records every registered capability** (`AbiRegisteredCapability {capability, id,
      vtable, self}`). Managed ABI mirrors in `AbiNative.cs` (layout-critical `[StructLayout(Sequential)]` + `delegate*
      unmanaged`). New **`AbiSmoke`** tool gcc-compiles `test_plugin.c` → `.so`, loads it, and verifies a native C
      plugin registers a **media-source-provider + a control-decoder** (`caps=0x42`) — the gate's load+register half.
      Build 0/0, 812 tests, arch 4/4 (S.Abi→Core). *Next layer:* bind each recorded vtable to its managed interface
      (`IVideoSource` via the frame-union marshalling; `IControlMeterBlobDecoder` — needs S.Control, so an arch add)
      and register the adapter into the scoped registry, so the capabilities don't just register but *run*.
- [ ] **Per-platform conformance plugin** exercising every vtable, in CI (D8/D9).
- [ ] ⚠️ **Review `mfp_plugin.h` hard before tagging v1** — it's expensive to change after (OQ1/D9).

**Gate:** subtitle render test; `OSCLib.Tests`/`PMLib.Tests`; sample C-ABI plugin loads + provides a
source **and** a control decoder.
**Exit:** a third-party native plugin adds a video source + a GL layer surface without touching the host.

**AOT gate VERIFIED for Phase 6 (2026-06-27):** `AotSmoke` extended to reference + exercise the real AOT-risk
paths — `S.Control` (Mond compile+run of a script that uses a profile `HelperScript` + the `show` bridge) and the
`S.Media.Interop` subtitle factory glue — `dotnet publish -p:PublishAot=true -r linux-x64` succeeds and the **7.2 MB
NativeAOT binary runs** them (`control profiles=4, mond+helper=/ch/01/mix/fader, subtitle factory reachable=True`).
*Caveat:* one trim warning **IL2026** — Mond's `VirtualMachine.Machine.Run()` calls `StackFrame.GetMethod()`
(`[RequiresUnreferencedCode]`); publish + run are unaffected, but Mond's *error stack-trace detail* can degrade
under aggressive trimming. It's in the Mond package, not our code; accept + note (Mond was picked *for* AOT support
and it runs). Windows-CI AOT still deferred (user).

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
