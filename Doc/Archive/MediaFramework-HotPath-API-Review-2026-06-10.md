# MediaFramework hot-path / reliability / API review — 2026-06-10 (evening pass)

Scope of this pass (complements the morning review in
`Doc/Archive/MediaFramework-Review-Findings-2026-06-10.md`, whose P1/P2 items are already fixed):

1. **Allocations & perf on hot paths** (audio run loop, video submit/fan-out, FFmpeg convert, NDI, PortAudio)
2. **Reliability under live audio/video re-routing** (the framework's flagship flexibility)
3. **End-user API simplification**

Everything below was verified against the tree as of 2026-06-10 evening, with empirical runs on the
real test corpus (4K60 ProRes, 1080p60 ProRes+alpha, Bluray MKV 5.1, FLAC/WAV/MP3). The probe
harness lives at `/tmp/mfbench` (throwaway; recreate from §2 notes if needed).

Legend: **P1** fix soon · **P2** should fix · **P3** opportunistic.

---

## 1. Headline: the hot paths are already allocation-clean (measured)

Steady-state managed allocation while decoding + routing into discard outputs
(`MediaPlayer.OpenFile(...).OpenAsync()`, hidden discard video primary, one audio route):

| File | Path exercised | Alloc rate | GC collections (12 s) |
|---|---|---|---|
| おねがいダーリン_0611.mov (ProRes 4K60) | video pass-through + audio | **26.8 KiB/s** | gen0/1/2 = **0/0/0** |
| hanacon_IA_fix.mov (ProRes 1080p60 + alpha) | alpha pipeline | 29.5 KiB/s | 0/0/0 |
| THE IDOLM@STER MOVIE.mkv (Bluray, 5.1) | multi-stream demux | 13.4 KiB/s | 0/0/0 |
| FLAC w/ cover art | audio + attached-pic hold | 2.5 KiB/s | 0/0/0 |
| MP3 (audio only) | audio | 3.2 KiB/s | 0/0/0 |

Decode kept full rate throughout (e.g. 738 frames in 12.3 s at 4K60, `droppedLate=0`).
A 50-cycle live add/route/remove churn (audio + video outputs, while playing 4K60) ran with
**zero faults, zero audio drops, zero video stalls** and ~22 KiB allocated per add/remove cycle
(control-plane only). Conclusion: **no general "reduce allocations" project is warranted** — the
engine's per-chunk/per-frame paths (router scratch buffers, pump free-pools, `ArrayPool` plane
backings, swr/sws into pinned memory) already hit the zero-GC target. The remaining items are
*surgical*, listed in §3.

Verified-good specifics (do not "fix"):

- `AudioRouter.RunLoop`: per-chunk work is `ReadInto` into reused scratch + `Array.Clear` +
  `ApplyRoute` (SIMD fast paths) + zero-copy pump `Commit` with primary-output backpressure.
- `OutputPump`: pooled chunk rotation; drop-oldest only on non-primary; no allocation on drop.
- `MediaContainerSharedDemux.ReadInto` (audio): swr converts straight into the caller span.
- Video CPU frames: plane buffers are `ArrayPool`-rented and returned by the frame release;
  fan-out uses refcounted zero-copy views (`VideoFrame.TryCreateCpuFanOutViews`).
- NDI receive: held buffers grow-and-pin once, amortized.

---

## 2. Reliability under live re-routing

### R1 (P1) — `VideoRouter.TryAddRoute` failure poisons the input and kills presentation

**Repro (measured):** add a branch output whose `AcceptedPixelFormats` is empty (e.g. a stock
`DiscardingVideoOutput`) to a `MediaPlayer` playing ProRes (`Yuv422P10Le`), then `TryAddRoute`:

1. `TryAddRoute` **throws** `InvalidOperationException` ("video fan-out: no swscale path from
   Yuv422P10Le to any branch format []") instead of returning `false` + `errorMessage` —
   `Try*` contract violation. Source: `VideoRouter.TryAddRoute` (VideoRouter.cs:239) calls
   `ReconfigureInputIfNeededLocked` → `ApplyConfigureLocked` →
   `VideoOutputFanoutFormats.PickBranchPixelFormat` (VideoOutputFanoutFormats.cs:68 throws).
2. The route bookkeeping (`_outputOwner[outputId]`, `reg.RoutedOutputIds`, `reg.RoutedSet`) is
   mutated **before** the reconfigure (VideoRouter.cs:274–276), so the throw leaves a
   **half-added route** behind.
3. Consequence: **every subsequent frame submit on that input fails** — in the measured run,
   `displayed` froze at 31 (the pre-failure count) while decode continued at full rate. One bad
   route-add takes down the healthy primary output until the route is manually removed.

**Fix plan:**
- Make `TryAddRoute` transactional: snapshot `(RoutedOutputIds, RoutedSet, _outputOwner[outputId])`
  before mutation; wrap `ReconfigureInputIfNeededLocked` in try/catch; on failure restore the
  snapshot, set `errorMessage`, return `false`. Same treatment for any other mutator that calls
  reconfigure after bookkeeping (`AddOutput`-with-immediate-route paths, `RemoveOutput` +
  `PromoteNextPrimary` if applicable).
- Regression tests: (a) failed branch negotiation → `false` + message, graph unchanged, primary
  still presenting; (b) subsequent valid `TryAddRoute` succeeds; (c) churn test with a mix of
  good/bad outputs stays healthy.

### R2 (P1, pairs with R1) — empty `AcceptedPixelFormats` is "permissive" as primary but "rejects everything" as branch

`DiscardingVideoOutput` documents empty `AcceptedPixelFormats` as *"negotiates like a permissive
display (empty → first native source format)"* and the primary negotiation honors that. The branch
fan-out picker (`PickBranchPixelFormat`) instead iterates the empty list and throws. Any
"accepts anything" output (discard, recorders, debug taps) therefore works as primary and fails as
branch — surprising and undocumented.

**Fix plan:** in `PickBranchPixelFormat`, treat an empty `branchAccepted` as "pass-through the
negotiated source format" (return `src`). Document the convention on `IVideoOutput.AcceptedPixelFormats`.
Test: discard output as second route on a 10-bit source receives `Yuv422P10Le` pass-through frames.

### R3 (P3) — churn-probe leftovers worth keeping

The 50-cycle churn probe (add/route/wait 20 ms/remove, audio + video, while playing) is a good
permanent regression: consider porting it into `S.Media.Playback.Tests` as
`LiveReRoutingChurn_NoFaultsNoDrops` with a mock clocked output instead of wall-clock waits.

---

## 3. Surgical hot-path optimizations (P2/P3 — all optional, ordered by value)

### H1 (P2) — Cache `VideoRouter.SubmitPlan` per route-version

`InputRegistration.SubmitPhased` (VideoRouter.cs:681) calls `BuildSubmitPlanLocked` **per frame**
whenever an input has ≥ 2 outputs: `RoutedOutputIds.ToArray()` + `new IVideoCpuFrameConverter?[n-1]`
+ `new bool[n-1]`, plus the `new VideoFrame?[n-1]` branch array in the caller. The plan only changes
when routes/converters change. Add a `_routesVersion` counter bumped by every route mutation; rebuild
the route-derived plan only when the version changed (keep the per-frame `hasHw`/`canFanOut` bits,
which depend on the frame backing, as cheap locals). Reuse a pooled `branchFrames` array sized to
plan length (the submit path is serialized by `_submitLock`, so one scratch array per input is safe).
Effect: removes ~4 small allocs/frame × fps × multi-output inputs (NDI mirror, composition fan-out).

### H2 (P2) — Reusable pinned conversion targets in the sws convert paths

`MediaContainerSharedDemux.BuildConvertedPlanarVideoFrame` (MediaContainerSharedDemux.cs:~2210),
its packed sibling, `VideoFileDecoder.BuildConvertedPlanarFrame` (VideoFileDecoder.cs:~781) and
`VideoCpuFrameConverter.Convert` (VideoCpuFrameConverter.cs:112–154) all do, per converted frame:
`ArrayPool.Rent` per plane + `GCHandle.Alloc(Pinned)` per plane + `new int[n]`/`new byte[n][]`/
`new ReadOnlyMemory<byte>[n]` + a release closure. The rents are fine; the **per-frame pinning churn**
(2–4 pins per frame, 120–240 pins/s at 4K60-to-NDI) and the small-array litter are avoidable:

- Keep a small ring (3–4 entries) of pre-pinned plane buffer *sets* per converter/demux instance,
  allocated on the POH (`GC.AllocateUninitializedArray(len, pinned: true)`), sized to the negotiated
  output format and rebuilt only on format change. A frame "leases" a set; the release returns it to
  the ring (fall back to the current rent+pin path when the ring is empty).
- Fold `strides`/`memories`/release into one pooled `FrameLease` object so a converted frame costs
  one object instead of ~6.

Only matters on **converting** paths (NDI UYVY repack, mismatched-output branches, sws letterboxing) —
pass-through playback already bypasses all of this (§1 numbers prove it). Do H2 after H1 and only if
NDI/4K conversion profiling shows pinning cost; the framework's own
`MF_MEDIA_PROFILE_CHANNEL_MAP`-style counter discipline applies.

### H3 (P3) — Small inspection-path cleanups

- `AudioRouter.StuckOutputPumpIds` / `SourceIds` / `OutputIds` / `GetRegisteredOutputIds` allocate
  arrays per read — fine for occasional calls, but HaPlay polls some of these for HUD/status. Either
  document "snapshot, don't poll per-frame" or expose counts + `CopyTo`-style overloads.
- `AudioClipPlayer` line 94 uses LINQ `Any` per trigger check (`_activeVoices.Any(...)`) — trivial
  loop swap if soundboards ever fire at high rate.
- `ChannelMap.ToString()` allocates via LINQ — debug-only, leave unless it shows up in traces.

---

## 4. End-user API simplification

### A1 (P2) — One-call output attach on `MediaPlayer`

Wiring the common case today takes four steps and two id round-trips:

```csharp
var outId = player.VideoRouter.AddOutput(sink, "main");
player.VideoRouter.TryAddRoute(player.VideoRouterInputId, outId, out var err); // + err handling
var aOut  = player.AudioRouter!.AddOutput(paOut);
player.AudioRouter.Route(player.AudioSourceId!, aOut);
```

Add façade helpers (Playback tier, thin, no new state):

```csharp
string AttachVideoOutput(IVideoOutput output, string? id = null);          // add + route, rollback on failure
string AttachAudioOutput(IAudioOutput output, string? id = null,
                         ChannelMap? map = null, float gain = 1f);         // add + route from AudioSourceId
bool   TryAttachVideoOutput(IVideoOutput output, out string? id, out string? error);
```

These subsume the `VideoRouterInputId`/`AudioSourceId` plumbing for the 90 % case while leaving the
router APIs for matrix/multi-route hosts. Update Quickstart accordingly.

### A2 (P2) — Audio matrix convenience + standard downmix presets (shared with HaPlay rewrite)

`ChannelMap` is assignment-only (one input per output channel, no per-cell gain). True mixing
matrices are already expressible — **one route per non-zero cell, routes sum additively** (this is
exactly what HaPlay's per-cell matrix does) — but every host must hand-roll the cell loop. Add:

- `AudioRouter.ApplyMatrix(string sourceId, string outputId, ReadOnlySpan2D<float>/float[,] gains, string routeIdPrefix = ...)`
  — installs/updates/removes per-cell routes to match the matrix in one call (diff against existing
  `routeIdPrefix`-owned routes; gain changes go through the existing click-free `GainSlot` fade).
- `AudioChannelLayoutPresets` (Core): ITU-R BS.775 downmix coefficient matrices —
  `Downmix(srcChannels, dstChannels)` for 5.1→2.0 (L = FL + 0.707C + 0.707Ls …), 7.1→2.0, plus
  `Passthrough`, `MonoSum`. These are the framework-side enabler for HaPlay's
  "pre-defined layouts/gains, applied when an N-channel source shows up" feature
  (see `HaPlay-UI-Rewrite-Plan-2026-06-10.md` §Players).

### A3 (P3) — Trim or commit the unused Playback-tier façades

Zero usage outside tests (checked UI + Tools): `MediaGraph`/`MediaGraphBuilder`, `RoutingScene`,
`MediaSession`, `Soundboard`/`SoundboardGrid`, `CueGraph`, `TriggerBindingSet`; `MediaPlayerController`
has exactly one consumer file. Combined with the still-open
`Doc/Archive/MediaFramework-Cue-Clip-API-RFC.md`, decide per façade: **promote** (HaPlay adopts it
during the UI rewrite — `ClipStandbyEngine` route already proves the model) or **delete/internalize**.
Every public façade kept must gain a consumer or a sample; speculative surface is a maintenance tax
(and an AOT size tax). Suggested default: keep `MediaPlayer`, `MediaPlayerController`,
`ClipStandbyEngine`, `ClipCompositionRuntime`, cue primitives per the RFC outcome; fold
`MediaGraph`/`RoutingScene`/`MediaSession`/`Soundboard*`/`TriggerBindingSet` into
`Doc`-documented recipes or drop to `internal` until a host needs them.

### A4 (P3) — Paper cuts found while writing a fresh consumer

- `MediaFrameworkRuntime.Init().UseFFmpeg()` needs `using S.Media.FFmpeg;` — the Quickstart snippet
  omits the `using` lines; first-run compile error for newcomers. Add full usings to all Doc snippets.
- `IPlayhead.CurrentPosition` vs. the more discoverable `Position`/`Elapsed` naming used elsewhere
  (`ISeekableSource.Position`, `ElapsedSinceStart`) — leave the API, but add `<seealso>` cross-refs;
  three subtly different position properties (`PlayClock.CurrentPosition`, seekable `Position`,
  clock `ElapsedSinceStart`) is the most confusing corner of an otherwise tidy surface.
- `DiscardingAudioOutput(new AudioFormat(rate, channels))` requires the caller to restate the
  router's rate; a `DiscardingAudioOutput.For(router)` or an `AudioRouter.AddDiscardOutput()` test/
  fallback helper removes the foot-gun (mismatch throws at `AddOutput`).

---

## 5. Suggested order of attack

1. **R1 + R2** — transactional `TryAddRoute` + permissive empty-accepts in branch fan-out
   (single PR; directly protects live re-routing, the framework's core promise).
2. **A1** — `AttachVideoOutput`/`AttachAudioOutput` (tiny, immediately simplifies HaPlay + docs).
3. **A2** — `ApplyMatrix` + downmix presets (prerequisite for the HaPlay audio-matrix rewrite).
4. **H1** — SubmitPlan caching (small, clear win for every multi-output show).
5. **A3** — façade decision sweep alongside the cue/clip RFC review.
6. **H2/H3/A4/R3** — opportunistic.

Per repo convention: keep this file's checklist updated together with the code, and attach a short
verification report (test sweep + a re-run of the §1 probe table) to each phase.

- [x] R1 transactional TryAddRoute (+ tests) — 2026-06-10
- [x] R2 empty-AcceptedPixelFormats = pass-through in branch fan-out (+ test) — 2026-06-10
- [x] A1 MediaPlayer.Attach*Output helpers (+ Quickstart update) — 2026-06-10
- [x] A2 AudioRouter.ApplyMatrix + AudioChannelLayoutPresets (+ tests) — 2026-06-10
- [x] H1 SubmitPlan cache keyed on routes version (+ rebuild-counter test) — 2026-06-10
- [x] A3 façade keep/fold decisions recorded (§7 addendum; **execution gated on the cue/clip RFC sign-off**)
- [ ] H2 pinned conversion-target ring (deferred by design — only after profiling converting paths)
- [x] H3/A4 paper cuts (AudioClipPlayer LINQ, DiscardingAudioOutput.ForRouter, snapshot-alloc docs, IPlayhead position disambiguation, Quickstart usings) — 2026-06-10
- [x] R3 churn regression test (MediaPlayer-level attach/detach churn on a playing live graph) — 2026-06-10

---

## 6. Implementation log (2026-06-10, same day)

| Item | Change |
|---|---|
| **R1** | `VideoRouter.TryAddRoute` is transactional: route bookkeeping (`_outputOwner`, `RoutedOutputIds`, `RoutedSet`) is rolled back when `ReconfigureInputIfNeededLocked` throws, the previous valid configuration is re-applied (`TearDownPaths` + `ApplyConfigureLocked(prevFmt)`), and the method returns `false` + errorMessage instead of throwing. Tests: `VideoRouterRouteRollbackTests.TryAddRoute_BranchNegotiationFails_ReturnsFalseRollsBackAndKeepsPresenting` (asserts the primary keeps presenting and the rejected output can be re-used), `…_FailedThenRetrySameOutput_StillReturnsFalseWithoutCorruption`. |
| **R2** | `VideoOutputFanoutFormats.PickBranchPixelFormat` treats an empty `branchAccepted` as permissive → returns the negotiated source format (pass-through). Convention documented on `IVideoOutput.AcceptedPixelFormats`. Test: `…_EmptyAcceptedBranch_ReceivesNegotiatedFormatPassThrough` (runs under a no-converter router to prove no swscale path is needed). |
| **H1** | `InputRegistration` caches the route-derived submit plan behind a `_routesVersion` stamp bumped in `ApplyConfigureLocked`/`TearDownPaths`; per-frame work is now only the frame-backing bits (`hasHw` → readback/fan-out flags). The `branchFrames` array became a reused scratch (`_branchFrameScratch`, serialized by `_submitLock`, slots nulled on every exit path). Internal `VideoRouter.SubmitPlanRebuilds` counter; test `…SubmitPlan_IsCachedAcrossSubmits_AndRebuiltOnRouteChange` asserts zero rebuilds across 25 steady-state submits. |
| **A1** | `MediaPlayer.AttachVideoOutput` / `TryAttachVideoOutput` / `AttachAudioOutput` — add + route in one call, rollback (`RemoveOutput`) on route failure. Quickstart rewritten around them. Tests: `MediaPlayerAttachOutputTests` (registration, no-audio throw, rollback on bad map, permissive video branch). |
| **A2** | `AudioRouter.ApplyMatrix` (new partial `AudioRouter.Matrix.cs`): reconciles per-cell routes (`{prefix}#{src}:{dst}`) in one atomic state swap — changed cells fade via `GainSlot.Target`, new cells fade in from `Current = 0`, zeroed cells removed, foreign routes untouched, prefix collisions rejected. `RemoveMatrix` for teardown. `AudioChannelLayoutPresets`: `Passthrough`, `Downmix`/`TryGetDownmix` (mono↔N, 2→1, identity placement when dst>src, ITU-style 5.1→2.0, 7.1→2.0, 7.1→5.1; `normalize: true` caps per-output gain sums at 1.0). Tests: `AudioRouterMatrixTests` incl. an end-to-end swap-matrix mix assertion through the running router. |
| **R3** | `MediaPlayerAttachOutputTests.AttachDetachChurn_OnPlayingLiveGraph_StaysHealthy` — 25 attach/detach cycles (audio + video) on a playing live graph; asserts no router fault and the audio router still running. |
| **A4/H3** | `AudioClipPlayer` OneShot check de-LINQed; `DiscardingAudioOutput.ForRouter(router, channels)`; snapshot-allocation notes on `AudioRouter` id properties; `IPlayhead.CurrentPosition` doc disambiguates the three position properties; Quickstart snippets now carry their `using` lines. |

**Deliberately deferred:** **H2** (pinned conversion-target ring) — the review itself gates it on
profiling evidence from a converting workload (NDI UYVY at 4K60); pass-through playback measures
allocation-free without it.

**Verification report (2026-06-10):** full solution build 0 errors. Test sweep all green —
S.Media.Core **494** (was 483; +11 new), S.Media.FFmpeg **180**, S.Media.Playback **82** (+5 new
attach/churn), NDI 61, OpenGL 108, SkiaSharp 10, PortAudio 26, FFmpeg.Encode 8, HaPlay **449**.
Probe re-runs on the 4K60 ProRes: §2 R1 repro now returns `TryAddRoute=false`-free path — the
formerly-crashing permissive branch routes successfully (R2) and presentation continues
(`displayed` keeps climbing instead of freezing); steady-state §1 numbers unchanged after H1
(26.7 KiB/s, gen0/1/2 = 0/0/0, 738 frames decoded / 0 late drops in 12 s).

---

## 7. A3 addendum — façade keep/fold proposal (recorded, not yet executed)

Execution is gated on the cue/clip API RFC review (`Doc/Archive/MediaFramework-Cue-Clip-API-RFC.md`,
still awaiting sign-off), because the same decision sweep covers `CueGraph` and the clip runtime
surface. Proposal on record:

| Façade | Consumers (UI/Tools) | Proposal |
|---|---|---|
| `MediaPlayer` | 10 files | **Keep** (primary product entry; now carries the Attach helpers) |
| `MediaPlayerController` | 1 file | **Keep** |
| `ClipStandbyEngine` / `ClipCompositionRuntime` / `ClipAudioOutputRuntime` | 1 file + HaPlay cue engine | **Keep** |
| `MediaGraph` / `MediaGraphBuilder` | 0 | Fold to a Doc recipe or `internal`; `MediaSession` goes with it |
| `RoutingScene` | 0 | Fold to `internal` (HaPlay's matrix presets supersede the concept) |
| `Soundboard` / `SoundboardGrid` | 0 | Decide with the RFC (cue primitives overlap) |
| `CueGraph` | 0 | RFC subject — decide there |
| `TriggerBindingSet` | 0 | Fold to `internal` (TriggerBus stays public) |
