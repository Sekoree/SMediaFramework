# TEST-01 — Coverage by behavioral contract

Coverage tracked by *what the code promises*, not by line count. Each subsystem below lists its load-bearing
behavioral contracts and the test(s) that pin them, with a status:

- **✓ covered** — a hermetic (no-hardware) test asserts the contract on every CI run.
- **◑ partial** — the core is covered hermetically; some behaviour is only reachable on real hardware/network
  (gated opt-in tests) or is asserted indirectly.
- **○ gap** — no automated assertion; verified by manual/hardware runs only.

Suite at time of writing: **19 test assemblies, ~1629 tests, 0 failures.** Opt-in hardware/network suites gate on
env vars (`MFP_RUN_NDI_TESTS`, `MFP_RUN_MIDI_TESTS`, `MFP_RUN_NETWORK_TESTS`) and self-skip otherwise.

---

## Core — clocks, timing, frame ownership

| Contract | Test(s) | Status |
|---|---|---|
| A paused/underrun clock freezes `ElapsedSinceStart`; resume folds master drift into the playhead | `MediaClockTests`, `MediaClockMasterTests`, `CompositePlaybackClockTests`, `PlaybackSlavedRouterClockTests`, `OutputSlavedRouterClockTests` | ✓ |
| Playback clocks are allocation-free on the steady tick | `PlaybackClockAllocationTests` | ✓ |
| Video-present sync groups align multiple outputs to one timeline | `VideoPresentSyncGroupTests`, `OutputSyncGroupTests`, `LiveTimelineDriverTests` | ✓ |
| Frame release is exactly-once; DMABUF NV12/P010/P016 backings ref-count correctly | `VideoFrameReleaseTests`, `VideoFrameTests`, `DmabufNv12/P010/P016BackingRefCountTests` | ✓ |
| Pixel-format negotiation picks the cheapest common format | `VideoFormatNegotiatorTests`, `PixelFormatInfoTests`, `VideoColorSpaceTests` | ✓ |
| Registry disposal is an interlocked transition; capability ops rejected after disposal (CORE-01) | `RegistryAndDispatcherTests` | ✓ |
| Builder rolls back accumulated lifetimes on a failed build (CORE-02) | `RegistryAndDispatcherTests` | ✓ |

## Audio routing (`AudioRouter`)

| Contract | Test(s) | Status |
|---|---|---|
| Route apply is deterministic; add/remove/last-write semantics | `AudioRouterRouteTests`, `AudioRouterApplyRouteDeterminismTests`, `AudioRouterRouteLastTests` | ✓ |
| N→M channel matrix mixing (SIMD + scalar remainder, HW-accel guards) | `AudioRouterMatrixTests`, `ChannelMapTests`, `AudioMixPresetTests` | ✓ |
| Pump back-pressure raises pressure lock-free; drop counters; lifecycle | `AudioRouterPumpLifecycleTests`, `PumpPressurePlaybackHintMonitorTests` | ✓ |
| A faulted source doesn't poison the graph; source gating | `AudioRouterFaultTests`, `AudioRouterSourceGatingTests` | ✓ |
| Clocking / master election under a live seeded graph | `AudioRouterClockingTests`, `AudioRouterLiveGraphSeededTests`, `AudioRouterPlaybackTests` | ✓ |

## Video routing (`VideoRouter`)

| Contract | Test(s) | Status |
|---|---|---|
| A failed branch negotiation rolls back and keeps presenting (no graph poison) | `VideoRouterRouteRollbackTests` | ✓ |
| Snapshot-under-lock + submit-outside + retire-after-lease-drain, safe under concurrent route churn (ROUTE-01/02) | `VideoRouterConcurrencyTests` | ✓ |
| Retiming/offset playhead keeps rebased PTS and playhead consistent | `RetimingVideoOutputTests` | ✓ |
| Deinterlace (bob/yadif) correctness | `BobDeinterlacerTests` | ✓ |

## FFmpeg decode (`S.Media.Decode.FFmpeg`)

| Contract | Test(s) | Status |
|---|---|---|
| Provider claims only its own schemes; option mapping; cancel a blocked open | `FFmpegDecoderProviderTests` | ✓ (network cancel is opt-in) |
| Timestamp normalization: best-effort→pts fallback, timebase↔wall-clock, seek round-trip (FFMPEG-02) | `FFmpegTimestampsTests` (pure, no native) | ✓ |
| Real decode + seek across a fixture matrix: A+V / V-only / A-only / multi-track / truncated / repeated-open stability / repeated-seek (FFMPEG-01) | `FFmpegFixtureMatrixTests` (generated fixtures) | ◑ (skips without ffmpeg CLI + native) |
| Stream-copy remux (YouTube prepare path) preserves both tracks + seekability | `StreamCopyRemuxerTests` | ◑ (needs native + ffmpeg) |
| Adaptive-rate audio output backpressure | `AdaptiveRateAudioOutputTests` | ✓ |

## Compositor & GPU

| Contract | Test(s) | Status |
|---|---|---|
| Layer format-change / new-surface handling | `CompositorLayerFormatChangeTests`, `CompositorNewSurfaceTests`, `SurfaceLayerTests` | ✓ |
| DMABUF CPU readback for a converting branch | `VideoDmabufCpuReadbackTests` | ✓ |
| GL compositor orientation (GL vs CPU), multi-context | `S.Media.Gpu.Tests/*`, `GlCompositorOrientationTests`, `SDL3GLVideoCompositor*Tests` | ◑ (GL paths run under xvfb software GL; PRESENT-01 gates one) |

## Session (`ShowSession`, `ShowDocument`)

| Contract | Test(s) | Status |
|---|---|---|
| Document validated + staged before the live show is touched; all errors returned (DOC-01) | `ShowSessionTests`, `ShowDocumentTests` | ✓ |
| JSON round-trip is lossless + AOT-safe; old docs with removed fields still load (DOC-02) | `ShowDocumentTests` | ✓ |
| Deck seek resumes; trim-in/loop/fade honoured; end-monitor stops at duration | `MediaPlayer*Tests`, `CueStartOffsetCompositionTests`, `ClipWindowTests`, `SessionSmoke` (end-to-end) | ✓ |
| Audio-output topology rebuild; per-route isolation | `AudioRouteRebuildTests`, `CompositionOutputAttachTests` | ✓ |
| Subtitle overlay layer keep-policy (constant-PTS overlay doesn't freeze) | `SubtitleLayerFreshnessTests`, `SurfaceLayerSessionTests` | ✓ |
| Device-enumeration cache (SESSION-01) | via `AudioRouteRebuildTests` (indirect) | ◑ (internal; exercised indirectly) |

## Control — MIDI / OSC / scripts

| Contract | Test(s) | Status |
|---|---|---|
| Bounded coalescing event queue: coalesce continuous, preserve edges, drop counters, non-blocking enqueue, bounded shutdown (CTRL-01/02) | `ControlEventQueueTests` | ✓ |
| Monitor ring buffer is fixed-size (CTRL-03) | `ControlMonitorTests` | ✓ |
| Data-driven device profiles (no hardcoded devices); 14-bit CC combine | `ControlDeviceProfileTests`, `ControlDeviceMatcherTests`, `ControlValueCacheTests` | ✓ |
| OSC decode + malformed/truncated rejection; timetag scheduling; coalesce/atomicity policy (OSC-01) | `OSCPacketCodecTests`, `OSCMalformedPacketTests`, `OSCTimeTagSchedulingTests`, `ControlOSCListenerManagerTests` | ✓ |
| Mond script host: profiles, helper scripts, show bridge | `ControlScriptRuntimeSessionTests`, `ControlSystemRuntimeSessionTests`, `Bcf2000GuideScriptsTests` | ✓ |
| MIDI message parse / SysEx / 14-bit accumulate | `MIDIMessageParserTests`, `MIDISysExAccumulatorTests`, `MIDIAccumulatorTests` | ✓ |
| PortMIDI capability diagnostic + real device round-trip (MIDI-01) | `MIDIRuntimeDiagnosticsTests` | ◑ (device round-trip opt-in) |

## Interop ABIs

| Contract | Test(s) | Status |
|---|---|---|
| Outbound C ABI: error mapping, states, `NOT_FOUND`, last-error locality, per-session call leases, repeated init/shutdown, double-destroy (ABI-01/02/03) | `SmpSmoke/smoke.c` (real C client) | ◑ (gated on AOT-published `.so`) |
| Plugin host: per-adapter leases; unload deferred until in-flight native call drains (PLUG-01/02) | `AbiSmoke` (real C plugin, dispose-during-blocked-call) | ◑ (gated; gcc + AOT) |
| Plugin directory scanning | `MediaPluginDirectoryTests` | ✓ |

## Backends & sources (hardware-adjacent)

| Contract | Test(s) | Status |
|---|---|---|
| `IAudioBackend` conformance across PortAudio + miniaudio: name, enumeration, open-default format+lifecycle, error (AUDIO-01) | `AudioBackendConformanceTests` | ◑ (device open runs on real default device; self-skips headless) |
| NDI timestamp correlation (pure); bandwidth/reconnect policy; live receive + exactly-once release (NDI-01) | `NDIFrameTimingTests`, `NDIReceiveBandwidthPolicyTests` (pure) + `NDILoopbackSoakTests` (opt-in live) | ◑ |
| MMD: bake determinism/eviction/retry, native shim ABI contract, real asset animate+render (MMD-01..04) | `MMDBakeTests`, `MMDShimContractTests`, `MMDRealAssetTests` | ✓ (needs built `libmmd_bullet.so`) |
| YouTube reliable-mode prepare, json3 subtitles, per-stream cache (YT-01..03) | `S.Media.Source.YouTube.Tests/*` | ◑ (network path opt-in) |
| Subtitle ASS/SSA render (libass) | `AssRenderTests`, `SubtitleSelectionTests` | ◑ (needs libass + font) |
| Text cue: URI round-trip, render, duration-bounding, live replace (SESSION-02) | `TextSourceTests`, `TextFrameRendererTests` | ✓ (SkiaSharp) |

## HaPlay application

Broad ViewModel/mapping coverage (77 test files) — remote API dispatch + token masking (`RemoteApiDispatcherTests`,
`RemoteApiTokenMaskingTests`), project IO (`HaPlayProjectIOTests`), show mapping (`ShowDocumentMapperTests`,
`MediaPlayerShowMapperTests`), accessibility smoke (`IconButtonAccessibilityTests`, `SoundboardAccessibilityTests`),
appearance (`AppearanceSettingsTests`), settings persistence (`AppSettingsTests`), deck routing/end-detection, and
per-workspace empty-state/interaction. **✓** for logic; UI rendering is verified by the headless launch smoke.

---

## Genuine gaps / next priorities

1. **Hardware/network paths are opt-in, not gating.** NDI live receive, MIDI device round-trip, the outbound
   C-ABI + plugin smokes, backend device-open, YouTube network, and the FFmpeg fixture matrix all run only where
   their dependency exists. CI gates one software-GL path (PRESENT-01); a per-platform *gating* real-runtime lane
   for at least the C ABI + backend enumeration would close the biggest blind spot (REL-02 / TEST-01 follow-up).
2. **NDI native capture/send has no fake-adapter seam** (NDI-01 remainder) — reconnect/format-change/cancellation
   are only exercised against a live source, not deterministically. Tracked as a separate refactor.
3. **`ShowSession` internals** (device cache, monitoring/end) are asserted indirectly through end-to-end session
   tests rather than in isolation — acceptable, but a targeted unit would make the invariant explicit.
4. **GL/compositor correctness** relies on software GL under xvfb; the hardware GL path and the Windows launch
   lane remain best-effort (PRESENT-01 Windows follow-up).

Everything a hermetic runner *can* assert without hardware is asserted; the residual is inherent to a
media/hardware framework and is mitigated by the opt-in soaks, which pass against real devices when present.
