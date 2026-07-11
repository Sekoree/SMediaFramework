# MFPlayer Code Review — 2026-07-11

> **Fix pass applied same day** — see [Fix log](#fix-log-2026-07-11) at the end for what was
> changed, what was deliberately deferred, and test results. Per-finding status tags: ✅ fixed,
> ⏸ deferred, 📝 noted (no change intended).

Scope: all first-party projects — `MediaFramework/` (Media, Interop, Control, Audio, MIDI, NDI, OSC, Subtitles libs) and `UI/HaPlay` + `UI/HaPlay.Desktop`. Excluded: `External/`, `Reference/`, generated/vendored code, `Tools/` smoke programs (skimmed only).

Method: full reads of the hot-path files (clocks, routers, pumps, players, PortAudio callback, compositor source, subtitle layer, FFmpeg demux/convert paths), targeted reads of the largest remaining files, and repo-wide pattern sweeps (async void, sync-over-async, per-frame allocations, `DateTime.Now`, timer/event hygiene, dispose patterns). HaPlay (~44k LOC) was sampled at its hot paths and timers rather than read exhaustively.

---

## Overall assessment

This is an unusually disciplined codebase. Things that are normally review findings are already handled systematically:

- **Real-time-audio safety**: the PortAudio callback (`S.Media.Audio.PortAudio/PortAudioOutput.cs:566`) is lock-free, allocation-free, and never throws across the native boundary.
- **Allocation discipline**: `ArrayPool` with idempotent releases in every decode path; per-output pump buffers recycled through free-pools with zero-copy `Commit`; compositor read path reuses scratch lists; `VideoFrame` fan-out shares backings with refcounted release.
- **Shutdown robustness**: bounded joins everywhere, with explicit "stuck" terminal states that deliberately leak rather than use-after-dispose (`OutputPump.Dispose`, `VideoOutputPump.Dispose`, `VideoPlayer.StopInternal`).
- **Concurrency structure**: immutable `RouterState` snapshots, phase-split video submit that converts outside the router gate with converter leasing, format-versioned pump queues to drop stale frames.
- **UI hygiene**: all `async void` handlers are event/timer handlers with try/catch; timers are single-flight guarded; `DateTime.Now` appears only in log-file naming.

No high-severity correctness bugs were found in the playback path. The findings below are: one test-integrity bug, two behavioral warts, and a set of consolidation/allocation opportunities.

Severity legend: **H** = should fix, **M** = worth fixing, **L** = nice-to-have, **T** = trivial.

---

## 1. Correctness / behavior

### 1.1 (H) ✅ HaPlay.Tests: `Dispatch(async () => …)` never awaits the body — assertions cannot fail the test

Files: `UI/HaPlay.Tests/SessionRecoveryTests.cs:274,306`, `ProjectDirtyTrackingTests.cs:58,80,140`, and every other use of `HeadlessUnitTestSession.Dispatch(async () => { … })`.

Avalonia's `HeadlessUnitTestSession` has `Dispatch(Action, ct)`, `Dispatch<TResult>(Func<TResult>, ct)`, and `Dispatch<TResult>(Func<Task<TResult>>, ct)`. An `async () => { … }` lambda with no result is a `Func<Task>`, which binds to `Func<TResult>` with `TResult = Task` — the session runs the lambda to its first await and returns the **inner task un-awaited**. `await session.Dispatch(...)` awaits only the outer dispatch. Consequences:

- Assertions after the first `await` inside the lambda run (or fail) after the test has already passed.
- Exceptions from the body are unobserved.

Fix: add one shared helper and mechanically switch call sites:

```csharp
public static async Task DispatchAsync(this HeadlessUnitTestSession s, Func<Task> body, CancellationToken ct)
    => await await s.Dispatch(body, ct); // outer = dispatched, inner = the async body
```

(Or use the `Func<Task<TResult>>` overload by returning a dummy value.) Then audit which of those tests actually still pass — some may have been green vacuously.

### 1.2 (M) ✅ `PauseFlushPolicy` is silently a no-op

File: `MediaFramework/Media/S.Media.Players/MediaPlayer.cs:370`

```csharp
private static Action? ResolveFlushAction(PauseFlushPolicy policy) => null;
```

`MediaPlayer.Pause(...)` and `SeekCoordinated(...)` accept a `PauseFlushPolicy`, and `S.Media.Session` call sites deliberately pass `SkipFlush` (`ShowSession.cs:1211,1449,1660,1682`, `ClipStandbyEngine.cs:422`) — but both enum values produce exactly the same behavior (no flush). The enum documents `FlushCodecPipelines` as "run the default flush hook", which never happens on this open path.

Either wire the flush action for the registry/live path, or delete the parameter and the enum (keeping `PauseWithFlushAction` as the explicit escape hatch). As-is it misleads both callers and readers into believing a codec flush occurs by default.

### 1.3 (M) ✅/📝 Intentional pump flushes latch a permanent adaptive-rate bias

Chain:

1. `AudioRouter.OutputPump.AbandonQueue()` (`AudioRouter.OutputPump.cs:209-220`) runs on every Pause / `FlushOutputBuffers` / `RemoveOutput` / stop, and calls `RecordDrop()` **per abandoned chunk**, which raises `AudioRouter.PumpPressure`.
2. `PumpPressurePlaybackHintMonitor.ApplyObservation` (`S.Media.Routing/Audio/PumpPressurePlaybackHintMonitor.cs:112-125`) converts that burst (e.g. 8 chunks in a few ms) into a large drops-per-second figure → hint clamps to the max (−40 ppm).
3. The hint **never decays**: when `delta <= 0` the method returns leaving `_hintPpm` unchanged, so the −40 ppm bias persists until some future drop produces a different value.
4. `AdaptiveRateAudioOutput.Submit` (`S.Media.Decode.FFmpeg/Audio/AdaptiveRateAudioOutput.cs:84-95`) then permanently resamples the non-master output slightly slow (clamped to ±3 Hz).

So a single user pause biases every adaptive-rate-wrapped output (NDI, record legs) for the rest of the session, for a "pressure" event that was intentional. Two independent fixes, both worth doing:

- In `AbandonQueue`, count abandoned chunks separately (an `_abandoned` counter) instead of `RecordDrop()` — pump pressure should mean "output can't keep up", not "host flushed". The `WaitForIdle` accounting already has the `_processed` increment it needs.
- Give the monitor a decay path: when an observation window passes with `delta == 0`, ease `_hintPpm` back toward 0 (or reset after N quiet seconds).

### 1.4 (L) ✅ Orphaned XML documentation on `AddOutput` in both routers

- `MediaFramework/Media/S.Media.Routing/Audio/AudioRouter.cs:289-327`: the `<param name="pumpCapacityChunks">` doc plus its long latency-budget `<remarks>` are attached to `EnableAdaptiveRateOnNonMasterOutputs`, not to `AddOutput`. `AddOutput` itself (line 352) has no doc comment.
- `MediaFramework/Media/S.Media.Routing/Video/VideoRouter.cs:91-117`: `AddOutput`'s summary and `asyncPump`/`synchronous` param docs sit on `GetRegisteredOutputIds` (which ends up with two `<summary>` blocks); `AddOutput` has none.

Looks like a refactor inserted members between doc and target. The content is valuable — reattach it (the compiler flags none of this because the affected members simply gain bogus params docs).

### 1.5 (L) ✅ `ILocalVideoPreviewRuntime.ApplyHoldImageWindowSize` is a no-op in both implementations

`UI/HaPlay/OutputPreview/LocalVideoPreviewRuntime.cs:209-214,465-471` — both runtimes explicitly discard the arguments. If the "keep windowed preview dimensions stable" policy is now permanent, remove the method from the interface and its call sites; a two-implementation interface where every implementation ignores the call is a trap for the next output engine.

---

## 2. Performance / allocations

The hot paths are already excellent. These are the remaining opportunities, roughly ordered by value.

### 2.1 (M) ✅ `ChannelMap` fast-path dispatch: classify once, not per chunk — and the two dispatch chains have drifted

- `ChannelMap.ApplyAdditive` (`S.Media.Core/Audio/ChannelMap.cs:134-234`) probes up to **19** `TryAccumulate*` fast paths on every call; each probe re-derives the map shape (`AsSpan`, length/index checks). The map is immutable after construction.
- `AudioRouter.ApplyRoute` (`S.Media.Routing/Audio/AudioRouter.cs:1609-1728`) duplicates the same chain for the uniform-gain case — but with only **13** probes, missing six that `ApplyAdditive` has (`WideSourceStereoConsecutivePair`, `WideSourceSingleChannelDupStereo`, `StereoToMonoSingleOutput`, `WideSourceMonoSingleOutput`, `StereoDuplexGrouped`, `StereoDuplexGroupedSwapped`).

Consequences: (a) a route at uniform gain ≠ 1.0 with one of those six map shapes silently falls back to the scalar loop, while the identical route at gain 1.0 gets SIMD via `ApplyAdditive` — a surprising, shape-dependent perf cliff; (b) every new fast path must be added in two places in the right order, and history shows that already hasn't happened.

Suggested shape: classify the map **once in the `ChannelMap` constructor** into a `FastPathKind` enum (+ any precomputed params like the packed-permutation indices), and make both `ApplyAdditive` and `ApplyRoute` a single `switch` on it with a `uniformGain` parameter. This removes the probe chain from the per-chunk path (routers run 100 chunks/s × routes), guarantees the two call sites can't drift, and makes the scalar fallback conditions explicit.

### 2.2 (M) ✅(audio)/⏸(video) `MediaContainerSharedDemux` duplicates the standalone decoders' conversion code

- Audio: `MediaContainerSharedDemux.cs:1785-1885` (`ConvertAudioFrame`, `TryDrainAudioTailFrame`) is near-verbatim `AudioFileDecoder.cs:479-577`.
- Video: the plane-pack / pooled-buffer emit code at `MediaContainerSharedDemux.cs:2319-2444` mirrors `VideoFileDecoder.cs:808-936`.

These are exactly the functions where subtle bugs get fixed (swr rebuild on mid-file format change, idempotent pool returns, stride-safe row copies) — every fix currently has to land twice. Extract shared internal helpers (e.g. `SwrFrameConverter`, `VideoPlaneEmitter`) taking the codec ctx/frame + format state; both the shared demux and the standalone decoders call them. This is the highest-value simplification in the repo (~300–400 duplicated lines in the most delicate code).

### 2.3 (L) ✅ `VideoFrame` CPU fan-out: two ~50-line near-identical bodies

`S.Media.Core/Video/VideoFrame.cs:233-283` (`TryCreateNv12CpuFanOutViews`) vs `:311-361` (`TryCreateCpuFanOutViews`) differ only in the eligibility predicate. Make the NV12 variant delegate:

```csharp
public static bool TryCreateNv12CpuFanOutViews(...) =>
    IsNv12Shape(source) && TryCreateCpuFanOutViews(source, viewCount, hint, out views);
```

(keeping the stricter NV12 plane/stride checks in the predicate). The rollback-on-exception logic is the risky part to have twice.

### 2.4 (L) ✅ `TryUpdateThrottle` is copy-pasted 8×

Identical `private static bool TryUpdateThrottle(ref long, TimeSpan)` in: `AudioFileDecoder.cs:399`, `VideoFileDecoder.cs:537`, `NDIAudioOutput.cs:177`, `NDIVideoSender.cs:577`, `VideoPlayer.cs:1041`, `AudioRouter.OutputPump.cs:305`, `VideoOutputPump.cs:566`, `VideoRouter.cs:520`. Move it into `MediaDiagnostics` (Core is referenced by all eight projects) as e.g. `MediaDiagnostics.TryEnterThrottledLog(ref long slot, TimeSpan interval)`.

### 2.5 (L) ⏸ GL compositor multi-warp path allocates small arrays per composite

`S.Media.Compositor/OpenGL/GlVideoCompositor.cs:626, 664-666, 763` — `new VideoFrame[outputs.Count]`, `new PboReadback[outputs.Count]`, and `SnapshotOutputRequests` allocate per read at output cadence (60 Hz). The single-output path and `VideoCompositorSource` already use reusable scratch; cache these arrays keyed on `outputs.Count` the same way (the read path is single-consumer, so a plain field is enough).

### 2.6 (L) ⏸ Per-frame jagged-array wrappers in the CPU convert/emit paths

`VideoCpuFrameConverter.cs:128` and `VideoFileDecoder.cs:880` (and the shared-demux twin) allocate `new byte[nDst][]` + a `ReadOnlyMemory<byte>[]` + `int[]` strides per converted frame. The plane data itself is pooled; only these small holder arrays churn (~3 small objects per frame, 60/s per converting branch). Converters are serialized (`_submitLock` / pump drain thread), so the holder arrays can be cached fields resized on reconfigure. Worth it only on the branch-convert path (NDI at high fps); skip if you value the current code's simplicity.

### 2.7 (T) ✅(partial) Micro-notes

- `S.Control/ControlEventQueue.cs:158-173`: `CoalesceKeyFor` builds an interpolated string per control event (including the non-full-queue common case). A readonly record struct key (`(kind, node, ch, controller)`) in the `_coalesceIndex` dictionary would remove per-event string churn under MIDI floods. Low priority — events are human-scale most of the time.
- `UI/HaPlay/ViewModels/MediaPlayerViewModel.cs:1265-1279`: `PollAudioMeters` snapshots `_meterTaps.ToArray()` per 250 ms tick; a reusable `List<>` copy would be allocation-free. Negligible.
- `UI/HaPlay/OutputPreview/LocalVideoPreviewRuntime.cs:66-72`: `CreateBlackBgra` writes `0` into three already-zeroed bytes per pixel; only the alpha byte needs setting (`for (i = 3; i < len; i += 4) bytes[i] = 255;`).

---

## 3. Dead / vestigial code — ✅ (except `PrewarmVideoAfterSeek`, kept: public API)

- (T) `VideoOutputPump._firstSubmitLogged` (`S.Media.Routing/Video/VideoOutputPump.cs:68,422`): written via `Interlocked.Exchange`, never read — the first-submit log is already gated by `n == 1`. Delete the field.
- (T) `VideoPlayer._slotsAvailable` (`S.Media.Players/VideoPlayer.cs:56`) is never reassigned — mark `readonly` (documents the "never dispose/recreate mid-flight" invariant the `DrainQueue` comment relies on).
- (T) `MediaPlayer.GetMetrics` (`MediaPlayer.cs:188-190`): `videoSnap` is assigned `null` then unconditionally reassigned; `MediaPlayer.PrewarmVideoAfterSeek` (`:365-368`) is an empty public method kept only for API shape — worth an `[Obsolete]` or removal if no external callers.

---

## 4. Notes & observations (no action required)

- **`AudioRouter` natural-EOF ignores unrouted sources** (`AudioRouter.cs:1452-1474`): `CompletedNaturally` fires when all *routed* sources are exhausted, even if an unrouted, unexhausted source is registered. The comment documents this as intentional (cue/soundboard preload); flagged only so it isn't "fixed" accidentally in either direction.
- **`MediaClock.Start` holds `_gate` while starting the driver thread** (`MediaClock.cs:150`): the driver immediately contends briefly on `CurrentPosition` — harmless, just noted.
- **`VideoPlayer.HasFrameWithinLeadOf`** enumerates a `ConcurrentQueue` (snapshot + enumerator alloc) — only called from the pre-audio sync gate, not per tick. Fine.
- **`ShowSession.LoadDocument` sync-over-async** (`ShowSession.cs:365`) and the recovery service's `GetAwaiter().GetResult()` on the finalize path (`SessionRecoveryService.cs:413-451`) are deliberate, documented, and off the UI-thread/deadlock-prone paths I could trace. The `MediaPlayer.TryOpen` sync open (`MediaPlayer.cs:473`) documents the same tradeoff inline.
- **HaPlay VM size**: `ControlWorkspaceViewModel.cs` is a single 3,133-line file. `MediaPlayerViewModel` is already split into partials (`.ShowSession`, `.Transport`, `.Playlist`); the control workspace would benefit from the same treatment (monitor, scripts, devices, layers are natural seams). Organizational only.
- **Logs/TestResults** under the repo tree are correctly untracked (verified with `git ls-files`).

---

## 5. What's notably good (patterns worth keeping consistent)

Kept short deliberately — these set the bar the findings above are measured against:

1. **Pump pattern** (audio `OutputPump`, `VideoOutputPump`): bounded queue + drop-oldest + backpressure only on the clock-master leg, with drop accounting that keeps `WaitForIdle` from wedging (`_readyEvictions`). The format-versioned video queue closing the reconfigure race is a standout.
2. **Phase-split `VideoRouter.SubmitPhased`** (`VideoRouter.cs:797-968`): snapshot under lock → convert outside → revalidate and deliver under lock, with converter leasing (`_convertInFlight` + deferred dispose) and a version-stamped submit-plan cache. Textbook.
3. **Terminal-stuck semantics**: routers/players refuse restart after a join-cap breach instead of double-starting decode threads over shared native state.
4. **`ChannelMap` SIMD coverage** with exhaustive shape docs and a profiling switch (`MF_MEDIA_PROFILE_CHANNEL_MAP`).
5. **Degrade-instead-of-fail opens** in the shared demux (unusable audio → video-only and vice versa) with the failure reason surfaced as a warning.

---

## Suggested fix order

| # | Finding | Effort |
|---|---------|--------|
| 1 | 1.1 test `Dispatch` helper + audit affected tests | small, high value |
| 2 | 1.3 abandon-vs-drop accounting + hint decay | small |
| 3 | 1.2 implement or delete `PauseFlushPolicy` | small |
| 4 | 2.1 `ChannelMap` classify-once dispatch | medium |
| 5 | 2.2 extract shared demux/decoder conversion helpers | medium |
| 6 | 2.3–2.6, section 3, 1.4–1.5 cleanups | small, batchable |

---

## Fix log (2026-07-11)

Applied the same day as the review; full solution builds with **0 warnings / 0 errors** and all 19
test projects pass (1,735 passed, 0 failed, 9 skipped — including HaPlay.Tests' 630).

| Finding | Status | What was done |
|---|---|---|
| 1.1 test `Dispatch(async)` | ✅ | New `UI/HaPlay.Tests/HeadlessDispatchExtensions.cs` with `DispatchAsync`; the 9 direct `.Dispatch(async …)` sites and the 3 `DispatchUi(Func<Task>)` helpers now go through it. **Important subtlety:** the naive `await await session.Dispatch(body)` deadlocks — after the lambda's first await the headless session stops pumping its dispatcher, so the body's continuation never runs (verified: the first full-suite run hung exactly this way). The helper therefore routes through the `Func<Task<TResult>>` overload, which pumps until the inner task completes. All previously-vacuous bodies genuinely pass when awaited. |
| 1.2 `PauseFlushPolicy` | ✅ removed | Enum + `flushPolicy` parameters + `ResolveFlushAction` deleted; `ShowSession` (4 sites) and `ClipStandbyEngine` updated. `PauseWithFlushAction` remains the explicit escape hatch. |
| 1.3 abandon-vs-drop | ✅ / 📝 | `OutputPump.AbandonQueue` now counts into a new `_abandoned` counter (exposed as `OutputPumpStats.Abandoned`) and no longer raises `PumpPressure` — a pause/flush/remove can no longer latch an adaptive-rate bias. The monitor **decay** half was deliberately *not* added: `PumpPressurePlaybackHintMonitorTests.ApplyObservation_NoNewDrops_KeepsPriorHint` pins keep-prior-hint as intended behavior for genuine drops, and with the spurious trigger removed the persistent bias is arguably the design (counteract steady drift). Revisit only if a transient real stall latching bias proves to be a problem. |
| 1.4 orphaned XML docs | ✅ | `AddOutput` docs reattached in both routers; `AudioRouter.AddOutput` gained a summary. |
| 1.5 `ApplyHoldImageWindowSize` | ✅ removed | Dead end-to-end (the VM forwarder had no callers): interface member, both no-op impls, and the VM method deleted. |
| 2.1 `ChannelMap` dispatch | ✅ | New `ChannelMap.TryAccumulateAnyInterleaved` holds the single ordered 19-probe chain. `ApplyAdditive` calls it; `AudioRouter.ApplyRoute` delegates unity gain to `ApplyAdditive` (one probe pass instead of 13 + 19) and probes the full chain once for other uniform gains — the six previously-missing shapes now get SIMD at any gain. All six were verified to honor `uniformGain`. Profiling-bucket semantics preserved (`ChannelRouteMixProfilingTests` unchanged, green). The full classify-once-at-construction refactor was consciously skipped (chosen option: shared chain). |
| 2.2 demux/decoder dedup | ✅ audio / ⏸ video | New `S.Media.Decode.FFmpeg/Audio/SwrPooledAudioFrames.cs` (rent → swr_convert → idempotent pooled release); `AudioFileDecoder.ConvertFrame`/`TryDrainTailFrame` and the shared demux's `ConvertAudioFrame`/`TryDrainAudioTailFrame` are now thin wrappers doing only their own PTS/position bookkeeping (~150 duplicated lines removed). The **video plane-emit dedup is deferred** to a dedicated session per review decision. |
| 2.3 NV12 fan-out | ✅ | `TryCreateNv12CpuFanOutViews` is now an NV12 shape gate delegating to `TryCreateCpuFanOutViews`; one copy of the release-swap/countdown rollback logic. |
| 2.4 `TryUpdateThrottle` ×8 | ✅ | Single `MediaDiagnostics.TryUpdateThrottle`; all 8 private copies deleted, call sites qualified. |
| 2.5 GL multi-warp arrays | ⏸ | Deferred — small per-composite arrays in complex GL code; low value vs. regression surface. |
| 2.6 convert-path holder arrays | ⏸ | Deferred with 2.2's video half (same code region). |
| 2.7 micro-notes | ✅ partial | `CreateBlackBgra` writes alpha only; `PollAudioMeters` uses a reusable scratch list. The `ControlEventQueue` coalesce-key struct was deferred (low priority per review). |
| §3 dead code | ✅ | `_firstSubmitLogged` removed; `VideoPlayer._slotsAvailable` is `readonly` with the invariant documented; `GetMetrics` null-then-assign cleaned. `PrewarmVideoAfterSeek` kept (public API). |

Deferred items for a future pass: video-side demux/decoder plane-emit dedup (2.2), GL multi-warp
array caching (2.5/2.6), `ControlEventQueue` struct coalesce keys (2.7), optional hint-monitor decay
(1.3), `ControlWorkspaceViewModel` partial split (§4).
