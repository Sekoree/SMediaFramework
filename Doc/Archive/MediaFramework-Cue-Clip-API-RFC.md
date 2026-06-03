# Framework Cue / Clip API — Design RFC

Created: 2026-06-03
Status: **Phase 1 implemented** (framework API + default standby engine landed in
`S.Media.Playback`; shared cue audio/composition runtime primitives landed; HaPlay
file/live source-lifecycle migration landed; video-capable soundboard remains a follow-up)

Backlog origin: "Consider a framework-level cue/clip API that can power HaPlay,
soundboards, and cue-player hosts" (see `HaPlay-CuePlayer-Cleanup-Backlog.md`,
Larger Enhancements). Desired guarantees: **non-consuming standby, explicit start
barrier, coordinated grouped starts, clip-relative audio/video timing, clear output
ownership.**

## 1. Motivation

The richest cue logic in the codebase lives in HaPlay's `CuePlaybackEngine`
(`UI/HaPlay/Playback/`), not in the reusable framework. A second host that wants the
same behaviour (a soundboard with video, an external cue-player front end, a show
controller) would have to re-derive: standby preparation, the open→seek→route→hold
pipeline, the start barrier, grouped coordinated starts, clip-relative A/V rebasing,
and output ownership / conflict release.

The framework already carries the *vocabulary* for cues but not the *standby engine*.
This RFC proposes lifting the five guarantees into `S.Media.Playback` as a small,
explicit clip-standby surface that HaPlay's engine becomes a thin host of, and that a
video-capable soundboard can also build on.

## 2. What already exists (inventory)

Framework — `S.Media.Core`:

- `ClipWindow` — source start/end, effective duration, source↔relative mapping, guarded
  end detection (promoted out of HaPlay in the earlier cleanup).
- `Video.RetimingVideoOutput` — additive PTS offset (+ zero-clamp), zero-copy on
  hw/dmabuf backings; the clip-rebase primitive.
- `MediaPlayer` (`S.Media.Playback` assembly) — transport with `VideoRouter`,
  `AudioRouter`, and `Play(videoOnlyMaster:)` for clock-slaved video.

Framework — `S.Media.Playback` (the existing "product" layer):

- `CueGraph` / `CueDefinition` / `CueShowFile` — a serializable cue-graph model. Note
  `CueDefinition.PreloadKey` and `CueExecutionStatus.NotReady` already reserve a
  preload/readiness concept; phase 1 adds the standby engine, but does not yet wire it
  into `CueGraph.PreloadKey`.
- `Soundboard` / `CueVoice` — an **audio-only** cue voice engine over `AudioRouter`
  (`AddCue` / `Fire` → `CueVoice` / `Reap`, choke groups). Fire-and-forget: no standby,
  no video, no start barrier, no grouped coordinated start.
- `MediaGraph` / `MediaGraphBuilder` — topology presets, incl. `CueCompositor` and
  `SoundboardToAudioOutput`.
- `SoundboardGrid` (`SoundboardPadMode`, `SoundboardLedState`) — pad/LED surface.
- `TriggerBindingSet` — MIDI/OSC/keyboard → action routing (`CueGraph`, `Soundboard`).
- `ClipStandbyEngine` / `ClipSpec` / `IArmedClip` — phase-1 standby surface:
  opens through existing `MediaPlayerOpenBuilder`s, seeks to `ClipWindow.Start`, holds
  ready clips warm under `ClipStandbyPolicy`, arms without auto-starting, and starts
  groups after every member is armed.
- `ClipAudioOutputRuntime` / `ClipCompositionRuntime` — shared output-runtime
  primitives: audio-router ownership per output, compositor/layer/output fan-out per
  composition, clock-master handoff, and output-pressure/drift signals. Hosts still adapt
  their concrete devices into framework output leases.

HaPlay — `UI/HaPlay/Playback/` (where the real guarantees live today):

- `CuePlaybackEngine` — orchestrates cue tree and route planning; delegates standby to
  `ClipStandbyEngine` and audio/composition runtime ownership to framework primitives.
- `ActiveCue` — per-cue runtime (player, `ClipWindow`, layer slots, pausable audio
  sources, `PlaybackClockMaster`).
- `CueCompositionRuntime` — now a HaPlay adapter that acquires output-line VMs and chooses
  CPU/OpenGL compositor backend before handing leases to framework `ClipCompositionRuntime`.
- Pre-roll: `RefreshPreparedCuesAsync`, `BuildPreparedCueKey`, `TryTakePreparedCue`,
  `EvictPreparedExceptAsync`, `CuePreRollCache`, `PreparedCueState`
  (Idle/Preparing/Ready/Stale/Failed).

## 3. Gap analysis — where each guarantee lives today

| Guarantee | Framework today | HaPlay today |
|---|---|---|
| Non-consuming standby | `ClipStandbyEngine` open/seek/hold keyed by `ClipKey` | `RefreshPreparedCuesAsync`, `_prepared`, cache-key match, evict, `PreparedCueState` |
| Explicit start barrier | `IArmedClip.Start()` over a non-started `MediaPlayer` | `deferPlay` + paused audio sources + coordinated unpause |
| Coordinated grouped starts | `StartGroupAsync` arms all, then starts all | `ExecuteGroupAsync` (parallel open, paused, unpause-all) |
| Clip-relative A/V timing | `ClipWindow` + `RetimingVideoOutput` (primitives) | wired in `WireVideoPlacements` + seek to `ClipWindow.Start` |
| Clear output ownership | `ClipAudioOutputRuntime` / `ClipCompositionRuntime` own shared cue output runtimes; `MediaGraph` remains the graph owner vocabulary | HaPlay output-line adapters + conflict release |

The primitives (clip window, retiming) are already framework-level. The **orchestration
with guarantees** is the gap.

## 4. API (in `S.Media.Playback`)

A new, host-agnostic clip-standby surface. Phase 1 intentionally reuses the existing
`MediaPlayerOpenBuilder` family instead of adding a second source abstraction.

### 4.1 Clip source + prepared handle

```csharp
public interface IClipMediaSource
{
    string Description { get; }
    MediaPlayerOpenBuilder CreateOpenBuilder();
}

// What to play and how it's trimmed/placed — host-agnostic, no UI types.
public sealed record ClipSpec(...); // id, source, ClipWindow, host cache key, route descriptors

// A non-consuming standby: opened, seeked to Window.Start, held warm.
public interface IPreparedClip : IAsyncDisposable
{
    ClipKey Key { get; }
    ClipPreparationState State { get; }   // mirrors PreparedCueState
    string? Error { get; }
    // Promote to playing WITHOUT consuming: the engine keeps ownership until Start/Release.
}

public enum ClipPreparationState { Idle, Preparing, Ready, Stale, Failed }
```

`ClipPreparationState` is intentionally the same five states HaPlay's
`PreparedCueState` already uses (incl. the new `Stale`), so the mapping is 1:1.

### 4.2 The standby engine

```csharp
public interface IClipStandbyEngine : IAsyncDisposable
{
    // Non-consuming standby: idempotent on CacheKey; re-prepares on key change (-> Stale).
    Task RefreshStandbyAsync(IReadOnlyList<ClipSpec> window, ClipStandbyPolicy policy, CancellationToken ct);

    event Action<IReadOnlyList<ClipPreparationStatus>> StandbyStatesChanged;

    // Start barrier: take a prepared clip (or open cold), return ARMED-but-not-started.
    Task<IArmedClip> ArmAsync(ClipSpec spec, CancellationToken ct);

    // Drop a prepared standby entry by host id.
    Task RemoveStandbyAsync(string id);

    // Coordinated grouped start: arm all, then release the barrier for all at once.
    Task<IReadOnlyList<IArmedClip>> StartGroupAsync(IReadOnlyList<ClipSpec> specs, CancellationToken ct);
}

public sealed record ClipStandbyPolicy(
    int MaxPreparedDecoders = 6,   // resource cap (now per-list in HaPlay)
    int Window = 4);               // how far ahead to warm
```

`IArmedClip.Start()` is the **explicit start barrier**: arming opens/seeks and does
not start transport. Hosts that own external output runtimes wire those routes between
`ArmAsync` and `Start()`. `StartGroupAsync` arms every clip, then starts them together
so a slow decoder open can't desync the group.

### 4.3 Output ownership

The shared cue-output runtimes now live in `S.Media.Playback`: `ClipAudioOutputRuntime`
owns one audio router per physical audio output, and `ClipCompositionRuntime` owns one
compositor/layer/output fan-out per composition. Hosts pass concrete output leases
(`IAudioOutput` / `IVideoOutput` plus release callbacks) because only the host knows its
device registry. The remaining extraction is to make conflict release ("a new cue takes
an output another cue holds") a graph operation rather than HaPlay's
`ReleaseConflictingPlayerOutputsAsync` callback.

## 5. The five guarantees, made explicit

1. **Non-consuming standby** — `RefreshStandbyAsync` opens/seeks/holds; `ArmAsync`
   *borrows* a prepared clip without disposing it on a failed/aborted start; the engine
   owns it until `Start()` or `Release()`.
2. **Explicit start barrier** — `Arm` (not started) → `Start` (release). No clip ever
   auto-plays on open.
3. **Coordinated grouped starts** — `StartGroupAsync` arms all, releases together.
4. **Clip-relative A/V timing** — `ClipWindow` drives the source seek in phase 1;
   hosts still apply `RetimingVideoOutput` when wiring video layer outputs.
5. **Clear output ownership** — `MediaGraph` owns outputs; the standby engine owns only
   source/route lifetimes; conflict release is a graph operation.

## 6. HaPlay migration

`CuePlaybackEngine` becomes a thin host that:

- builds `ClipSpec`s from `MediaCueNode` (its existing `BuildRoutePlan` +
  `BuildPreparedCueKey` map directly onto `ClipSpec.AudioRoutes/VideoPlacements/CacheKey`),
- delegates standby/arm/group to `IClipStandbyEngine`,
- keeps HaPlay-specific concerns (cue tree, color tags, NDI/PortAudio pre-connect,
  inspector wiring, the `Stale` badge) in the VM layer.

The source-lifecycle part of this migration is implemented: HaPlay now delegates file-cue
standby, live NDI/PortAudio pre-open, arm, stale invalidation, standby removal, and the
first-start barrier to `ClipStandbyEngine`. Output runtime ownership is split: framework
primitives own the shared routers/compositors, while HaPlay still adapts output-line VMs
into concrete leases between `ArmAsync` and `Start()`.

`PreparedCueState` ↔ `ClipPreparationState` is a straight rename; the new per-list
`MaxPreparedDecoders` cap is already shaped like `ClipStandbyPolicy.MaxPreparedDecoders`.

## 7. Other hosts this unlocks

- **Soundboard with video** — `Soundboard` is audio-only today; a `CompositorCueHost`
  on the same engine adds video placement + clip-relative timing with no new orchestration.
- **External cue-player / show controller** — drives `IClipStandbyEngine` directly via
  `CueShowFile`, getting standby + start barrier + grouped starts for free.

## 8. Open questions / risks

- **Threading model.** HaPlay's engine marshals through `Dispatcher.UIThread` in places
  (e.g. reading `SelectedCueList`). A framework engine must be UI-agnostic — host supplies
  specs; the engine never reaches into a VM. This is the largest refactor.
- **Live reconnect policy.** NDI/PortAudio cues now pre-open through standby so trigger
  can arm an already-connected receiver/capture stream. Retry/reconnect behavior after a
  prepared live source disappears remains host policy.
- **Cache-key ownership.** Keep key computation host-side (HaPlay knows comp size,
  output bindings) and pass it in, rather than the engine guessing identity.
- **Memory-estimate eviction** (a deferred backlog sub-item) would slot into
  `ClipStandbyPolicy` later; out of scope here.

## 9. Non-goals

- Not changing cue-relative timeline semantics (see
  `HaPlay-Cue-Composition-Timeline-Semantics.md`).
- Not a stored per-cue downmix matrix (separate backlog item).
- Not replacing `Soundboard`/`CueGraph`/`MediaGraph` — this layers standby/arming under
  them.

## 10. Suggested phasing

1. **Done:** Land `ClipSpec` / `ClipPreparationState` / `IPreparedClip` types + a
   UI-agnostic `IClipStandbyEngine` with default `ClipStandbyEngine`.
2. **Done in framework:** Add the start barrier + `StartGroupAsync` to the interface.
3. **Done in HaPlay:** Reroute file-cue and live NDI/PortAudio source lifecycle onto it
   behind the existing public engine surface (no UI change).
4. **Done in framework + HaPlay adapter:** Extract shared audio/composition runtime
   ownership into framework primitives while keeping host-specific device acquisition in
   HaPlay adapters.
5. Build the video-capable soundboard host as the second consumer (validation that the
   API is genuinely host-agnostic).
6. Fold memory-estimate eviction in, if wanted.
