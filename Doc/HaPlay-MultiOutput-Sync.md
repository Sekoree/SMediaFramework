# Multi-output sync — options & long-term direction

Companion to issues-and-improvements **#2** (`Doc/Explained/15-Issues-and-Improvements.md`)
and the clock model in [`Doc/Explained/06`](Explained/06-Clocks-and-AV-Sync.md) /
[`04`](Explained/04-Core-Audio-Engine.md). Answers: *"with multiple outputs — and future
compositions stitched across several outputs — which is the better long-term solution?"*

## TL;DR

**Option A and Option B are not competitors — A is a building block of B.**

* **Now (shipped):** enable Option A (per-output adaptive resampling) by default for HaPlay
  multi-output graphs. It is essentially free (the machinery already existed) and it solves
  the *actual current pain*: independent program feeds (NDI + local device + a second device)
  drifting on their own crystals until one drops or starves.
* **Long term, for stitched / video-wall surfaces:** Option B (a coordinated, group-level
  clock policy) is the real answer, because Option A **cannot frame-lock** two outputs into one
  seamless canvas. Build B as an explicit **output sync group ("genlock domain")** that reuses
  Option A's resampler as its audio actuator and adds the missing piece: **lock-step video
  present across the grouped outputs**.

## Why A is right for independent feeds and wrong for stitching

| | Independent feeds (3 outputs, same program) | Stitched surface (1 canvas split across N outputs) |
|---|---|---|
| Requirement | No output drops/starves | All outputs show the frame for the **same master timestamp** on the **same tick** — no seam tear |
| Failure if unmet | Audible click / frame stall after minutes | Visible tear at panel seams; an object crossing the boundary is one frame ahead on one side |
| What Option A does | Nudges each non-master output's sample rate ±few Hz from its queue pressure → drift self-corrects | **Nothing useful** — it is audio-only and reactive; it never phase-aligns video outputs |
| Sufficient? | **Yes** | **No** — needs Option B |

Option A is reactive (driven by drop pressure), audio-only, and prevents queue overflow/starvation
per output. It does not make two outputs *present the same pixels at the same instant* — outputs
still differ in latency by tens of milliseconds, which is invisible for independent feeds and fatal
for a stitched wall.

## Recommended long-term design — Option B as an output sync group

A graph-level controller, scoped to an explicitly declared **group** of outputs rather than applied
globally (HaPlay mixes both use cases — don't force lock-step on feeds that only need drift control):

```
            ┌──────────────── OutputSyncGroup ("genlock domain") ───────────────┐
            │  reference = MediaClock master (audio device) | wall | ext genlock │
            │                                                                    │
 played-sample / pump stats ──► phase+rate controller ──► per member output:     │
 (already exposed today)        (the missing policy)       • audio: ppm slew via  │
                                                             AdaptiveRateAudioOutput  ← reuse Option A
                                                           • video: shared present  │
                                                             scheduler, lock-step    │  ← the new piece
                                                             repeat/drop on the      │
                                                             reference tick          │
            └────────────────────────────────────────────────────────────────────┘
 outputs NOT in a group keep today's independent behaviour (correct for feeds)
```

* **One reference per group.** Reuses the existing single-`MediaClock`-master model — no new clock
  primitive needed for the common case. Falls back to wall-clock when the group has no audio master;
  leaves a seam for a future external **genlock/PTP** reference.
* **Audio actuator = Option A.** The controller drives each member's `AdaptiveRateAudioOutput` toward
  the reference instead of each output independently reacting to its own drops. Option A's resampler
  is exactly the right knob; B just supplies a better setpoint.
* **Video actuator = `VideoPresentSyncGroup` (built 2026-06-15).** Each physical video output's
  `VideoOutputPump` runs its own present cadence; for a group, all member video outputs must instead
  present the frame for the reference timestamp on a **shared** tick, with **lock-step** repeat/drop.
  That scheduler now exists (`VideoPresentSyncGroup` + `SyncPresentVideoOutput`, `S.Media.Core.Video`) —
  the *"synchronized drop/repeat across outputs"* the architecture doc lists as not-implemented, and
  precisely what stitching needs. (Wrap each member device output in a `SyncPresentVideoOutput` instead of
  a `VideoOutputPump` and add it to the group.)
* **Per-group opt-in expresses intent.** A streaming feed + a confidence monitor showing the same
  program should *not* be force-locked (it would only add latency); a 2×2 projector wall *must* be.
  A group boundary is the natural place to declare which.

This also closes the **cross-cue compositor alignment caveat** (doc 06): two cues on two devices in
one composition drift today; put both devices in one sync group and the controller rate-locks them.

## Two honest caveats

1. **Prefer single-output + output mapping where the hardware allows.** The shipped warp/mapping
   (`WarpSection`/`WarpMesh`, GL) already carves **one** output into sections mapped onto multiple
   panels. An LED wall fed by one processor, or a GPU spanning heads as one logical surface, needs
   **no** multi-output sync at all — it's one output plus mapping. Reserve Option B for *genuinely
   separate* physical outputs each driving part of the canvas. That keeps the hard case rare.
2. **Software present scheduling reduces skew to sub-frame but cannot guarantee zero tear** across
   separate GPU outputs without hardware genlock (display/Quadro Sync) or a single spanned surface.
   The framework's job is to feed the right frame at the right tick; the last vsync-phase difference
   is a display-server / hardware concern. B should target sub-frame alignment and document the
   hardware-genlock boundary rather than promise pixel-lock it can't deliver in software.

## Status

* **Option A — done (2026-06-15).** HaPlay registers the FFmpeg adaptive-rate plugin at startup
  (`App.InitializeMediaFramework`) and calls `AudioRouter.EnableAdaptiveRateOnNonMasterOutputs()` on
  the playback session router (`HaPlayPlaybackSession.EnableMultiOutputDriftCorrection`). No-op for
  single-output graphs; guarded so a missing plugin degrades gracefully.
  * **Cue / composition audio is *not* a target for Option A.** Each `ClipAudioOutputRuntime` owns
    one `AudioRouter` feeding exactly **one** physical device, so there is no non-master output for
    the wrapper to act on — enabling it there is a no-op. Two cues on two devices are *separate*
    routers with *separate* masters; that cross-router coordination is exactly what Option B (the
    sync group) exists to solve, and it cannot be reached from Option A.
* **Option B — Phase 1 foundation built (2026-06-15).** `OutputSyncGroup` (`S.Media.Core.Clock`) is the
  coordinated master-ppm controller the architecture doc listed as not-implemented: a reference clock +
  member clocks + a bounded **PI rate controller** (tuned by loop bandwidth + damping) whose per-member
  ppm output feeds Option A's `AdaptiveRateAudioOutput` via its ppm-provider constructor — slewing a
  member's device rate paces its master clock, which paces its video, so audio and video both converge.
  Resets the loop on pause/seek (discontinuity guard). Unit-tested: locks a +40 ppm member to sub-ms
  phase with a ~−40 ppm correction. Drive it from a host loop (`Tick(elapsed)`) or its internal timer
  (`Start(interval)`).
* **Option B — Phase 2b video present scheduler: done (framework, 2026-06-15).** The lock-step frame
  *present* piece is now built in Core (`S.Media.Core.Video`): `VideoPresentSyncGroup` (the scheduler) +
  `ISyncPresentableVideoOutput` (the member contract) + `SyncPresentVideoOutput` (a buffering output that
  presents on the group's tick instead of its own cadence). Per tick the scheduler reads the reference
  `IReadOnlyPlayhead`, and: presents all members in lock-step at the *oldest* of their newest-due PTS when
  every member is advance-ready; **holds** (presents nothing new, bounded by `MaxStarveHoldTicks`) when some
  members are ready but others have fallen behind, so the canvas never tears; then **degrades** to
  presenting the ready members so a wedged output can't freeze the whole wall; and treats a tick where no
  member is due as a normal between-frames hold. This is exactly the *"synchronized drop/repeat across
  outputs"* the architecture doc lists as not-implemented. Unit-tested (12 cases) in
  `S.Media.Core.Tests/Video/VideoPresentSyncGroupTests.cs`; full Clock+Video sweep green (305).
  * It composes with Phase 1 through one master playhead: the same `MediaClock` whose `IPlaybackClock`
    feeds the audio `OutputSyncGroup` is the present scheduler's `IReadOnlyPlayhead` reference. Audio side
    rate-disciplines crystals; video side phase-aligns the present.
* **Option B — Phase 2 audio host wiring: done (engine-wide, opt-in, 2026-06-15).** Scope decision made:
  **engine-wide** (one reference device, every other active audio device disciplined to it). The cue engine
  owns one `EngineAudioGenlock` (`UI/HaPlay/Playback/EngineAudioGenlock.cs`) — the first registered device
  becomes the reference (left **unwrapped**, so the master clock is never run through a resampler; note
  `AdaptiveRateAudioOutput` resamples even at 0 ppm), every other device joins one `OutputSyncGroup` as a
  member and is wrapped via `ClipAudioOutputRuntime.ratePpmProvider`. Wired into `GetOrCreateAudioRuntime`
  (register member + thread provider), `ReleaseEmptyRuntimes` (unregister + reference handoff), and engine
  `Dispose`. **Opt-in and off by default** via `HAPLAY_MULTIOUTPUT_GENLOCK=1`; when unset the manager is
  never constructed and the audio path is byte-identical to before. Unit-tested (`EngineAudioGenlockTests`,
  5 cases: reference selection, member discipline, idempotent register, reference handoff on release,
  zero-for-unknown). **Still needs two-device hardware validation** before the toggle is promoted to a
  default / UI setting (a wrong drift *direction* won't surface in the unit suite).
* **Multi-output composition layout editor: done (2026-06-15).** The UI that *defines* a multi-output
  composition — which part of the canvas each physical output shows — ships in the cue player. From the cue
  **Output setup** dialog, each output row has a **Layout…** button opening
  `CompositionOutputLayoutDialog`: a draggable, aspect-correct view of the composition canvas with every
  output bound to that composition drawn as a movable/resizable box (its normalized source slice).
  Overlapping boxes are blend zones; canvas not covered by any box is a gap (stays black). On Save each
  output's slice is written back to its `CueOutputMapping` (one full-output section sized to the slice — the
  video-wall tile model) and live-applied via `UpdateOutputMappingCallback`. Built from the proven
  `CompositionPlacementCanvas` pattern: `OutputLayoutCanvas` (control) + `CompositionOutputLayoutViewModel`
  / `OutputLayoutItemViewModel`. Unit-tested (`CompositionOutputLayoutViewModelTests`, 4 cases: slice
  round-trip, defaults, overlaps/gaps). *Note:* applying a layout slice resets that output's mapping to a
  single tile section — use the per-output **Mapping…** editor afterwards for warp/mesh on a tile.
* **Option B — Phase 2 video present-sync for compositions: NOT NEEDED (resolved by design, verified 2026-06-15).**
  Investigating where to add the present-sync hook inside `ClipCompositionRuntime` showed the composition
  fan-out is **already frame-locked** — there is nothing for a `VideoPresentSyncGroup` to add here. Evidence:
  1. **Single-cadence fan-out.** `ClipCompositionRuntime.PumpOneFrame` composites the canvas **once per tick**
     and submits **that one frame to every output** in a single loop (zero-copy `TryCreateCpuFanOutViews`
     over the same canvas backing). Every output therefore shows canvas-frame-N on the same tick by
     construction — the strongest software-level frame-lock.
  2. **It runs for video-only too.** `EnsurePumpStarted` starts a freerun `MediaClock` at the canvas rate
     even with no audio master, so an audioless wall is pumped and locked (no black-output risk — the earlier
     lease-level concern is moot because no separate present group is involved).
  3. **Proven by an existing test.** `S.Media.Playback.Tests` →
     `ClipCompositionRuntime_MultiOutputPump_SharesCanvasBackingAcrossOutputs` asserts both outputs receive
     **same-PTS** frames over the **same backing** — i.e. the frame-lock guarantee is already covered.
  4. **NDI egress already timecoded.** `NDIOutputPreviewRuntime` stamps `NDIVideoTimecodeMode.PresentationRelativeTicks`,
     so NDI receivers already get a shared presentation timecode.

  A `SyncPresentVideoOutput` wrapped around the leases would only buffer already-synchronized frames and
  couldn't see each output's *downstream* device congestion, so it adds latency with no lock benefit; and
  coordinated drop across a mixed local+NDI wall (freeze the local projectors because the NDI network
  hiccuped) is usually undesirable. The **residual** cross-output skew is device vsync phase / NDI network
  timing — a **hardware-genlock** concern (display genlock / Quadro Sync; NDI Discovery + receiver-side
  timecode), outside what the framework can do in software.

  `VideoPresentSyncGroup` + `SyncPresentVideoOutput` remain valid Core primitives for a *different*
  architecture — outputs driven by **independent** per-output clock ticks (e.g. N separate `VideoPlayer`s);
  they're just not what the composition fan-out needs.

### Possible Option-A follow-up (small, needs author sign-off)

The framework's `MaybeWrapAdaptiveRateOutputLocked` currently wraps **every** non-master output. A
non-clocked tap (a meter, a discarding sink) cannot drift — it has no independent crystal — so
wrapping it in a resampler is harmless but wasted work. Restricting the auto-wrap to
`IClockedOutput` non-master outputs would be strictly more conservative and target exactly the
outputs that actually drift. Left out of the current change because it alters a framework hot-path
heuristic shared by all hosts; flagged here for review.

## Phase 2 — concrete cue-engine wiring plan (audio-actuated path)

`OutputSyncGroup` now observes **`IPlaybackClock`** (`ElapsedSinceStart` is the raw genlock metric), which
is exactly what the cue audio path exposes. **The actuation primitive is now built (2026-06-15):**
`ClipAudioOutputRuntime` takes an opt-in `ratePpmProvider` constructor argument (default-off) that wraps its
device output in an `AdaptiveRateAudioOutput` and explicitly `SlaveTo`s the router to that wrapper (the usual
AutoWirePrimary auto-slave skips `IAdaptiveRateWrappedOutput` by design). The **host wiring is now done**
(engine-wide, opt-in `HAPLAY_MULTIOUTPUT_GENLOCK`) via `EngineAudioGenlock` — see the Status section. The
notes below record the integration points; they remain accurate for the (still-pending) **video** host
wiring and as a reference for the audio path that shipped. Two-device hardware validation of drift
*direction* is the remaining gate before the toggle becomes a default.

**Integration points (all in `UI/HaPlay/Playback/CuePlaybackEngine*`):**
- Per device line, `GetOrCreateAudioRuntime(outputLineId)` builds a `ClipAudioOutputRuntime` from
  `AcquireAudioOutput(...)`, which returns `(IAudioOutput Output, IPlaybackClock? PlaybackClock, …)`. The
  `PlaybackClock` is the device's physical clock — the per-member genlock input.
- A composition's reference is already chosen: `ActiveCue.PlaybackClockMaster` is set to the **first**
  runtime's `PlaybackClock` (`CuePlaybackEngine.AudioRouting.cs` ~77). That's the `OutputSyncGroup` reference.

**Wiring (opt-in, per composition that spans >1 device):**
1. When the composition acquires a runtime whose `PlaybackClock` differs from `PlaybackClockMaster`, treat it
   as a sync member: `var h = group.AddMember(runtime.PlaybackClock)`.
2. Actuate it by passing `ratePpmProvider: () => group.GetMemberPpm(h)` to the `ClipAudioOutputRuntime`
   constructor (**implemented 2026-06-15**, default-off): the runtime wraps its device output in an
   `AdaptiveRateAudioOutput` driven by that provider and explicitly slaves the router to the wrapper. So the
   only remaining host work is to thread the provider through where the runtime is created
   (`GetOrCreateAudioRuntime` / `AcquireAudioOutput`).

**Design decision (made 2026-06-15): engine-wide.** Per-composition (reference = each composition's
`PlaybackClockMaster`) is the literal fix for the documented caveat, but `ClipAudioOutputRuntime`s are
**pooled per device and shared across compositions** (and the soundboard), so a per-composition correction on
a shared device is ambiguous — a device can only take one correction. **Engine-wide** (one reference device,
all other active devices disciplined to it) is unambiguous, fits the pooled model, and is the natural "lock
the whole show to one master" model. Implemented as `EngineAudioGenlock`. The reference is the first device
registered, with reference handoff on release. *Two-device hardware validation is still required* before the
opt-in becomes a default.

**Guard rails:** keep it behind an opt-in setting (default off) so existing single-device / same-device
compositions are untouched; the controller already no-ops when a member isn't advancing and resets on a
seek-sized jump, so a paused/queued cue won't get a bogus correction.

**Phase 2b (video-only walls):** outputs with no audio actuator can't be slewed via `AdaptiveRateAudioOutput`.
On a single machine, pure software video clocks share the system QPC and don't drift, so the real need is a
shared present *schedule* (all grouped video pumps present the reference-timestamp frame in lock-step) rather
than rate disciplining; separate display pixel clocks ultimately need hardware genlock. That present-scheduler
is the remaining framework piece, layered on the same `OutputSyncGroup` reference.
