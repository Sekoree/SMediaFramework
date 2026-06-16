# 06 · Clocks & A/V Sync (the keystone)

This is the most important concept in the framework. If audio and video don't agree
on *what time it is*, you get lip-sync drift, stutters, or freezes. Read this slowly.

## The problem, ELI5

You have two things playing at once: a sound and a picture. Each needs to know *when*
to advance. The naive answer is "use the wall clock" (a stopwatch). That fails,
because of a subtle hardware fact:

> A sound card has its own crystal oscillator. It does **not** tick at *exactly* the
> rate it claims. A "48,000 Hz" card might really play 48,002 or 47,998 samples per
> second. Over an hour that's seconds of drift.

So if video follows the wall clock and audio follows the sound card, they slowly
slide apart — and you can't fix it by "just being careful," because the two clocks
are physically different crystals.

**The fix: pick one master and make everyone follow it.** In MFPlayer the master is
(almost always) the audio output, because human ears notice an audio glitch far more
than a one-frame video hiccup. The audio device reports *"I have actually played N
samples,"* and "current time" is defined as `N / sampleRate`. Video then asks that
same clock "what time is it?" and shows the matching frame. Now both follow the *same*
crystal — the one you can actually hear — so they can't drift apart.

```
  WRONG: two crystals, slow divergence          RIGHT: one master, locked
  ┌─────────┐         ┌─────────┐               ┌─────────┐
  │  video  │←wall    │  audio  │←card           │  video  │──┐
  │  clock  │ clock   │  clock  │ crystal        └─────────┘  │ "what time is it?"
  └─────────┘         └─────────┘                             ▼
       slowly ─────────► drift                          ┌──────────────┐
                                                        │  MediaClock  │
                                                        │  (slaved to  │
                                                        │  audio device)│
                                                        └──────────────┘
                                                              ▲ "I've played N samples"
                                                        ┌─────────┐
                                                        │  audio  │
                                                        │ output  │
                                                        └─────────┘
```

## The contract: `IPlaybackClock`

A playback clock is a thing that can answer "how much real playback has happened?"

```csharp
public interface IPlaybackClock
{
    TimeSpan ElapsedSinceStart { get; }  // MONOTONIC — never goes backwards
    bool IsAdvancing { get; }            // true while actually playing
}
```

Two ironclad rules:

1. **`ElapsedSinceStart` is monotonic** — it represents real progress (samples
   consumed ÷ rate). It must never regress.
2. **The freeze contract:** pausing or underrunning the underlying source must
   **freeze** `ElapsedSinceStart`. If a paused/underrun output keeps advancing its
   reported time, the playhead drifts away from where the audio actually is, and
   pause/resume desyncs. The audio output implements this carefully — see below.

The typical implementer is the audio output (`PortAudioOutput`): it counts samples
the hardware callback has consumed and divides by the rate. NDI receive provides
`NDIIngestPlaybackClock` (driven by receiver timecode). Video-only content provides
`VideoPtsClock`.

## The master: `MediaClock`

`MediaClock` is the conductor the whole graph reads. It implements `IMediaClock`
(= `IPlayhead` + tick events + transport). Two modes:

* **Stopwatch mode (default):** free-running, backed by a `Stopwatch`. Used when no
  output owns an authoritative clock (silent video, headless tests).
* **Slaved mode:** `SetMaster(playbackClock)` attaches an `IPlaybackClock`. Now
  `CurrentPosition` is derived from the master's real elapsed time, not wall time.

### Two kinds of "time" in one object (the subtle bit)

`MediaClock` separates *render cadence* from *media-time advancement* — this trips
people up, so be explicit:

* **Tick events** (`AudioTick` ~100 Hz, `VideoTick` ~60 Hz, `PositionChanged` ~30 Hz)
  are driven by an **internal wall-clock driver thread**. They mean "it's time to
  render again," *not* "media time advanced by X." The driver runs regardless of
  whether a master is attached.
* **`CurrentPosition`** (media time) is computed on demand from the master:

  ```
  position = basePosition + (master.ElapsedSinceStart − masterAnchor)
  ```

  `basePosition` is where we were at the last Start/Seek/SetMaster; the anchor is the
  master's elapsed at that moment. So "how far have we played" always comes from the
  master's real sample count, while "how often do we redraw" comes from the driver.

> **ELI5:** the driver thread is a metronome that says "tick!" 60 times a second so
> the screen redraws smoothly. But *what time the song is at* is read off the audio
> device's odometer, not the metronome.

### The driver thread is burst-capped (anti-freeze)

If a tick handler runs long or the OS schedules the thread late, the driver
**bursts** the missed deadlines (capped at 64 audio / 64 video / 8 position per wake)
and then **fast-forwards** the schedule to "now." A long stall therefore produces a
short catch-up burst, not an ever-growing backlog that freezes the process. (See
`DriverLoop` in `MediaClock.cs`.)

### Pause / resume and the "fold" (a real bug class, solved)

Here's a problem you only hit with a real audio device. When you `Pause`:

* The clock snapshots `_masterElapsedWhenPaused = master.ElapsedSinceStart`.
* But the audio device's output ring **is still draining** for a few more
  milliseconds — the hardware keeps playing buffered samples after you said "pause."

If you ignore that, on resume the playhead is behind where the audio actually got to,
and video jumps. So `MediaClock.Start` **folds in the drift** that accrued while
paused:

```csharp
// On Start(), if we have a master and were paused:
var drift = master.ElapsedSinceStart − _masterElapsedWhenPaused;
if (drift > 0) _basePosition += drift;   // audio kept playing during "pause" — account for it
// (if drift < 0, the master regressed — flush/segment reset — don't fold)
```

This pause-fold is *the* fix for pause/resume desync. It only works because the
output honors the freeze contract (a clean pause freezes elapsed; the small post-pause
drain is the legitimate delta being folded). See the `playbackclock-freeze-contract`
note in the project memory.

### Transport methods

`Start`, `Pause`, `Stop` (= Pause for now), `Reset` (→ 0), `Seek(position)`,
`SetMaster`. `Seek`/`Reset` raise `PositionChanged` *synchronously on the caller's
thread*; the driver raises it ~30 Hz otherwise (marshal to your UI thread if needed).
`MediaClockExtensions.SetMasterChain` and `PlaybackTimelineClockExtensions` are sugar.

## The clock family

| Type | Implements | Role |
|------|-----------|------|
| `MediaClock` | `IMediaClock`, `IPlayhead` | The master driver everything reads. Slaves to an `IPlaybackClock`. |
| `CompositePlaybackClock` | `IPlaybackClock` | Merges several candidates by **priority**: the active one is the highest-priority candidate whose `IsAdvancing` is true. `CompositePlaybackClockBlend` adds optional smooth-snap behaviour beyond a hard switch. |
| `VideoPtsClock` | `IPlaybackClock` | Driven by the most recently presented video frame's PTS + wall delta. For file/VFR video with no audio master. |
| `NDIIngestPlaybackClock` | `IPlaybackClock` | Driven by NDI receiver audio timecode/timestamp (100 ns units) + wall extrapolation between captures. The receive-side analogue of a sample clock. |

The playhead surface is layered so consumers get only what they should touch:
`IPlayhead` (full: position, running, rate, cooperative seek) → `IReadOnlyPlayhead`
(no seek — what `AsPlayhead()` hands observers) → legacy `IPlaybackPlayhead`/
`IPlaybackTimeline` aliases (obsolete, use `IPlayhead`).

## How the engines use the clock

* **`AudioRouter.SlaveTo(outputId)`** makes the router's pacing follow that output's
  `IClockedOutput.WaitForCapacity` *and* makes that output the master clock (the one
  with backpressure — see [04](04-Core-Audio-Engine.md)).
* **`VideoPlayer`** reads `clock.CurrentPosition − PlayheadOffset` each `VideoTick`
  and presents the matching frame ([05](05-Core-Video-Pipeline.md)). The offset
  accounts for audio being buffered *ahead* in the output ring, so the picture lines
  up with *heard* audio.

```
       PortAudio output ── reports played samples ──► MediaClock.SetMaster(it)
            ▲                                              │
     AudioRouter.SlaveTo(it)                               │ CurrentPosition
     (paces mix + backpressure)                            ▼
                                                  VideoPlayer.OnVideoTick:
                                                  playhead = pos − PlayheadOffset
                                                  → show the frame at that PTS
```

## Coordinating A/V transport: the session types

When one logical "media" has both an audio router and a video player, their
start/stop/seek must happen in the right order. Core provides:

* `AvPlaybackCoordinator` — orders combined start/stop/seek across an audio side and a
  video side. `IAvPlaybackSession` is the internal contract.
* `MediaPlaybackSession` — holds the video player + source (+ optional audio router /
  clock) for coordinated transport.
* `PauseFlushPolicy` — whether a coordinated pause/seek runs a shared-mux libav flush
  after A/V quiesce (the deadlock-avoidance knob discussed in
  `Doc/MediaFramework-Architecture.md` and doc [08](08-FFmpeg-Decode-and-Encode.md)).

## Drift between multiple outputs (the honest limitation)

Only the *one* slaved output is the master. Every other output runs off its own
crystal and drifts at ~±50 ppm. The baseline per-output tools: `AudioRouter.PumpPressure`
events, FFmpeg's `AdaptiveRateAudioOutput` (a few-ppm per-output resample driven by
`PumpPressurePlaybackHintMonitor`), and `GetAggregatePumpStats`. NDI provides
`NDIFrameSync` for receive-side TBC. See [04](04-Core-Audio-Engine.md) and
[09](09-Output-Backends.md).

> **Update (2026-06-15) — genlock primitives now exist.** What the original text called
> "not provided" is now built as opt-in Core primitives (the multi-output sync work in
> `Doc/HaPlay-MultiOutput-Sync.md`): **`OutputSyncGroup`** (`S.Media.Core.Clock`) is the
> coordinated master-PPM PI controller — it disciplines member clocks to one reference and
> emits per-member ppm corrections that drive each member's `AdaptiveRateAudioOutput`
> (audio rate). **`VideoPresentSyncGroup`** + `SyncPresentVideoOutput`
> (`S.Media.Core.Video`, see [05](05-Core-Video-Pipeline.md)) add the lock-step
> *present* across grouped video outputs (the synchronized drop/repeat). They compose
> through one master playhead. HaPlay auto-enables the per-output adaptive resample
> ("Option A") already; full *group* wiring (declaring a genlock domain across several
> devices) is the remaining host step, deferred until validated on real multi-output
> hardware.

### Cross-cue alignment caveat (compositions)

When several media cues share one composition, each cue's video pump is slaved to
*its own* audio master. Per-cue A/V stays locked; **cross-cue** pixel-accurate
alignment is only guaranteed when those cues route to the **same physical audio
device** (one crystal). Two cues on two different PortAudio devices in one compositor
will slowly drift relative to each other — route both to the same device when it
matters. (`SlotKeepPolicy.MasterAligned` degrades frame-rate mismatches gracefully
but cannot merge two physical clocks.)

## Two seek war-stories worth knowing

These are real bugs that were found and fixed; they illustrate how delicate sync is.

1. **HW-frame PTS loss → seek lands on the wrong frame.** On long-GOP content with
   hardware decode, frames briefly lost their PTS because `av_frame_copy_props` wasn't
   called — so a keyframe got mislabeled as the seek target and audio ended up "ahead"
   after a seek. The content-verifying probe (`--verify-content`) catches label-blind
   seeks. (Memory: `hwframe-pts-seek-desync`.)
2. **Timebase mismatch on trimmed cues.** A clip rebased with `RetimingVideoOutput`
   (frame PTS shifted to a zero-based clip timeline) needs an equally rebased playhead
   (`OffsetPlayhead`) — otherwise a backward seek freezes the video on a trimmed cue,
   because the player compares rebased frame PTS against a non-rebased clock. Always
   pair the two. (Memory: `timebase-mismatch-trap`.)

`OffsetPlayhead` (an `IPlayhead` view shifted by a constant) and `RetimingVideoOutput`
are the two halves of clip rebasing — covered in [11](11-Playback-Product-Tier.md).

Next: [07 · Triggers, Diagnostics & Runtime](07-Triggers-Diagnostics-Runtime.md).
