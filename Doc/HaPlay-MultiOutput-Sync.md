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
* **Video actuator = the missing piece.** Today each physical video output's `VideoOutputPump` runs
  its own present cadence. For a group, all member video outputs must present the frame for the
  reference timestamp on a **shared** tick, with **lock-step** repeat/drop. This is the
  *"synchronized drop/repeat across outputs"* the architecture doc explicitly lists as **not
  implemented** — and it is precisely what stitching needs.
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
* **Option B — Phase 2 (remaining).** Lock-step frame *present* for video-only outputs that have **no**
  audio actuator (pure LED/projector walls): a shared present scheduler across the grouped video pumps
  that selects the frame for the reference tick with coordinated repeat/drop. Plus host wiring in HaPlay
  to declare sync groups and route each member's device through the controller. This builds directly on
  the Phase-1 `OutputSyncGroup`.

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
AutoWirePrimary auto-slave skips `IAdaptiveRateWrappedOutput` by design). What remains is the **host wiring**
(create the group, choose membership, pass the provider, drive `Tick`) — deliberately **not** yet wired into
the live cue engine, because its efficacy (correct drift *direction*, no drops) can only be confirmed with
**two real audio devices**, and a wrong direction would not show up in the unit suite.

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

**Open design decision (yours):** genlock *scope*. Per-composition (reference = each composition's
`PlaybackClockMaster`) is the literal fix for the documented caveat, but `ClipAudioOutputRuntime`s are
**pooled per device and shared across compositions**, so a per-composition correction on a shared device is
ambiguous. Engine-wide genlock (one reference device, all other active devices disciplined to it) sidesteps
that and is the natural "lock the whole show to one master" model — but it's a product choice. This needs
deciding **and** two-device hardware validation before the host wiring ships.
3. Own one `OutputSyncGroup` per composition (reference = `PlaybackClockMaster`); `group.Start(100 ms)` (or
   tick it from the engine's existing periodic loop). Dispose it with the composition; `RemoveMember` when a
   runtime is released by `ReleaseEmptyRuntimes`.

**Guard rails:** keep it behind an opt-in setting (default off) so existing single-device / same-device
compositions are untouched; the controller already no-ops when a member isn't advancing and resets on a
seek-sized jump, so a paused/queued cue won't get a bogus correction.

**Phase 2b (video-only walls):** outputs with no audio actuator can't be slewed via `AdaptiveRateAudioOutput`.
On a single machine, pure software video clocks share the system QPC and don't drift, so the real need is a
shared present *schedule* (all grouped video pumps present the reference-timestamp frame in lock-step) rather
than rate disciplining; separate display pixel clocks ultimately need hardware genlock. That present-scheduler
is the remaining framework piece, layered on the same `OutputSyncGroup` reference.
