# MFPlayer `Next` rewrite: critical architecture and implementation review

**Review date:** 2026-06-30

**Reviewed revision:** `79b70ed` on branch `next-2`

**Scope:** the design documents in `Next/`, the implementation in `next/`, the HaPlay port, build/test/NativeAOT/ABI paths, and the bundled YouTubeExplode and MMD reference material.

## Executive conclusion

The rewrite has a substantially better intended shape than the old static framework: dependency rules are explicit, registries replace global extension state, the headless session model is the correct destination, the C ABI is testable from C, and the low-level media/GPU test coverage is useful. The solution builds, all current managed tests pass, representative local playback works, the inbound plugin smokes pass, and the outbound NativeAOT C library works for its current narrow surface.

It is not yet a completed rewrite or a safe replacement for the existing HaPlay runtime. The new `ShowSession` path is behind `HAPLAY_USE_SHOWSESSION=1` and is off by default; the large old playback engines remain the production path. Several plan claims therefore describe the target or a smoke-test result rather than the behavior of the shipped app. The most important unfinished boundaries are media opening, session command scheduling, clock/synchronization wiring, output ownership, plugin lifetime management, and actual UI cutover.

I would not freeze the native plugin ABI as version 1, enable the new session path by default, or call Phase 8 complete yet. The immediate release blockers are:

1. `ShowSession` takes ownership of HaPlay-owned SDL/NDI outputs and disposes them on document reload/disposal.
2. Long cue operations run on the serial session dispatcher and can prevent stop, seek, GO, or shutdown from being processed.
3. The media registry opens audio and video independently, often opening the same FFmpeg source twice, discards the real errors, and has no cancellable asynchronous prepare operation.
4. The synchronization primitives introduced by the rewrite are mostly not connected to the player/session/composition paths that need them.
5. Native module/plugin lifetime is not owned by a disposable host, leaking PortAudio/NDI runtime references and making safe plugin unload impractical.
6. The HaPlay convergence path loses material functionality and is opt-in, while the phase checklist still has its actual retirement/parity exit conditions open.

Both proposed future plugins are feasible, but neither should be implemented against the current extension boundary unchanged:

- YouTube is a good first-party, build-time managed module. It needs an asynchronous atomic media-open API and a persistent prefetch/cache service. It should not start as a native ABI plugin.
- MMD is feasible as a GPU layer-surface plugin, but the surface ABI and session clock contract need another design pass. A useful implementation is a renderer project, not a port of the included Blender add-on. Physics, seeking, multiple models, GL state isolation, and asset licensing make it a significantly larger feature.

## Review method and observed baseline

I reviewed all nine `Next/*.md` documents against the projects and runtime paths under `next/`, searched call sites for the central abstractions, compared retained HaPlay/framework files with the old tree, and exercised build, test, playback, ABI, GL, and NativeAOT paths.

The snapshot is large enough that passing tests alone are not an adequate completion signal:

| Measure | Observed snapshot |
|---|---:|
| `Next/` design documents | 9 files, about 2,415 lines |
| `next/` source/project files excluding `bin` and `obj` | about 968 |
| `next/` C# files | 827 |
| `next/` C# lines | about 154,670 |
| projects in the rewrite | 65 `.csproj` files |
| production source files over 800 lines | 27 files, about 41,574 lines combined |
| managed tests run | 1,301 passed, 0 failed |
| build | succeeded, 0 errors, 37 analyzer warnings |

The largest retained HaPlay types are still the old responsibility clusters: `MediaPlayerViewModel.cs` is about 3,589 lines, `ControlWorkspaceViewModel.cs` about 2,926, `CuePlaybackEngine.cs` about 2,512, and `HaPlayPlaybackSession.cs` about 1,988. Of the files with counterparts in the old UI, 132 are byte-identical, representing roughly 19,849 lines. The same pattern exists in the framework tree: 152 common C# files are byte-identical, roughly 18,721 lines. Reuse is not inherently a defect, but it means this is currently an architectural rewrite around a substantial port, not yet the simplification the plans describe.

### Validation performed

The following checks passed:

- `dotnet build next/MFPlayer.Next.sln -m:1 --no-restore -v:minimal`
- all solution tests: 1,301 passed
- `FrameDump` decoded 30 BGRA frames from `/run/media/sekoree/512/mambo.mp4`
- `VideoPlaybackSmoke` presented 78 frames over approximately three seconds from the same file
- `AbiSmoke` exercised the six advertised sample plugin capabilities
- `AbiGlSmoke` exercised a native GL layer surface under Xvfb
- `AotSmoke` published and ran as a native executable
- `S.Media.Interop` published as a NativeAOT shared library; the C `SmpSmoke` client passed with both an empty show and real media
- a forced NativeAOT publish of `HaPlay.Desktop` completed

There are important qualifications:

- The normal solution build emitted 37 `IL2026`/`IL3050` warnings in the UI test project, mainly reflection-based JSON and non-generic enum converters. The production AOT publish emitted the known Mond `StackFrame.GetMethod` trim warning.
- `HaPlay.Desktop.csproj:8-11` explicitly sets `PublishAot=false` and still names the executable `HaPlay_Test`.
- The published HaPlay application launched under Xvfb, but `HAPLAY_SMOKE=1` did not make it self-exit and the run timed out. There is no current `HAPLAY_SMOKE` implementation, despite `Next/09-Phase-Checklists.md:649` recording that behavior as a completed render smoke.
- The headless C ABI session initialized PortAudio even though no audio output was attached, producing avoidable ALSA/device warnings.
- The GL and subtitle CI smokes are non-gating, and the outbound media C smoke catches and ignores failure.

## Priority findings

Severity means:

- **Blocker:** fix before default-on cutover or ABI freeze.
- **High:** likely correctness, reliability, or major architecture failure in real use.
- **Medium:** maintainability, performance, test, or feature-completeness problem that should be scheduled.
- **Low:** cleanup or documentation mismatch.

| ID | Severity | Finding | Immediate action |
|---|---|---|---|
| NXT-01 | Blocker | `ShowSession` disposes outputs borrowed from HaPlay | Make ownership explicit; pass a release lease and do not dispose borrowed outputs |
| NXT-02 | Blocker | Media open is synchronous, split into audio/video, non-cancellable, and hides errors | Replace it with one asynchronous atomic asset-open result |
| NXT-03 | Blocker | Cue waits/open/monitor work can monopolize the session dispatcher | Keep state mutation serialized, but run cancellable work outside the dispatcher |
| NXT-04 | Blocker | New session/clock/sync abstractions are not wired into production playback | Establish one session clock contract and test real multi-source/multi-output behavior |
| NXT-05 | Blocker | Module and native plugin lifetimes have no owning disposable host | Add `MediaHost`/capability catalog ownership before ABI freeze |
| NXT-06 | High | HaPlay still defaults to the old engines; mapper drops supported app semantics | Cut over one vertical slice with parity tests, then delete the replaced path |
| NXT-07 | High | GO/cue/fade/end behavior contains correctness gaps | Validate cue graphs and implement the documented behavior rather than exposing inert options |
| NXT-08 | High | Native C handle validation can throw across unmanaged exports | Use a validated handle table and catch at every ABI entry point |
| NXT-09 | High | Dynamic plugin support is only a set of adapters and smokes, not a host feature | Add catalog, configuration, policy, diagnostics, leases, and UI/runtime wiring |
| NXT-10 | High | GPU layer surfaces are outside normal composition and multi-output paths | Put frames and surfaces in one ordered layer model and compositor interface |
| NXT-11 | High | The new session composition defaults to CPU and introduces large per-frame work | Inject compositor/backend selection and add allocation/performance gates |
| NXT-12 | Medium | Persistence accepts invalid/unsupported documents and replaces live state before validation | Validate and stage a complete new session graph before swapping |
| NXT-13 | Medium | Empty module projects and old UI duplication make the solution look more complete than it is | Remove/defer empty projects; split and retire duplicated engines |
| NXT-14 | Medium | Tests are numerous but concentrated away from the new orchestration seams | Add adversarial session, plugin lifecycle, UI cutover, sync, and performance tests |
| NXT-15 | Medium | CI does not run on pushes to the current branch and does not gate the claimed product paths | Fix branch triggers and add gating HaPlay, GL, media, ABI, and AOT checks |

## Detailed findings

### NXT-01 — host output ownership is violated

`ShowSession.LoadDocumentCoreAsync` asks the host for real outputs and wraps every returned output with `DisposeOutputOnRuntimeDispose: true` (`S.Media.Session/ShowSession.cs:181-194`). `ClipCompositionRuntime.AcquiredOutput.Retire` consequently calls `Dispose()` on those objects (`ClipCompositionRuntime.cs:1253-1268`).

HaPlay's factory returns outputs acquired from `OutputManagementViewModel`. Those are borrowed, single-holder handles to long-lived SDL windows or NDI runtimes (`MediaPlayerViewModel.ShowSession.cs:69-96`, `OutputManagementViewModel.cs:36-74`). The UI later releases them so the same output can return to the idle slate and be acquired again. Disposing the underlying output first breaks that contract. A second document load disposes the previous acquired output before HaPlay releases/reacquires it; session disposal has the same problem.

This is a concrete use-after-dispose/reload failure, not just an ambiguous style issue.

Recommended contract:

```text
host acquires output lease
        |
        v
session owns lease only ---- release callback ----> host releases holder
        |
        +-- session must not dispose the host-owned output
```

`videoOutputFactory` should return `ClipCompositionOutputLease`, not a bare `IVideoOutput`. HaPlay should return `DisposeOutputOnRuntimeDispose=false` and a UI-safe release callback. Session-created objects such as `DiscardingVideoOutput` can remain owned/disposed by the session. Add reload, stop, dispose, output-removal, and failed-load tests with disposal counters.

### NXT-02 — media open is the wrong abstraction for local and network media

`IMediaDecoderProvider` exposes synchronous `Probe`, `OpenVideo`, and `OpenAudio` methods (`S.Media.Core/Registry/IMediaDecoderProvider.cs`). `MediaPlayer.TryOpen` calls video and audio separately and catches every exception without retaining it (`S.Media.Players/MediaPlayer.cs:426-445`). The FFmpeg provider creates a video container for `OpenVideo` and a separate audio decoder for `OpenAudio` (`S.Media.Decode.FFmpeg/FFmpegModule.cs:57-66`). An ordinary audio/video file therefore normally has two demux contexts, and a network media item would normally have two independent connections.

Consequences:

- redundant file/network I/O and probing;
- independent buffering and seek state for correlated tracks;
- more hardware decoder and native-resource pressure;
- no atomic answer for “the media opened, with these tracks and duration”;
- no cancellation or progress during network resolution/open;
- one side can silently fail, converting a real error into unexpected video-only or audio-only playback;
- the eventual error says no provider could open the URI instead of reporting the real FFmpeg/network/authentication cause.

This also blocks a clean YouTube implementation, where the operation includes resolving a manifest, selecting one or two streams, downloading or opening them, and producing one correlated timeline.

Replace the split open calls with an operation similar to:

```csharp
ValueTask<MediaOpenResult> OpenAsync(
    MediaOpenRequest request,
    IProgress<MediaPrepareProgress>? progress,
    CancellationToken cancellationToken);
```

The result should own a single media asset/session and expose selected audio/video/subtitle tracks, metadata, duration/live state, seek capability, and structured diagnostics. It can internally share one demux context or explicitly represent paired external streams. Provider-specific options should live in a versioned option bag or typed descriptor rather than continually widening the common record.

Keep source preparation separate from decoder warmup:

- `PrepareAsync` resolves/downloads/caches an external asset.
- `OpenAsync` opens the prepared asset and returns the correlated tracks.
- decoder/output `ArmAsync` allocates the imminent playback resources.

The current `Task.Run` use in standby code does not solve cancellation: it only moves an uninterruptible synchronous native/network call to a worker thread.

Related API residue should be removed or implemented. `MediaPlayerOpenOptions.cs:33` refers to a nonexistent `TryOpenStream`; the deinterlacer factory is threaded through options/registration but has no production consumer; stream spooling/seekability hints are not a usable registry stream-open surface.

### NXT-03 — the dispatcher serializes latency, not just state

The session dispatcher is a good ownership boundary for short state transitions. It should not await arbitrary user media I/O, cue pre/post waits, fade completion, or long end/loop work. Today `CueGraph.FireAsync` performs pre-wait, action, post-wait, and recursive auto-continue as one awaited operation. `ShowSession.FireCueAsync` invokes that work on its serialized dispatcher. There is no caller cancellation token on the public fire operation.

A long pre-wait, slow source open, blocked network prepare, or recursive follow-on chain can keep later stop, seek, GO, load, and shutdown commands queued behind it. A show-control dispatcher that cannot promptly process STOP is not safe.

Use the dispatcher only to validate state, create a cue-operation object, and atomically register its cancellation source. Execute waits, I/O, and monitoring outside the dispatcher. Re-enter it for small commits. Stop/load/dispose must cancel active work without waiting behind that same work. Define explicit operation generations so late completions from a canceled cue cannot mutate a newer show.

Add tests where open never completes, pre-wait is minutes long, a follow-on cycle exists, and stop/load/dispose must complete within a bounded time.

### NXT-04 — synchronization is designed but not integrated

`SourceSyncGroup` and `LiveTimelineDriver` have implementations and tests, but no production player/session call sites. `NDIModule.cs:7-18` explicitly says live A/V correlation will land later and uses its own shared-receiver/rebase behavior. `ClipCompositionRuntime.SetClockMaster` exists at `ClipCompositionRuntime.cs:364`; the retained old HaPlay cue engine calls it through its wrapper, but the new `ShowSession` path does not. `TransportGroup.SessionClock` is used as snapshot/reference state rather than as the clock driving all clips and compositions.

As a result:

- session compositions normally free-run instead of following the group/audio clock;
- multi-source sessions can start sequentially with observable skew;
- live input correlation is separate from file/session synchronization;
- pause, seek, discontinuity, and rate changes do not have one authoritative propagation path;
- `VideoPresentSyncGroup` remains a tested primitive rather than the output presentation mechanism;
- the multi-output smoke fans one tick to windows but does not demonstrate locked physical presentation over time.

The fix is architectural rather than another small helper. Define one timeline contract for each transport group:

- monotonic master time plus generation/discontinuity ID;
- play/pause/rate/seek state;
- cue-local origin and trim offset;
- source correlation for live timestamps;
- frame selection/drop/hold policy;
- output presentation target time.

Every audio renderer, video selector, composition, subtitle track, and plugin layer should receive that contract. Start a group with a barrier: prepare all members, choose a shared start epoch, then release them. Test measurable A/V and output skew over long runs, seeks, pause/resume, underrun, and clock discontinuities. Tests should assert tolerances, not simply that frames appeared.

### NXT-05 — registries do not own native resources or plugin leases

`MediaRegistry` is immutable but not disposable. Module registration is nevertheless performing lifetime work:

- `PortAudioModule.Register` calls `PortAudioRuntime.Acquire()` and never arranges a matching release (`PortAudioModule.cs:12-18`). Its comment says session disposal would land in Phase 4, but it did not.
- `NDIModule.Register` creates a disposable `NDIRuntime` and drops the handle (`NDIModule.cs:23-36`).
- the outbound `SessionBox` stores an `IMediaRegistry`, but destroying a session cannot dispose it (`S.Media.Interop/NativeApi.cs:35-39`).
- `AbiPluginHost.RegisterInto` creates registry adapters, while the sample smoke manually finds and disposes adapters. Normal registry consumers have no equivalent ownership mechanism.
- compositor layer factories captured from a native plugin are not tied to a host lease that prevents unload until every surface/factory is gone.

This leaks native runtime references and defeats the promised deferred-unload safety. Finalizers are not an adequate replacement: a native GL layer finalizer may call plugin destruction on a finalizer thread without the owning GL context.

Introduce a single `MediaHost : IAsyncDisposable` (the name is illustrative) which owns:

- media, compositor, control, subtitle, and output registries;
- module lifetime tokens;
- native library/plugin leases;
- device/runtime services;
- scheduler/dispatcher resources;
- diagnostics and configuration.

Registrations should return owned registrations/lifetime tokens, not perform untracked global acquisition. Objects created by a plugin should retain a reference-counted plugin lease. GL objects should be explicitly retired on the render/context thread before the library can unload. Host disposal should fail/report leaked plugin objects in debug/test builds.

### NXT-06 — the rewrite is not the default runtime and the mapper is lossy

The plans correctly identify the desired boundary: `S.Media.Session` owns show runtime/data and HaPlay owns view state. The actual app is not there.

`MediaPlayerViewModel.ShowSession.cs:12-32` and `MainViewModel.cs:212-219` require `HAPLAY_USE_SHOWSESSION=1`. With no environment variable, the copied `HaPlayPlaybackSession`, `CuePlaybackEngine`, and `SoundboardEngine` remain the runtime. The open Phase 8 items in `Next/09-Phase-Checklists.md:721-725` still call for view-state-only persistence and retirement of old HaPlay.

The opt-in mapper is not parity preserving. It drops or defers action/comment/text cues, deeper nested groups and fire modes, multi-composition cues, corner-pin/advanced placement details, and parts of soundboard/live behavior. `MediaPlayerViewModel.ShowSession.cs` itself documents that fault recovery and end behavior remain on the old engine.

Running both systems indefinitely is more dangerous than either one alone: fixes and semantics diverge, every UI change needs to understand two ownership models, and the opt-in path receives less real use.

Use a strangler cutover with a narrower definition of done:

1. Pick one complete vertical slice, such as ordinary file playback in one player deck.
2. Write black-box parity tests against project loading, routing, transport, output lifecycle, error behavior, and end actions.
3. Make the session implementation the only path for that slice.
4. Delete the replaced engine code immediately.
5. Repeat for live inputs, cue list, soundboard, composition, and control.

Do not map unsupported semantics by silently dropping them. Reject with a precise migration error until the session model supports them.

### NXT-07 — cue and clip semantics expose behavior that is not implemented

Several problems are independently observable:

- GO is documented as finding the next armed/enabled cue, but `ShowSession.cs:861-875` orders by number without filtering armed/enabled state. It sets `LastFiredNumber` before the fire succeeds. A disabled, unarmed, not-ready, or failed cue is skipped on subsequent GO.
- Duplicate cue numbers are not rejected, so “next by number” is not a stable cursor.
- recursive auto-continue (`CueGraph.cs:225`) has no cycle validation. A cyclic follow-on graph can run without termination.
- `CueFaultPolicy` exposes nine policies, but the implementation only gives special behavior to `StopShow`; the other values largely collapse to “continue.” `StopTargetIds`, `PreloadKey`, and `FallbackOutputId` are queried or stored but not executed as their names imply.
- `ShowClipBinding.FadeOut` is never applied. End action `FadeOutAndStop` explicitly says the fade is deferred (`ShowSession.cs:816`).
- fade-in routes start at zero and ramp to `1`, ignoring a configured route gain below or above unity (`ShowSession.cs:722` and its call sites). Video opacity is not faded.
- end detection polls at 100 ms with a 120 ms guard, so stop/freeze/loop can occur before the true final frame/sample. Freeze can retain a pre-final frame; hardware-frame hold-last is not generally supported.
- loop seek executes in the same orchestration path rather than as a precisely scheduled transport discontinuity.
- per-cue model is effectively one clip because `_clipsByCue = document.Clips.ToDictionary(c => c.CueId, ...)`; it cannot represent a cue launching multiple correlated media bindings.

Simplify before expanding: expose only fault/end policies that have implemented, tested semantics. Add a `ShowDocumentValidator` for unique IDs/numbers, existing targets, acyclic follow-on links (unless an explicitly bounded loop is supported), valid ranges, route/output references, and supported feature combinations. Move `LastFiredNumber` only after the cue is accepted/fired, or track a cue identity cursor rather than a number.

### NXT-08 — the outbound C ABI can throw on invalid handles

`NativeApi.TryBox` checks zero but then directly calls `GCHandle.FromIntPtr(session)` and reads `handle.Target` (`S.Media.Interop/NativeApi.cs:277-299`). An arbitrary, stale, or double-destroyed nonzero handle can throw before the individual export's `try/catch`, allowing an exception to cross an `[UnmanagedCallersOnly]` boundary. That can terminate the process.

`mfp_shutdown` only sets `s_initialized=false` (`NativeApi.cs:51-52`). After shutdown, `TryBox` refuses every existing session, including destruction, so live session handles and their resources cannot be cleanly released.

Use an internal synchronized handle table keyed by monotonically increasing ID plus generation, not raw `GCHandle` pointers supplied back by untrusted callers. Every exported function needs a top-level no-throw boundary. Shutdown should either reject while sessions are alive, destroy them deterministically, or leave destruction valid after runtime shutdown. Add a C fuzz/negative test for random handles, nulls, stale handles, double destroy, concurrent destroy/call, malformed UTF-8/JSON, and shutdown ordering.

The outbound API is also narrower than its smoke name may suggest: sessions currently do not attach an audio backend/output or a visible video output. The media smoke proves open/transport/playhead behavior, not audible/displayed output.

### NXT-09 — plugin support is not yet a host feature

The two-tier model is reasonable. Tier A static registration is appropriate for trusted first-party .NET modules under NativeAOT. Tier B is a reasonable direction for language-neutral dynamic plugins. What exists today is the adapter core and proof smokes, not an end-user plugin system.

Missing host capabilities include:

- a plugin directory/catalog and deterministic scan order;
- allow/deny policy, duplicate plugin/capability ID rules, ABI range negotiation, and architecture/OS diagnostics;
- typed/versioned configuration and secrets handling;
- project-relative asset resolution;
- plugin status, prepare progress, faults, and operator-visible logs;
- ownership/unload rules described in NXT-05;
- HaPlay settings/editor integration;
- capability use by the actual session/compositor.

HaPlay does not reference `S.Abi` or load a plugin catalog. `MediaRuntime` constructs a fixed set of modules. `AbiPluginHost.Load(path)` loads one explicitly named library, and `RegisterInto` binds only the currently supported adapters. Presenter/subtitle registration mentioned in builder comments never became registry surfaces.

Treat the ABI header as experimental until the YouTube/MMD design exercises it. Freezing it now would preserve several gaps:

- source open options are ignored by native source adapters;
- native sources do not implement the managed seekable-source interface even though the vtable includes a seek operation;
- no position/duration reporting exists for finite plugin sources;
- host sync negotiation advertises only `MFP_SYNC_NONE` (`AbiPluginHost.cs:161`);
- GL texture frames are declared but rejected by `AbiFrameMarshal.cs:75`;
- Linux dma-buf and Windows D3D11 handles lack a demonstrated fence/keyed-mutex synchronization contract;
- Windows plugin adapters are not exercised by CI.

### NXT-10 — layer surfaces are not first-class compositor layers

The MMD idea makes this particularly visible. Native layer surfaces can be created and manually called by `AbiGlSmoke`, but normal session composition never requests them. `GlVideoCompositor.CompositeWithSurfaces` is a concrete-type method, not part of the ordinary `IVideoCompositor` operation. `ClipCompositionRuntime` handles frame layers and has no surface-layer document/runtime model.

The current GL implementation renders all frame layers, then all surfaces. It therefore cannot interleave a 3D surface between ordinary video/text/image layers by Z order. It is also absent from `CompositeMulti` and the zero-copy/multi-output path, and the current surface call returns through a CPU frame readback. The host manually calls `ConfigureGl`; lifecycle/context loss is not modeled.

Replace parallel “frames then surfaces” APIs with one ordered render graph, for example a discriminated `CompositorLayerContent` containing frame, retained GPU texture, or render-surface content. The compositor should support the layer kind through its interface, preserve one Z order, and render all mapped outputs in one context/share group where possible. Surface configuration needs GL/API version, profile, extensions/capabilities, target format/color space/origin, context generation/loss, and output timing.

GL state isolation is incomplete for a 3D renderer. The host currently preserves useful state, but a realistic MMD plugin will use depth, culling, stencil, more buffer/texture bindings, and potentially framebuffer/sRGB state. Define the exact state contract: either the plugin must restore all state, or the host captures/restores the complete supported state subset and tests a deliberately hostile plugin.

### NXT-11 — the session path does not realize the intended GPU/performance design

`ShowSession` constructs `ClipCompositionRuntime` directly without a compositor factory (`ShowSession.cs:181-194`), so the default path is CPU composition. The HaPlay opt-in session path can consequently turn decoded frames into full BGRA CPU canvases before handing them to SDL/NDI, even when GL hardware composition is available. This is a performance regression risk relative to the intended zero-copy architecture.

Other concrete hot-path issues:

- audio and video commonly open the same FFmpeg item twice (NXT-02);
- `ClipCompositionRuntime` materializes `_acquired.ToList()` per output frame;
- subtitle feed iteration snapshots with `ToArray()` per frame;
- integrated multi-warp creates new request arrays/lists in its frame loop;
- subtitle overlays render/copy a full-canvas BGRA buffer each composition tick even when the subtitle event has not changed;
- `CompositeWithSurfaces` reads the result back to CPU;
- standby opening the next items holds decoders/native resources but is not a persistent content pre-cache.

At 1920x1080, one BGRA canvas is about 7.9 MiB. Rebuilding/copying one unchanged full-frame subtitle overlay at 60 fps is approximately 475 MiB/s of memory traffic before composition and output copies. Cache subtitle render output until event/style/canvas changes.

Add an injected compositor factory to `ShowSession`, then benchmark representative paths instead of relying only on smoke tools:

- 1080p60 single video to one and four outputs;
- two/four/eight-layer compositions;
- subtitles idle versus changing;
- affine/warp/multi-output;
- software decode, hardware decode, and fallback;
- NDI input/output;
- allocations/frame, CPU/GPU time, dropped/late frames, and end-to-end latency.

Set explicit budgets and gate regressions. Allocation tests around the render/audio callbacks are cheap and should run in CI; sustained GPU/NDI tests can be nightly/manual hardware jobs.

### NXT-12 — persistence is not transactional or sufficiently validated

`ShowDocument.Version` is serialized but unsupported future versions are not rejected. Deserialization and load do not comprehensively validate null collections, duplicate keys, invalid references, dimensions/rates, cue graph cycles, or supported behavior.

`LoadDocumentCoreAsync` disposes the current groups and compositions before it completes construction of the replacement (`ShowSession.cs:164-202`). A malformed document or factory failure can therefore destroy a running show and leave a partially built replacement.

Use a versioned `ShowDocumentValidator` and a staged runtime graph:

1. deserialize with strict source-generated JSON;
2. validate schema version and all referential/semantic invariants;
3. construct and prepare a new graph in isolation;
4. atomically swap it on the dispatcher;
5. retire the old graph afterward.

Return structured validation diagnostics with JSON paths and IDs. Migration between schema versions should be explicit and tested with stored fixtures.

### NXT-13 — project and type structure can be simplified materially

Two projects are only Phase 0 shells with no implementation source: `S.Media.Images.Skia` and `S.Media.Encode.FFmpeg`. The documents and project graph make them look like capabilities, while text rendering remains in HaPlay and encoding/remux is absent. Either implement a small end-to-end capability and its tests, or remove the projects from the active solution until scheduled.

The current system also contains two product orchestration layers: `ShowSession` in the framework and `CuePlaybackEngine`/`HaPlayPlaybackSession`/`SoundboardEngine` in HaPlay. This is the largest simplification opportunity. Finishing the strangler and deleting each replaced slice matters more than further partial-class organization.

Large framework files also combine separable responsibilities:

- `MediaContainerSharedDemux.cs` (~2,445 lines): container ownership, stream decode, seeking, and coordination;
- `AudioRouter.cs` (~1,789): graph mutation, pumps, buffering, diagnostics, and policy;
- `GlVideoCompositor.cs` (~1,669): resource ownership, shader/format paths, composition, surfaces, and readback;
- `ClipCompositionRuntime.cs` (~1,493): layer graph, timing, subtitle feeds, output mapping, pumps, and lifecycle;
- `ShowSession.cs` (~1,094): persistence graph, cue runtime, standby, transport, voice/soundboard, and monitoring.

Split around ownership and test seams, not merely file size. Good boundaries include prepared asset, transport group, cue scheduler, composition graph, output lease set, render backend, and document compiler. Mechanical SIMD/generated interop code can remain large if clearly isolated.

`SubtitleOverlayFactory` should not live in outbound `S.Media.Interop` while HaPlay references that project only to get subtitle composition help. Move it into a subtitle integration module or host composition project; keep the outbound C API as a thin host facade.

### NXT-14 — test count hides weak coverage at the new seams

The 1,301 passing tests are useful, but distribution matters. Approximate counts from the run include 436 Core, 156 Control, 114 GPU, and 472 HaPlay tests, while Players has 4, FFmpeg 15, Session 48, Compositor 12, and the plugin/ABI behavior is largely smoke executables. Those are precisely the packages where the rewrite introduces new ownership/orchestration boundaries.

There are no direct replacement test projects for several old capabilities, including PortAudio, MiniAudio, NDI, FFmpeg encode, and Skia images. Old diagnostic tools such as `EncoderSmoke` and `TransportSyncProbe` also do not have complete replacements.

High-value missing tests:

- open one A/V file and assert one shared underlying asset/demux lifetime;
- cancellation and fault propagation during probe/open/prepare;
- STOP while cue pre-wait/open/auto-continue is active;
- document-load rollback on failure;
- output borrow/reload/release/dispose ordering;
- cue cycle, duplicate number/ID, invalid reference, disabled/unarmed GO;
- exact configured audio gain and fade-out behavior;
- plugin unload while adapters/surfaces exist;
- bad/stale C handles and all unmanaged error paths;
- correlated A/V after seek/pause/rate/discontinuity;
- layer-surface Z ordering and GL state restoration;
- real default-on HaPlay workflows, not only mapper/unit tests.

### NXT-15 — CI does not enforce the documented completion criteria

`.github/workflows/next-build.yml:8-12` runs on pushes only to branch `next`; the reviewed branch is `next-2`. Pull requests still trigger, but direct branch work can miss CI.

The AOT job publishes `AotSmoke`, not every relevant deliverable. HaPlay explicitly disables AOT in its project, and no HaPlay launch/self-exit smoke is present. ABI plugin smoke is Linux-only; GL and subtitle tests are `continue-on-error`; the real-media outbound C smoke catches failure. There is no Windows dynamic-plugin smoke, no gating visual output test, and no performance/synchronization threshold.

Recommended gates:

1. build and all managed tests on Linux and Windows;
2. publish/run `AotSmoke`, `S.Media.Interop`, and HaPlay NativeAOT deliverables;
3. launch HaPlay under Xvfb/Windows with a current explicit test hook and assert a rendered frame plus clean exit;
4. compile/load native sample plugins on Linux and Windows;
5. make GL layer, subtitles, and real-media C client gating after pinning native dependencies;
6. run malformed-handle ABI tests;
7. run allocation budgets in PR CI and sustained playback/sync tests in scheduled hardware CI.

Update checklist claims from evidence generated by CI rather than hand-maintained historical totals. A statement like “Phase 8 done” should require its own exit line—old HaPlay retired and feature parity—not only a successful solution build.

## Proposed simplification target

The framework will become easier to reason about if the public architecture is reduced to five owned concepts:

```text
MediaHost
  +-- CapabilityCatalog (built-in modules + native plugin leases)
  +-- MediaAssetService (probe / prepare / cache / atomic open)
  +-- ShowRuntime (validated document + cue scheduler + transport groups)
  +-- RenderRuntime (ordered layers + compositor + output leases)
  +-- ControlRuntime (OSC / MIDI / scripting / C API adapters)
```

Recommended simplifications:

- One owned host instead of several registries plus untracked native runtime acquisitions.
- One atomic `MediaAsset` open instead of independent audio/video source opens.
- One session timeline/discontinuity contract instead of several unconnected clock helpers.
- One ordered layer graph for video frames, images/text/subtitles, and plugin GPU surfaces.
- One cue runtime in `S.Media.Session`; HaPlay view models become commands/projections over it.
- One generic media source descriptor in UI/project data. Avoid adding a new closed `PlaylistItem` subtype and mapper switch for every provider. A descriptor should contain URI, provider hint, display metadata, and versioned provider options.
- One explicit output lease type carrying `Output`, `Release`, ownership, and thread-affinity metadata.
- Fewer advertised policies. Add an enum value only when its semantics and tests exist.

## YouTubeExplode plugin feasibility and design

### Verdict

**Feasible and a good first external-source feature, after NXT-02 and NXT-05.** Implement it first as a Tier A, build-time managed module such as `S.Media.Source.YouTube`. YoutubeExplode is already managed, declares trim/AOT compatibility for .NET 7+ in the bundled project (`YoutubeExplode.csproj:3-12`), and exposes asynchronous cancellable operations. Wrapping it in the current C ABI would add marshaling and NativeAOT packaging while losing the async/options/metadata behavior it needs.

The folder name `Reference/YoutubeExplode-6.6` is misleading: its `Directory.Build.props` declares `0.0.0-dev`, targets include .NET 10, and copyright/source content are current. Pin an exact NuGet release or repository commit and record it in dependency metadata; do not assume this is the published 6.6 source merely from the directory name. Its license is MIT.

YoutubeExplode's own README notes that muxed streams are not the reliable best-quality path; high quality commonly requires a separate video-only and audio-only stream. `MediaStream` is seekable and uses asynchronous segmented requests/retries (`MediaStream.cs:11-118`), but its synchronous `Read` blocks on `ReadAsync`. Passing that into today's synchronous split source API would compound blocking and create two unrelated opens.

### Source and project model

Use a canonical URI such as `youtube:<video-id>` or `youtube://<video-id>`. The provider may also recognize `youtube.com`/`youtu.be` URLs with higher confidence than FFmpeg, but persist the normalized video ID so link variations do not change cache identity.

Provider options should include at least:

- mode: audio only, video only, or audio + video;
- quality policy: best, maximum height, preferred codec/container, bandwidth cap;
- audio language/track preference where available;
- cache policy: require full local copy, prefer cache, or allow progressive;
- optional authentication/profile reference, never raw secrets in `ShowDocument`;
- captions selection as a later subtitle-track integration.

Both HaPlay player and cue playback should store the same generic source descriptor. `ShowClipBinding.MediaPath` can already carry an arbitrary URI, but HaPlay's `PlaylistItem` hierarchy and mapper use closed type switches. Replace that switch with provider-neutral source data plus separately cached display metadata/status.

### Playback architecture

For dependable show playback, default to full preparation before a cue becomes ready:

1. Resolve video metadata and stream manifest asynchronously.
2. Select the audio/video stream IDs according to policy.
3. Download to content-addressed partial files with cancellation and progress.
4. Verify expected lengths and atomically rename completed files.
5. Either:
   - remux the two streams without transcoding into one local Matroska/MP4 asset, then use the normal FFmpeg one-asset path; or
   - return a paired `MediaAsset` whose audio/video tracks share an explicit timeline.
6. Open/arm the local prepared asset through the standard player/session path.

Remuxing to one local file is the simpler and more reliable first implementation, but it requires implementing the currently empty `S.Media.Encode.FFmpeg` capability or a narrowly scoped FFmpeg remux service. Avoid invoking an untracked shell `ffmpeg` process from cue logic.

Progressive playback can be added later. It must handle URL expiry/refresh, range behavior, bandwidth stalls, and independent audio/video backpressure. It is a poor default for time-critical cue playback.

### Cache/pre-caching design

The current standby engine warms a small number of decoders. It is not a persistent pre-cache and can consume network connections and hardware decoders while waiting. Add a source-provider preparation interface, for example:

```csharp
interface IMediaPreparer
{
    ValueTask<PreparedMedia> PrepareAsync(
        MediaSourceDescriptor source,
        PreparePolicy policy,
        IProgress<MediaPrepareProgress>? progress,
        CancellationToken cancellationToken);
}
```

Cache properties:

- key by video ID, selected itag/stream IDs, and format policy/version;
- keep metadata/manifest entries on a short TTL, separate from immutable downloaded content;
- use `.partial` files and atomic final rename;
- coalesce concurrent requests for the same key;
- validate content length and store hashes where practical;
- use byte- and age-based LRU quotas;
- pin files while prepared/open/playing so eviction cannot remove them;
- expose ready/downloading/failed/stale status and progress to HaPlay;
- recover abandoned partial files and interrupted cache index writes;
- refresh expired stream URLs and return explicit offline/region/login/removed errors;
- allow a show-level “prepare all external media” command and readiness report.

`FireCueAsync` should not implicitly begin a multi-gigabyte download on the dispatcher. For reliable mode, GO is accepted only after the source reports ready. The UI should allow an explicit progressive override.

### YouTube risks and tests

YouTube access is based on an internal, changing service surface; availability can break independently of MFPlayer. There are also content-rights, regional, age/login, and service-terms considerations. The application should not promise that every URL is playable and should show the actual provider error. This is an operational/product warning, not legal advice.

Required tests:

- URL/ID normalization and provider scoring;
- audio-only, video-only, muxed, and separate-stream selection;
- atomic paired A/V open and seek after remux;
- cancellation at resolve/download/remux/open;
- cache coalescing, quota, pinning, crash recovery, corruption, and expired URL refresh;
- no-network playback from a completed cache;
- AOT publish/run with the exact pinned YoutubeExplode dependency;
- integration tests behind an opt-in environment flag because live YouTube behavior is external and unstable.

Suggested delivery sequence:

1. Implement atomic async asset open and host lifetime.
2. Implement generic source descriptors and HaPlay readiness UI.
3. Add local-file prefetch/cache infrastructure and FFmpeg remux.
4. Add YouTube metadata/manifest/selection and full-download preparation.
5. Integrate player and cue paths through the same session API.
6. Consider progressive playback only after the cached path is reliable.

## MMD PMX/VMD plugin feasibility and design

### Verdict

**Feasible, but a major renderer/runtime feature and a useful test case for redesigning the layer-surface ABI before it is frozen.** The best fit is a 3D render-surface capability composed as one or more layers in the normal GPU compositor. It is not naturally a video decoder. It needs cue-local time, seek/discontinuity notifications, deterministic simulation behavior, asynchronous asset preparation, and render-context lifecycle.

For a trusted first-party implementation, a Tier A module is the fastest route because it can use typed preparation and render APIs directly. If dynamic installation or third-party distribution is a requirement, implement it as a Tier B native plugin only after the surface ABI changes below. In either form, the same session layer should power a cue item and a HaPlay deck/editor preview; do not create a separate MMD-only transport. An optional music/media source can join the same transport group so its audio and the VMD timeline share one clock.

The included `blender_mmd_tools_append-4.5.11` is not the parser/renderer to embed. Its README says it adjusts Blender scenes in cooperation with the separate `MMD-Blender/blender_mmd_tools`, and its developer guide explicitly puts MMD core functionality out of scope. It depends heavily on Blender's `bpy` environment. It is GPLv3. Use it as behavioral/reference material only unless the project deliberately accepts Blender and GPL implications; obtain an independent compatible parser/renderer implementation or write one against the PMX/VMD specifications. This is not legal advice.

The supplied test assets are suitable only for local manual development under their included terms. The motion README restricts use to non-commercial/personal use and requires credit/approval outside personal use. The model README forbids commercial use. Do not publish them, put them in public/distributed CI artifacts, or treat them as redistributable fixtures. Replace them with purpose-made/cleared minimal PMX/VMD test fixtures before automated distribution.

### What the module must implement

PMX parsing/runtime involves more than mesh loading:

- UTF-8/UTF-16 strings and variable-width vertex/texture/material/bone/morph/rigid-body indices;
- BDEF1/BDEF2/BDEF4, SDEF, and QDEF skinning modes;
- material/toon/sphere textures, transparency/order, double-sided behavior, outlines, and shadows;
- bone hierarchy, append/inherit transforms, fixed/local axes, external parent, IK constraints, and deform order;
- vertex, UV, bone, material, group, flip, and impulse morphs;
- rigid bodies and joints with MMD-compatible physics/coordinate conventions.

VMD requires:

- Shift-JIS names and model/bone-name matching;
- bone and morph tracks with MMD Bezier interpolation;
- camera, light, self-shadow, and IK enable tracks if those are in scope;
- the MMD 30 fps timeline convention and defined conversion to the session timebase.

Rendering requires explicit coordinate/handedness conversion, GPU skinning and morph accumulation, material sorting, alpha behavior, outline pass, camera/light handling, and stable texture/color-space rules. Multiple models require per-instance pose/morph/physics state while sharing immutable mesh/texture/shader resources.

### Required framework changes

The current `MfpLayerSurface` supplies only global/master time to render and canvas width/height during `configure_gl`. That is insufficient for a cue-bound animation:

- The surface needs cue-local time or an explicit timeline origin/trim/rate.
- It needs pause, seek, loop, and discontinuity/generation events. Inferring a rewind by seeing time decrease is not enough for deterministic physics.
- It needs prepare/readiness/fault/progress outside the GL render callback.
- It needs context-created, resized, context-lost, and dispose-on-render-thread lifecycle.
- Configuration needs GL version/profile/extensions, target format/color space/origin, sample/depth requirements, and output mapping information.
- The normal composition graph must carry surface layers with Z order, opacity/blend/transform, rather than the smoke manually calling a concrete compositor method.
- Multi-output behavior must be defined. Ideally render the scene once to a retained texture and sample/warp that texture into all outputs, rather than advance physics/render independently per output.

Do not let plugin `Render` perform file I/O, shader compilation, texture decoding, or physics catch-up without a bound. `PrepareAsync` should parse and preprocess on worker threads; GL upload/compilation should be incremental or performed during an explicit render-thread attach phase before the cue reports ready.

### Proposed plugin/document model

A composition layer can reference a surface source descriptor:

```json
{
  "kind": "plugin-surface",
  "provider": "org.example.mmd",
  "configVersion": 1,
  "config": {
    "models": [
      {
        "pmx": "assets/model-a/model.pmx",
        "motions": ["motions/dance.vmd"],
        "transform": { "position": [0, 0, 0], "scale": 1.0 }
      }
    ],
    "cameraMotion": "motions/camera.vmd",
    "physics": "deterministic",
    "background": "transparent"
  }
}
```

The host should treat `config` as provider-owned, versioned UTF-8 JSON with a size limit and schema/diagnostics hook. Resolve paths through a project asset resolver; plugins should not receive arbitrary implicit working-directory paths. Config should support any number of models, per-model motion stacks/offsets, transforms, visibility, and eventually material overrides.

Cache immutable prepared data by content hash:

- parsed/validated PMX representation;
- normalized vertex/index buffers and influence data;
- decoded/converted textures and mipmaps;
- compiled shader variants and material pipeline keys;
- parsed/resampled VMD curves.

Keep pose, morph weights, IK, physics bodies, and transient GPU instance buffers per layer instance. Pin shared assets while any surface uses them.

### Physics and seeking

Bullet is the usual practical physics basis, but “same library” does not guarantee MMD-compatible behavior. Use a fixed simulation step independent of render frame rate and define catch-up limits. Seeking is the difficult part:

- deterministic reset and replay from frame zero is correct but can be too slow;
- periodic simulation checkpoints make seeks practical but consume storage/memory and must be invalidated by configuration changes;
- arbitrary backward seek must never continue from future physics state;
- loops need a defined warm-start/reset behavior;
- multiple outputs must sample one simulation state, not advance it multiple times.

The first useful release should omit dynamic physics or pre-bake it. Add deterministic runtime physics only after timeline/discontinuity tests exist.

### Staged MMD implementation

1. **Format/validation prototype:** purpose-made tiny PMX/VMD fixtures; parse and inspect one model, skeleton, materials, morphs, and a short motion. No plugin/GL yet.
2. **Renderer MVP:** one model, bone/morph animation, materials/textures, camera, outline, transparent background, no physics. Render golden images at fixed times.
3. **Framework surface integration:** first-class ordered surface layer, asynchronous prepare, cue-local clock, seek/pause/loop events, render-thread lifecycle, output lease tests.
4. **Multiple models:** shared resource cache, per-instance state, model/motion mapping UI, composition transforms, one render reused across outputs.
5. **Physics:** fixed-step Bullet integration, reset/replay/checkpoints, deterministic test scenes.
6. **Advanced MMD parity:** complex morph/material behaviors, IK edge cases, camera/light/shadow tracks, performance tuning, context-loss recovery.

### MMD acceptance tests

- parser bounds/fuzz tests for malformed PMX/VMD indices, counts, encodings, and truncated data;
- golden skeleton/morph interpolation values at exact VMD frames;
- golden rendered frames for cleared fixtures on a pinned software renderer;
- seek forward/backward, pause/resume, loop, and discontinuity correctness;
- two models with independent motions and shared assets;
- output fan-out does not advance simulation twice;
- plugin GL state does not corrupt following video/text layers;
- context loss/recreation and unload with live/retired surfaces;
- frame-time/allocation budgets at representative model/bone/morph counts;
- NativeAOT host plus plugin loading on Linux and Windows.

## Recommended delivery order

The sequence below minimizes throwaway work and uses the proposed plugins to validate the abstractions rather than adding special cases.

### Gate 1 — make the existing session safe

- Fix borrowed output leases (NXT-01).
- Move long work off the dispatcher and add cancellation/generations (NXT-03).
- Add strict document validation and transactional load (NXT-12).
- Correct GO, cue cycles, fade/gain/end behavior (NXT-07).

**Exit:** stop/load/dispose are bounded under faults; output reload is safe; invalid documents cannot replace a running show.

### Gate 2 — establish one owned media host and asset API

- Add disposable host/module/plugin lifetimes (NXT-05).
- Replace split synchronous media opening with async atomic `MediaAsset` open (NXT-02).
- Preserve structured errors and add prepare/cache contracts.
- Fix the outbound C handle table and shutdown behavior (NXT-08).

**Exit:** one A/V item uses one correlated asset lifetime; every native acquisition has a deterministic release; cancellation is tested.

### Gate 3 — converge time and rendering

- Wire a transport-group clock/discontinuity contract through players, compositions, subtitles, live sources, and outputs (NXT-04).
- Make compositor selection injectable and default to the appropriate GPU path.
- Unify frame and surface layers and multi-output rendering (NXT-10/NXT-11).
- Add measured sync/performance gates.

**Exit:** toleranced A/V and multi-output sync tests pass across seek/pause/loop; ordinary session playback uses the intended GPU path.

### Gate 4 — finish HaPlay cutover

- Add provider-neutral source descriptors and remove lossy mapper behavior.
- Cut over one complete slice at a time with parity tests.
- Delete the corresponding old engine code after each cutover.
- Re-enable and gate HaPlay NativeAOT; replace historical smoke claims with a current test hook.

**Exit:** no `HAPLAY_USE_SHOWSESSION` branch remains, old playback god objects are retired, and `ShowDocument` owns runtime data.

### Gate 5 — add YouTube cached playback

- Implement FFmpeg remux and persistent preparation cache.
- Add the Tier A YouTube provider, readiness UI, and player/cue integration.
- Keep progressive playback optional until cached playback is proven.

**Exit:** a prepared item plays offline through both player and cue paths with correct A/V seek and deterministic cache behavior.

### Gate 6 — revise/freeze ABI with the MMD surface prototype

- Build the no-physics MMD renderer prototype.
- Use it to finalize surface lifecycle, time/discontinuity, configuration, GL capability/state, and multi-output ABI.
- Add Windows/Linux conformance plugins before declaring ABI v1 stable.

**Exit:** the same cleared MMD fixture renders deterministically as a normal composition layer, survives seek/context recreation, and unloads without live leases.

## What is already worth keeping

The review is critical because the remaining issues sit at ownership boundaries, not because the rewrite should be discarded. Preserve and build on:

- the explicit dependency/architecture tests;
- immutable compose-time registration for Tier A modules;
- language-neutral C structs/vtables and real C smoke clients;
- pooled/ref-counted frame ownership work;
- platform GPU interop fallbacks and focused smokes;
- headless `ShowDocument`/`ShowSession` as the destination boundary;
- source-generated JSON direction for NativeAOT;
- separate control/OSC/MIDI packages;
- the extensive low-level Core/GPU/HaPlay regression tests.

The next phase should be consolidation rather than adding more capability surfaces: finish ownership, cancellation, synchronization, and cutover; remove the duplicate paths; then use YouTube and MMD as end-to-end proofs of the resulting plugin model.

## Verification appendix

Representative commands used during this review:

```bash
dotnet build next/MFPlayer.Next.sln -m:1 --no-restore -v:minimal
dotnet test next/MFPlayer.Next.sln -c Debug --no-build --no-restore

dotnet run --project next/MediaFramework/Tools/FrameDump/FrameDump.csproj \
  --no-build -- /run/media/sekoree/512/mambo.mp4 30

dotnet run --project next/MediaFramework/Tools/VideoPlaybackSmoke/VideoPlaybackSmoke.csproj \
  --no-build -- /run/media/sekoree/512/mambo.mp4 3

dotnet run --project next/MediaFramework/Tools/AbiSmoke/AbiSmoke.csproj --no-build
xvfb-run -a dotnet run --project next/MediaFramework/Tools/AbiGlSmoke/AbiGlSmoke.csproj --no-build

dotnet publish next/MediaFramework/Tools/AotSmoke/AotSmoke.csproj \
  -c Release -r linux-x64 -p:PublishAot=true

dotnet publish next/MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj \
  -c Release -r linux-x64 -p:PublishAot=true

dotnet publish next/UI/HaPlay.Desktop/HaPlay.Desktop.csproj \
  -c Release -r linux-x64 -p:PublishAot=true
```

Review limitations:

- This was a static/development-host review plus representative smoke execution, not a multi-day soak or measurements on production GPU/audio/NDI hardware.
- I did not test live YouTube requests; feasibility is based on the bundled source and the framework boundary.
- I did not execute the MMD assets in Blender or implement a parser; feasibility is based on the included code/assets/readmes and the compositor/plugin contracts.
- Licensing notes summarize included files and are not legal advice.

---

# Independent verification pass (second reviewer, 2026-06-30)

**Revision checked:** `79b70ed` on `next-2`, same as the original review.

**Method:** I re-read every cited file and line, traced the call sites and object lifetimes behind each claim, and confirmed the runtime consequence rather than the wording. I did **not** re-run the full test suite, AOT publishes, or smoke tools — I relied on the original review for those execution baselines. Every code-level finding below was re-derived from the current source.

**Verdict: all 15 findings (NXT-01 … NXT-15) are confirmed.** None were overturned. Two carry minor factual corrections that do not change severity. I add two new findings (NXT-16, NXT-17) and several strengthening notes, including a concrete reproduction for the NXT-01 blocker.

## Verdict table

| ID | Verdict | Strongest verified evidence |
|---|---|---|
| NXT-01 | **Confirmed (blocker)** | `ShowSession.cs:188-192` wraps *every* host output `DisposeOutputOnRuntimeDispose: true`; `ClipCompositionRuntime.cs:971-975`+`1265-1266` dispose it on retire; `LocalVideoPreviewRuntime.cs:288-296` returns the persistent `_sink`, `:298-319` keeps it alive on release. Reproducible — see below. |
| NXT-02 | **Confirmed (blocker)** | `MediaPlayer.cs:436-441` opens video/audio separately, `catch { … = null; }`; `:445` emits a generic "no decoder" error; `FFmpegModule.cs:60-68` builds two demux contexts. |
| NXT-03 | **Confirmed (blocker)** | `SessionDispatcher.cs:128-134` `await work()` sequentially in a `foreach` — the queue is parked for the whole operation; `FireCueAsync` (`ShowSession.cs:857`) threads **no** cancellation token. |
| NXT-04 | **Confirmed (blocker)** | `SetClockMaster` called only by the old `CuePlaybackEngine.cs:1539`, never by `ShowSession`; `SourceSyncGroup`/`LiveTimelineDriver` appear only in probe tools + an NDIModule comment. |
| NXT-05 | **Confirmed (blocker)** | `PortAudioModule.cs:17` `Acquire()` with release "Phase 4" (absent); `NDIModule.cs:31-33` drops the `NDIRuntime` handle; `NativeApi.SessionBox` holds a non-disposable `IMediaRegistry`. |
| NXT-06 | **Confirmed (high)** | `MainViewModel.cs:219` early-returns unless `HAPLAY_USE_SHOWSESSION=1`; `HaPlayShowMapper.cs:16-20,63,188` documents the dropped semantics in its own comments. |
| NXT-07 | **Confirmed (high)**, one count nit | `GoAsync` (`ShowSession.cs:865-873`) no armed/enabled filter + `LastFiredNumber` set before the fire result; `CueGraph.cs:229` recursive auto-continue, no cycle check; `:237` only `StopShow` handled. **Correction: the enum has 8 members, not 9.** |
| NXT-08 | **Confirmed (high)** | `NativeApi.cs:291-292` `GCHandle.FromIntPtr(session).Target` on caller-supplied pointer, called *outside* the try/catch in every export (e.g. `:99`, `:118`, `:144`); `:52` shutdown blocks `SessionDestroy`. |
| NXT-09 | **Confirmed (high)** | No `.csproj` outside `AbiSmoke`/`AbiGlSmoke` references `S.Abi`; `AbiPluginHost.cs:161` advertises only `MFP_SYNC_NONE`; `AbiFrameMarshal.cs:75` rejects `GlTexture` frames. |
| NXT-10 | **Confirmed (high)** | `CompositeWithSurfaces` is a concrete `GlVideoCompositor.cs:498` method (only caller is a smoke tool); `IVideoCompositor` exposes only frame-layer `Composite`. |
| NXT-11 | **Confirmed (high)** | `ClipCompositionRuntime.cs:108`+`:575` default to `CpuVideoCompositor` (`RequiresBgraLayerConversion: true`); `PumpOneFrame` allocates `_acquired.ToList()` (`:679`) and subtitle `ToArray()` (`:464`) per frame. |
| NXT-12 | **Confirmed (medium)** | `ShowDocument.FromJson` (`:157`) ignores the `Version` field; `LoadDocumentCoreAsync` (`ShowSession.cs:169-195`) disposes the live groups/compositions before building replacements. |
| NXT-13 | **Confirmed (medium)** | `S.Media.Images.Skia` and `S.Media.Encode.FFmpeg` contain **0** `.cs` files. |
| NXT-14 | **Confirmed (medium)** | Test attributes by project: Players **4**, Session **48**, Compositor **7**, FFmpeg **7** vs Core 382 / Control 156 / HaPlay 445 — concentrated away from the new seams, as claimed. |
| NXT-15 | **Confirmed (medium)** | `next-build.yml` push trigger is `branches: [next]` (not `next-2`); `HaPlay.Desktop.csproj:10-11` `PublishAot=false` + `AssemblyName=HaPlay_Test`; no `HAPLAY_SMOKE` reference exists in source. |

## Corrections to the original review

- **NXT-07 — "nine policies":** `CueFaultPolicy` (`CueGraph.cs:6-16`) has **8** members (`StopShow, SkipCue, Continue, HoldLastFrame, FadeToBlackOrSilence, ContinueAudioOnly, ContinueVideoOnly, RouteToFallbackOutput`). The substance is unaffected — only `StopShow` has real behavior; the other 7 collapse to "continue."
- **NXT-02 — "a network media item would normally have two independent connections":** true for the **FFmpeg** provider, but the framework's own **NDI** provider already pairs `OpenVideo`/`OpenAudio` onto one receiver via `SharedNdiSourceCache` (`NDIModule.cs:50-53`). This does not weaken the finding — it strengthens it: because the registry has no atomic open, *each provider must hand-roll its own pairing*, and the default (FFmpeg) one doesn't. The split-open contract is the wrong abstraction exactly as stated; the phrasing just over-generalizes one provider's behavior to all of them.

## Strengthening notes

- **NXT-01 is a low-cost fix, and a concrete crash.** The borrow lease the recommendation asks for *already exists*: `ClipCompositionOutputLease.DisposeOutputOnRuntimeDispose` defaults to `false`, and `AttachCompositionOutputAsync` (`ShowSession.cs:951-954`) already uses it correctly ("The caller owns the output's lifetime"). The bug is a single wrong argument at `ShowSession.cs:190` plus a missing UI release callback. Reproduction with no new code:
  1. With `HAPLAY_USE_SHOWSESSION=1`, play file A in a media-player deck. The deck acquires line L's persistent `_sink` and the composition wraps it `DisposeOutputOnRuntimeDispose: true`.
  2. Play file B in the **same deck**. `TryOpenViaShowSessionAsync` releases L (resets the holder, keeps `_sink` alive) and re-acquires the **same** `_sink`, then calls `LoadDocument`.
  3. `LoadDocumentCoreAsync` retires the **A** composition → `_sink.Dispose()` (the real SDL window/GL context) — the very object the **B** composition just re-acquired.
  4. The B fire renders onto a disposed `_sink`. App shutdown hits the same path via `DisposeStateAsync` (`ShowSession.cs:1018-1020`).
- **NXT-03 is worse than stated in two ways.** (a) The fire path threads **no** cancellation token at all — `FireCueAsync` → `_cueGraph.FireAsync(cueId)` with `default` CT — so a long `PreWait` (`CueGraph.cs:216-217`) cannot be interrupted even in principle. (b) The real-time fade ramp (`StartFadeIn`, 25 ms steps) and end/loop/freeze detection (`StartEndMonitor`, 100 ms) are themselves implemented as polls that marshal each step back onto the same serial dispatcher (`ShowSession.cs:745`, `:797`); when the dispatcher is parked behind a long fire, audio fades and loop points stall with it.
- **NXT-07 — duplicate cue id is worse than "not rejected":** `_clipsByCue = document.Clips.ToDictionary(c => c.CueId, …)` (`ShowSession.cs:197`) **throws** on a duplicate clip `CueId`, so a malformed document fails the load with an opaque `ArgumentException` rather than a validation diagnostic — another argument for the `ShowDocumentValidator` in NXT-12.

## Additional findings

### NXT-16 — Medium: session queries are serialized on the command dispatcher

The D5 design (`SessionDispatcher.cs:7-11`) states that commands marshal onto the serial loop while "queries elsewhere read immutable snapshots." `ShowSession` does not honor this: every query — `SnapshotAsync` (`:922`), `GetCueDefinitionsAsync` (`:937`), `GetPreparedCueIdsAsync` (`:942`), `GetCueExecutionLogAsync` (`:958`), `GetCompositionStatsAsync` (`:963`), `IsVoicePlayingAsync` (`:544`) — is an `InvokeAsync` onto the **same** serial dispatcher as commands.

Consequence: this compounds NXT-03 onto reads. The media-player deck polls `SnapshotAsync` every 250 ms (`MediaPlayerViewModel.ShowSession.cs:155,172`) and the cue workspace polls similarly; while a long `FireAsync`/pre-wait/blocked open holds the loop, the position/duration/state readout and cue-list queries freeze too, so the UI cannot even display "stuck" state accurately. The fix is the one the design already promised: back queries with a lock-free immutable snapshot (e.g. a volatile published record per transport group updated on the dispatcher) instead of round-tripping each read through the command queue. Add a test that reads position while a minutes-long pre-wait is active and asserts the read returns promptly.

### NXT-17 — Low: `mfp_last_error` C-string lifetime is a use-after-free / leak

`mfp_last_error` (`NativeApi.cs:57-64`) frees the previous `s_lastErrorNative` and returns a freshly `StringToCoTaskMemUTF8`-allocated buffer. Two problems for a C consumer:

- The returned pointer is owned by the **next** `mfp_last_error` call on the same thread, which frees it. A caller that stashes the pointer and calls `mfp_last_error` again (or any wrapper that does) reads freed memory. The header should document "copy immediately; valid only until the next call on this thread," or the API should return a caller-allocated-buffer `(char* buf, size_t len)` form.
- The final per-thread allocation is never freed: `[ThreadStatic]` fields run no finalizer, and `mfp_shutdown` (`:52`) only flips `s_initialized` — it does not free `s_lastErrorNative`. Every thread that ever read an error leaks one buffer at thread/process exit.

Low severity (bounded, per-thread), but it belongs with the NXT-08 ABI-hardening work: the same no-throw, deterministic-ownership pass over the C surface should fix the error-string contract.

## Bottom line

The review is accurate and well-evidenced; I would action it as written. The release-blocker framing is correct — NXT-01 in particular is a reproducible use-after-dispose on the second play in a deck and on shutdown, not a stylistic concern, and its fix is small because the borrow-lease mechanism already exists. I'd fold NXT-16 into the NXT-03/Gate-1 work (queries and commands share the same starvation) and NXT-17 into the NXT-08/Gate-2 ABI hardening.

---

# Remediation pass (2026-06-30)

The well-scoped correctness/safety findings were fixed in `next/` on branch `next-2`. The large architectural items (NXT-02/03 *full* redesigns, NXT-06/09/10, and the remainders of NXT-04/05/11) are deferred — they are the review's own multi-week "gates" and involve product/design decisions; see *Deferred* below.

After the changes: `dotnet build next/MFPlayer.Next.sln` → **0 errors**; the outbound C smoke including the new negative-handle gate **passes**; `SessionSmoke` plays a real show headless (audio + seek + a clock-mastered video composition + subtitles + loop + fade, exit 0); `S.Media.Core.Tests` **437/437** (+1), `S.Media.Session.Tests` **58/58** (was 48, +10 new regression tests), `S.Media.Players.Tests` 4/4, `S.Media.Decode.FFmpeg.Tests` 15/15, `S.Media.Compositor.Tests` 12/12, `HaPlay.Tests` 472/472. (Test infra: added a `MFP_PORTAUDIO_HOST_API` knob so PortAudio-touching tests/smokes run under JACK — `MFP_PORTAUDIO_HOST_API=JACK pw-jack dotnet test …` — instead of the dev box's broken ALSA config, which otherwise flakes one HaPlay audio test and floods stderr.) (One HaPlay run showed a transient single failure that did **not** reproduce — the dev box's ALSA/PortAudio device enumeration intermittently crashes the test host, unrelated to these changes.)

## Fixed

| ID | What changed | Key files | Regression tests added |
|---|---|---|---|
| NXT-01 | The host video-output factory now returns `ClipCompositionOutputLease` so the **host** declares ownership; HaPlay's player + cue paths return borrowed lines with `DisposeOutputOnRuntimeDispose: false`. The session no longer disposes borrowed SDL/NDI outputs on reload/shutdown. | `ShowSession.cs` (factory type + load), `MediaPlayerViewModel.ShowSession.cs`, `MainViewModel.cs` | `BorrowedVideoOutput_NotDisposed_OnReloadOrDispose`, `SessionOwnedVideoOutput_Disposed_OnDispose` |
| NXT-08 | Raw `GCHandle.FromIntPtr` replaced with a synchronized monotonic-id **handle table** (lookup never dereferences the caller token); every export wrapped in a no-throw boundary; `mfp_shutdown` now destroys live sessions deterministically; `SessionDestroy` is idempotent. | `NativeApi.cs` | C smoke negative-handle gate in `SmpSmoke/smoke.c` (garbage/null/double-free/post-shutdown) |
| NXT-17 | `mfp_last_error` lifetime documented (valid until next call on the thread) and freed on shutdown via `FreeLastErrorNative`. | `NativeApi.cs` | (covered by the C smoke) |
| NXT-07 | GO now filters **armed && enabled** and advances `LastFiredNumber` only after the cue actually ran/faulted; fade-in ramps to each route's **configured gain** (not a hard 1.0); a defensive **cycle guard** stops cyclic auto-continue; `CueFaultPolicy` documented honestly (only `StopShow`/`Continue` implemented). | `ShowSession.cs` (GoAsync, StartFadeIn), `CueGraph.cs` | `Go_SkipsDisabledCue_AndFiresNextEnabled`, `FireAsync_CyclicAutoContinue_TerminatesInsteadOfRecursingForever` |
| NXT-04 (partial) | Session compositions are now **clock-mastered to their transport group** instead of free-running: `ShowSession` wires the composition pump to the group's `SessionClock` (stable across cues — it re-references the active clip's playhead) with the clip playhead as the frame-selection timeline, so video follows the clip clock. Verified end-to-end (`SessionSmoke`: a real clip composites frames + loop + fade + subtitles on the mastered path, exit 0). Live sources keep the free-run path. (The full per-group timeline/discontinuity contract across audio/live/outputs, the multi-source start barrier, and measured-skew gates remain deferred.) | `ShowSession.cs` | `CompositionBoundClip_ClockMastersTheComposition_NotFreeRunning` |
| NXT-12 | New `ShowDocumentValidator` (version, unique ids/numbers, single clip per cue, resolvable refs, **acyclic auto-continue**, positive dims/rate); `LoadDocumentCoreAsync` now **validates → stages → atomically swaps**, so a bad document or factory failure can no longer destroy the running show. | `ShowDocumentValidator.cs` (new), `ShowSession.cs` | `Validator_RejectsBadVersionDuplicatesDanglingAndCycles`, `LoadDocument_InvalidDocument_Throws_AndLeavesRunningShowIntact` |
| NXT-16 | Added a lock-free `Snapshot()` backed by a volatile per-group clock/player view republished on the dispatcher; `SnapshotAsync` no longer marshals, so a UI position/state poll never queues behind a long command. | `ShowSession.cs` | `Snapshot_LockFree_ReflectsFireAndStop_WithoutMarshaling` |
| NXT-03 (partial) | STOP/StopCue/LOAD/DISPOSE now cancel the in-flight cue fire off-dispatcher via a published `_activeFireCts`, so a long pre-wait/open can no longer park the serial loop and block them — a cancelled fire returns `Failed` without advancing the GO cursor. (Full off-dispatcher execution of *all* cue work + operation generations is deferred; an uninterruptible synchronous native open still runs to completion.) | `ShowSession.cs` (FireCueAsync, GoAsync, Stop/StopCue/Load/Dispose) | `Stop_PreemptsLongPreWait_InsteadOfQueuingBehindIt` |
| NXT-05 (partial) | The registry now **owns** module-acquired native runtimes: `IMediaRegistryBuilder.AddLifetime` + `MediaRegistry : IDisposable` (releases lifetimes reverse-order, idempotent). PortAudio/NDI modules register their runtime release; the C-ABI session disposes its per-session registry on destroy/shutdown, so create/destroy churn no longer leaks PortAudio/NDI refs (`Pa_Terminate` now actually runs). (The full `MediaHost` owning *all* registries + plugin leases is still deferred.) | `IMediaRegistryBuilder.cs`, `MediaRegistry.cs`, `PortAudioModule.cs`, `NDIModule.cs`, `NativeApi.cs` | `Dispose_ReleasesRegisteredLifetimes_InReverseOrder_AndIsIdempotent` |
| NXT-11 (partial) | `ShowSession` now takes an **injectable compositor factory** (defaults to CPU; a host with a GL context can supply a GPU/warp compositor — the review's explicit ask). The two per-frame allocations are gone: `ClipCompositionRuntime` reads lock-free volatile snapshots of its acquired outputs and subtitle feeds instead of `ToList()`/`ToArray()` every composition tick. (Subtitle-overlay render caching, `CompositeWithSurfaces` CPU-readback removal, and the host GPU wiring + benchmark gates remain deferred.) | `ShowSession.cs`, `ClipCompositionRuntime.cs` | `CompositorFactory_IsConsulted_PerComposition_AtLoad` |
| NXT-15 | CI push trigger widened from `[next]` to `['next', 'next-*']` so the gate runs on the working branch. | `.github/workflows/next-build.yml` | — |
| NXT-02 (partial) | `MediaPlayer.TryOpen` now **retains** the real video/audio open exceptions and surfaces them when both sides fail, instead of the generic "no decoder" message. (The full atomic single-demux open is deferred.) | `MediaPlayer.cs` | — |

## Deferred (architectural gates — need design/product decisions)

These remain open by design; each is a substantial effort the review itself sequences behind gates:

- **NXT-02 (full)** — one atomic async `MediaAsset` open sharing a single demux context. Only the error-hiding sub-issue was fixed here.
- **NXT-03 (remainder)** — STOP/LOAD/DISPOSE preemption of an in-flight pre-wait/open is **done** (see Fixed). What remains: executing *all* cue work (open/monitor/fade) off the dispatcher with explicit operation generations, and preempting an uninterruptible *synchronous* native open (which still runs to completion). NXT-16 already fixed the query half.
- **NXT-06 / NXT-09 / NXT-10** — the strangler cutover + engine deletion, the plugin-host feature set, and first-class GPU surface layers. These are the Gate 3–4 work.
- **NXT-04 (remainder)** — session compositions now follow the group clock (see Fixed). What remains: the full per-group timeline/discontinuity contract threaded through every audio renderer / live source / output presentation, the multi-source start barrier (shared start epoch), and toleranced A/V + output-skew tests over seek/pause/loop on real hardware.
- **NXT-05 (remainder)** — native-runtime release is now wired through a disposable registry (see Fixed). What remains: the single `MediaHost : IAsyncDisposable` owning *all* registries (compositor/control/subtitle/output) and reference-counted native plugin leases with leaked-object reporting in debug/test builds.
- **NXT-11 (remainder)** — the injectable compositor seam + per-frame allocations are done (see Fixed). What remains: actually wiring a GPU compositor in the host, subtitle-overlay render caching (skip the full-canvas BGRA rebuild when the event is unchanged), removing the `CompositeWithSurfaces` CPU readback, and the perf/allocation benchmark gates.
- **NXT-13 / NXT-14** — empty-project removal and the broader test build-out beyond the targeted regressions added above.
