# MFPlayer / HaPlay framework and UI resource review

Date: 2026-07-18

## Executive summary

The framework is stronger than its file count initially suggests. The central audio/video routing paths are
mostly bounded, ownership is usually explicit, and the architecture tests enforce several useful invariants.
I did not find a universal per-frame allocation problem in FFmpeg decode, the audio router, or the main video
output pumps.

I did find several concrete problems outside the excluded third-party trees:

- an NDI audio producer/consumer cursor race that could replay or corrupt overwritten audio;
- a control-monitor bug that permanently froze the visible log once its ring reached capacity, plus two idle
  UI rebuild loops;
- an MPEG-TS rolling history that could grow without bound when a stream stopped producing keyframes;
- two static/full-frame paths producing roughly 105.5 MiB/s of avoidable 720p traffic or allocations;
- two session-lifetime histories that could grow without bound;
- duplicate project serialization and large UTF-8 allocations in the two-second recovery path;
- a newly disclosed vulnerable transitive dependency that also prevented the warnings-as-errors build;
- several smaller polling and API allocation issues.

Those items were fixed as part of this review and are detailed below. A follow-up pass then resolved or
measured every code-actionable item U1–U12 after the overload/API choices were confirmed. In particular, the
general-purpose `SessionDispatcher` is now bounded to a configurable 4,096 pending commands, metadata and
completion polling are consolidated, dormant UI playback code is gone, and unsupported cue fault policies are
rejected rather than silently degraded. The remaining work is hardware/profile validation, not an unresolved
implementation choice.

## Scope and method

I interpreted “except the enternals” as excluding the vendored/third-party `External/` tree. I also excluded
`Reference/` from code-quality findings. Generated `bin/` and `obj/` output was ignored. The review covered the
framework, HaPlay UI/Desktop, interop surface, project/build configuration, and tests as validation material.

The inventory-driven scan covered 951 production/config/resource files, including 791 production C# files
and about 151,350 C# lines. I combined:

- repository-wide searches for queues, timers, `Task.Run`, histories, frame-sized allocation, disposal,
  polling, and duplicated implementations;
- focused ownership/concurrency review of audio, video, session, control, recovery, NDI, HTTP streaming, and
  UI playback paths;
- Release builds and focused/full project test suites;
- three small temporary .NET 10 Release benchmarks using
  `GC.GetAllocatedBytesForCurrentThread` after warm-up;
- a solution-wide NuGet vulnerability audit including transitive packages.

Benchmark host: .NET SDK 10.0.110, Linux 7.1.3 x64, AMD Ryzen AI MAX+ 395 (16 cores / 32 threads), 109 GiB RAM.
The microbenchmarks are useful for allocation direction and order of magnitude; they are not substitutes for an
end-to-end playback profile.

I did **not** have a physical NDI sender/receiver, PortAudio device matrix, or a dedicated GL performance
capture. GPU-upload and MMD bandwidth figures below are therefore exact byte-rate calculations from the frame
formats, not measured bus throughput. Hardware validation is called out in the remaining work.

## Severity convention

- **P1**: correctness, security, or potentially unbounded resource usage; address before relying on the path in
  a long-running show.
- **P2**: material performance/reliability issue with a bounded impact or a scale-dependent design risk.
- **P3**: simplification, diagnostics, or lower-probability hardening.

## Implemented during this review

### F1 — P1: NDI audio overflow could move the read cursor backwards

Affected the standalone receiver and the combined audio/video NDI source:

- `MediaFramework/Media/S.Media.NDI/Audio/NDIAudioReceiver.cs`
- `MediaFramework/Media/S.Media.NDI/NDISource.cs`

Both had private copies of an SPSC float ring. During overflow, the producer wrote `ReadIndex`; during a normal
read, the consumer also wrote `ReadIndex`. This interleaving was possible:

1. consumer captures old read cursor `R` and copies data;
2. producer advances the cursor to discard old data and writes newer samples;
3. consumer stores `R + count`, which can be behind the producer's cursor.

That makes the logical buffered count exceed capacity and can replay overwritten data or produce corrupted
audio. It also violated the framework's own “all interleaved audio rings use `FrameAlignedFloatRing`” design.

Fix: both sources now share `NDIAudioJitterBuffer` in
`MediaFramework/Media/S.Media.NDI/Audio/NDIAudioJitterBuffer.cs`. It delegates cursor motion to the existing
CAS-guarded, frame-aligned ring. A consumer that loses a concurrent discard race returns silence/zero samples
and re-primes instead of publishing discarded data. The change also removed roughly 200 lines of duplicated
ring logic. New NDI tests cover newest-data overflow and rebase/prime behavior.

Follow-up: `NDIAudioReceiver.OverflowSamples` was renamed to `OverflowFloats`, and its receive/timing diagnostic
labels now use `audioOverflowFloats`/`videoOverflowFrames`; see U11.

### F2 — P1/P2: control monitor froze at capacity and rebuilt unnecessarily

Affected:

- `MediaFramework/Control/S.Control/ControlMonitor.cs`
- `MediaFramework/Control/S.Control/ControlValueCache.cs`
- `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.Learn.cs`
- `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.MidiDevices.cs`

The monitor UI used record count as its change detector. Once the 1,000-record ring filled, overwrites left the
count at 1,000, so the visible monitor stopped updating permanently. Before saturation, every changed tick
cleared and recreated up to 1,000 row view models. Even while the monitor was idle, every 250 ms it allocated a
1,000-reference snapshot merely to discover that nothing changed.

The same timer also cleared and recreated the complete X32 command table every 250 ms. Its backing cache was a
normal `Dictionary` written by the background control runtime and read by the Avalonia UI, which is not a safe
concurrent-read/write contract.

Fixes:

- `ControlMonitorBuffer.Version` is a monotonic, allocation-free change token.
- the UI snapshots only after that version changes and reconciles the drop-oldest/append tail while retaining
  overlapping row view models;
- saturated-ring updates are regression tested;
- `ControlValueCache` now uses `ConcurrentDictionary`, exposes stable `Entries` snapshots, and has its own
  mutation version;
- the X32 table updates only cache text whose value actually changed in the common unfiltered view; profile,
  configuration, and cache-sensitive filtered-view changes still perform a structural rebuild.

Monitor snapshot microbenchmark, 1,000 records and 20,000 idle polls:

| Change detector | Allocated per poll | Total time |
|---|---:|---:|
| old `Records` snapshot + count | 8,024 B | 196.01 ms |
| new `Version` check | 0 B | 0.10 ms |

At the actual 4 Hz timer rate, the old snapshot check alone allocated 32,096 B/s (31.3 KiB/s) per armed
workspace. That figure excludes the much larger old X32 and monitor row rebuild costs.

Follow-up: the active snapshot path was measured at 0.043 ms and 33,096 B per refresh with 1,000 records and
two text filters. At 4 Hz this is about 0.17 ms CPU and 132 KiB/s, so the full snapshot was retained rather than
adding rollover/clear complexity to a delta protocol; see U12.

### F3 — P1: MPEG-TS history became unbounded without new keyframes

Affected `MediaFramework/Media/S.Media.Stream.Http/TsFanOutBuffer.cs`.

The intended rolling cap was 8 MiB, but eviction stopped rather than discard the newest keyframe join point.
If an encoder produced a very long GOP or stopped producing keyframes after an error, the buffer retained the
entire remaining stream for the life of the server.

Fix: eviction now always enforces the rolling window (apart from one indivisible chunk). If the remembered
keyframe ages out, the join point becomes invalid and a new client receives no history until another keyframe
arrives. That is preferable to unbounded memory or handing a decoder a known mid-GOP prefix. Capacity is
injectable for tests; a regression test writes repeated non-keyframe chunks and verifies bounded history and no
invalid history for a new reader.

Follow-up: registration now captures immutable history references and the live handoff boundary under the
lock, materializes/coalesces outside it, buffers the bounded live tail during priming, and then publishes the
ordered history+live sequence; see U4.

### F4 — P2: idle static logo re-uploaded unchanged pixels at 30 fps

Affected:

- `UI/HaPlay/Playback/IdleLogoSlateSession.cs`
- `UI/HaPlay/Playback/LogoFallbackVideoOutput.cs`
- `UI/HaPlay/ViewModels/MediaPlayerViewModel.Transport.cs`

The idle slate used a UI `DispatcherTimer` to wrap and submit the same image to every local output at 30 fps.
Both local output implementations already retain the uploaded texture and redraw it when exposed
(`SDL3GLVideoOutput.PresentWithoutNewUpload` and Avalonia's `_hasUploadedOnce` path), so the timer provided no
visual benefit.

Fix: submit the template once on acquisition and let the output redraw its retained texture. The existing
500 ms acquisition retry remains so outputs busy with playback can receive the slate after they become free.
The normal HOLD-off/invalid-path timer branch now exits before route-list and signature allocations.

Avoided input bandwidth per local output:

- 1280×720 BGRA at 30 fps: 110,592,000 B/s = **105.5 MiB/s**;
- 1920×1080 BGRA at 30 fps: 248,832,000 B/s = **237.3 MiB/s**.

This also removes 30 UI wake-ups per second per active slate. It still needs a real SDL/Avalonia expose/resize
smoke test on each target OS.

### F5 — P2: MMD CPU fallback allocated a large object every frame

Affected `MediaFramework/Media/S.Media.Source.MMD/MMDVideoSource.cs` around the CPU renderer's frame emission.

The software renderer correctly keeps a reusable internal pixel buffer, but copied it into a new `byte[]` for
every emitted frame. At the default 1280×720 BGRA/30 fps, this created the same **105.5 MiB/s** of large-object
allocations. At 1080p it would be 237.3 MiB/s.

Fix: the emitted backing now comes from `ArrayPool<byte>` and is returned by the `VideoFrame` release callback.
The copy is still required because the renderer immediately reuses its internal pixels. This affects the CPU
fallback/preview path; the GL surface mode already uses a cached transparent plumbing frame.

### F6 — P2: session-lifetime diagnostic and trigger histories were unbounded

Affected:

- `MediaFramework/Control/S.Control/ControlScriptRuntime.cs`
- `MediaFramework/Media/S.Media.Session/TriggerBindingSet.cs`

A keep-running script that fails for a high-rate MIDI/OSC stream appended diagnostics forever. Likewise,
`TriggerBindingSet` retained every dispatch for the lifetime of a session.

Fix:

- script diagnostics retain a rolling 4,096-entry window, trim in 512-entry batches, and expose
  `DroppedDiagnostics`; sequence-based result extraction preserves current-dispatch diagnostics even across a
  trim;
- trigger dispatch history retains a rolling tail around 512 entries and trims in 64-entry batches.

This deliberately changes these public collections from lifetime histories to recent histories. Callers that
need an audit log should stream events to a bounded/file-backed logger rather than relying on RAM retention.

### F7 — P2: recovery serialized and encoded large projects redundantly

Affected:

- `UI/HaPlay/Models/ProjectHash.cs`
- `UI/HaPlay/Services/SessionRecoveryService.cs`

Every recovery run serialized a project to `json`, then `ProjectHash.Of(snapshot)` serialized the same snapshot
again. Hashing also created full-size UTF-8 arrays for the project and each script. This runs on a two-second
schedule, so large shows produced avoidable allocation and GC pressure even when playback was otherwise idle.

Fix: recovery hashes the already serialized JSON, and `ProjectHash.AppendUtf8` feeds SHA-256 through a 4 KiB
stack buffer. The combined recovery hash uses the same incremental encoder for project JSON and scripts.

Microbenchmark: 500,014-character JSON containing 500,000 non-ASCII `ü` characters (about 1 MiB UTF-8), 100
hash operations:

| Implementation | Allocated per call | Total time |
|---|---:|---:|
| old full `Encoding.UTF8.GetBytes` | 1,000,248 B | 79.38 ms |
| new incremental UTF-8 feed | 506 B | 71.23 ms |

That is about a 99.95% allocation reduction for the hash step, plus recovery no longer performs its second
project serialization. Unicode equality is covered by a new test.

### F8 — P2: hot UI polling allocated completed tasks unnecessarily

Affected the show-session snapshot and soundboard progress polling in `CueShowSessionCoordinator`,
`MediaPlayerViewModel.ShowSession`, and the native interop query.

`ShowSession.SnapshotAsync()` was only `Task.FromResult(Snapshot())`, yet the UI awaited it four or five times a
second. Voice progress had the same pattern. The hot callers now use synchronous lock-free query forms
(`Snapshot()` and `GetVoiceProgress()`). Async compatibility APIs remain for existing consumers.

The snapshot arrays themselves are still real immutable views and still allocate where needed; this change
only removes pointless completed-task/async-state plumbing from the polling path.

### F9 — P1: vulnerable transitive dependency and blocked clean build

`YoutubeExplode` 6.6.0 requests AngleSharp 1.4.0. On 2026-07-17, versions before 1.5.0 became affected by
CVE-2026-54570 / GHSA-pgww-w46g-26qg, an annotation-XML mutation-XSS issue. Because this repository treats
NuGet audit warnings as errors, the Release build failed in six projects.

Fix: `Directory.Packages.props` centrally pins the compatible transitive package to AngleSharp 1.5.0. Restored
assets resolve 1.5.0, and a solution-wide audit of 92 projects now reports zero vulnerable top-level or
transitive packages.

Sources:

- <https://github.com/advisories/GHSA-pgww-w46g-26qg>
- <https://www.nuget.org/packages/AngleSharp/1.5.0>

## Follow-up implementation status (U1–U12)

All code-actionable findings below are now resolved or deliberately retained after measurement. The detailed
U1–U12 sections that follow preserve the original diagnosis and rationale.

| Finding | Final status |
|---|---|
| U1 — dispatcher queue | **Resolved.** `SessionDispatcher` uses a bounded `BlockingCollection` with a default capacity of 4,096. Capacity is constructor-configurable directly and through `ShowSession`. Fire-and-forget work returns `false` when full; awaited work faults with `SessionDispatcherOverloadedException`. Queue depth, capacity, high-water mark, rejection count, name, and disposal state are exposed. Synchronous and asynchronous disposal both drain off-dispatcher and release the queue deterministically. |
| U2 — per-item completion tasks | **Resolved.** One lazy `SessionCompletionMonitor` per `ShowSession` polls all transport groups, soundboard voices, and preview state on the existing 100 ms cadence. Idle sessions have no running delay loop. The per-clip/per-voice `Task.Run` loops were removed. |
| U3 — stale metadata probes | **Resolved.** `ShowSessionMetadataPublisher` serializes fallback/rich publication, tags work with a generation, suppresses stale/disposed results, and bounds work to one active probe plus one latest-wins pending request. A deliberately blocked old probe is regression tested against a newer item. |
| U4 — TS priming under lock | **Resolved.** Registration snapshots immutable history references and join offsets under `_gate`, materializes outside it, stages bounded live chunks while priming, and commits history before live data. A blocking test hook verifies the producer continues while materialization is paused and that byte ordering is preserved. |
| U5 — triple dirty serialization | **Resolved for the identified path.** Dirty notification computes project state once and shares it across `IsProjectDirty`, `HasUnsavedChanges`, and `ProjectTitle`; close/replace decisions reuse the returned value, and `MarkProjectClean` supplies its known-clean result. Outside a notification, the property remains authoritative rather than depending on an incomplete mutation-site revision audit. |
| U6 — dormant playback code | **Resolved.** `BgraConvertingVideoOutput`, `MediaPlayerCompositionRuntime`, `LogoFallbackVideoOutput`, and their dead-only tests were deleted. The production slate path is now the small `StaticSlateVideoOutput`, which owns one template, submits one independently-owned frame to the borrowed async output, and never disposes that borrowed output. |
| U7 — misleading fault policies | **Resolved.** Only `StopShow` and `Continue` are accepted. Direct graph registration, cue-show serialization/deserialization, and `ShowDocumentValidator` reject reserved or unknown numeric values with a cue-specific error. Runtime handling retains a defence-in-depth rejection path. |
| U8 — concentrated responsibilities | **Improved at the risky seams.** Metadata probing and completion polling are separate owned components with explicit teardown; voice completion is consolidated behind `VoicePlayer`; dead UI orchestration was removed. Other large hot-path classes were not split solely for line count, because an arbitrary partial/file split would not improve ownership or performance. |
| U9 — runtime resurrection | **Resolved.** Any registry/bus/compositor access after `MediaRuntime.Shutdown()` logs the ordering bug and throws `ObjectDisposedException`; it cannot build an unowned native host. The real HaPlay smoke launch rendered and completed its normal session-first/runtime-last shutdown successfully. |
| U10 — unconditional Server GC | **Resolved/configurable.** Server GC remains the default, but `-p:HaPlayUseServerGC=false` produces workstation GC. Both generated runtime configurations were built and inspected (`System.GC.Server` false/true respectively). Representative hardware A/B profiling is still recommended before changing the default. |
| U11 — NDI units | **Resolved.** Standalone and combined counters and diagnostic labels consistently say floats for interleaved channel values and frames for video. |
| U12 — coarse active refresh | **Partly optimized, partly retained by measurement.** The X32 common path replaces only changed cache rows and the cache uses allocation-free value-type internal lookup keys. The temporary benchmark measured 0.061 ms/6.7 KB for one-row refresh versus 0.153 ms/125 KB for a full 190-row rebuild. The 1,000-record/two-filter monitor path measured 0.043 ms/33 KB per active refresh, so a rollover-safe delta protocol was not justified at the 4 Hz cap. |

## Original follow-up findings (historical diagnosis)

The sections below describe the state at initial review time. Their implementation status is authoritative in
the table above; they are retained so the reasoning, trade-offs, and original evidence remain available.

### U1 — P1: `SessionDispatcher` is an unbounded retention and latency queue

`MediaFramework/Media/S.Media.Core/Threading/SessionDispatcher.cs:18` constructs a `BlockingCollection` over an
unbounded `ConcurrentQueue`; `PostWork` at line 116 always adds while alive. A device, remote API, plugin, or UI
producer can submit faster than a slow open/reconfigure command completes. Every queued closure and every
awaited call's `TaskCompletionSource` remains live, while command latency grows without limit.

Do not merely add a numeric capacity: define overload semantics per command category. Recommended design:

- coalesce replaceable state setters (gain, seek target, route/mapping updates) by key;
- preserve ordered/lossless lifecycle commands up to a documented bound;
- reject/fault awaited work immediately when full;
- return `false` and increment a visible counter for dropped fire-and-forget work;
- expose queue depth/high-water/dropped diagnostics.

Also, synchronous `Dispose()` only calls `CompleteAdding`; it neither waits for the pump nor disposes the
collection. `DisposeAsync()` does. Either make the synchronous contract explicitly non-draining/internal or
give it deterministic completion. The constructor's `name` argument is currently unused and should be wired
into diagnostics or removed.

### U2 — P2: one polling task/timer is created per active clip or voice

`ShowSession.StartEndMonitor` starts a `Task.Run` loop per active transport clip. `VoicePlayer` does the same for
each soundboard voice and preview. Every loop delays 100 ms and then enqueues a dispatcher query. This is fine
for a few decks, but soundboard/polyphonic use scales tasks, timer registrations, and dispatcher traffic
linearly with voice count.

Replace them with one session-owned completion monitor that iterates the published active group/voice views,
or preferably completion events from the player/source. A single 50–100 ms cadence can retain the existing EOF
stall policy without N independent timers.

### U3 — P1 correctness: stale asynchronous metadata probes can overwrite the current item

`ShowSession.PublishItemMetadata` publishes a fallback immediately, then starts an uncancelled, unversioned
`Task.Run`. A slow probe for item A can complete after item B starts and publish A as now-playing metadata.
Repeated slow probes can also accumulate.

Associate probes with a monotonically increasing item generation and a cancellation token. Before publishing,
verify both generation and source URI still match. If the probe implementation cannot cancel, still suppress
stale publication and bound probe concurrency (for example, latest-wins with one outstanding worker).

### U4 — P2: HTTP client registration performs large allocation under the mux lock

`TsFanOutBuffer.Register` walks history and may allocate/copy up to roughly 8 MiB while holding `_gate`. During
that work, `OnBytes` cannot advance the live stream. Repeated client churn can cause transient allocation/GC
pressure and producer stalls even though client count is capped at 64.

Prefer immutable/ref-counted history segments: capture segment references plus join offset under the lock,
then prime the client outside it while preserving the live handoff boundary. At minimum, add a connection-rate
limit and move coalescing out of the critical section. Note that the rolling cap may exceed its target by one
indivisible source chunk; this should remain documented/tested.

### U5 — P2: dirty-state notification can serialize the complete project three times

`MainViewModel.NotifyDirtyStateChanged()` raises `IsProjectDirty`, `HasUnsavedChanges`, and `ProjectTitle`.
Bindings evaluating those properties can call `IsProjectDirty` three times; each call builds, serializes, and
hashes a complete project snapshot. The hash byte allocation is now much smaller, but snapshot construction and
JSON serialization remain. Large cue shows can therefore hitch the UI on an ordinary edit.

Use a project mutation revision/central dirty flag and cache the computed hash per revision. Recompute the
snapshot only at persistence/close checkpoints or once for a coalesced UI notification. This requires auditing
all mutation sites, which is why it was not changed opportunistically here.

### U6 — P2/P3: approximately 744 lines of UI playback pipeline are dormant or mostly dormant

- `BgraConvertingVideoOutput.cs` (160 lines) is referenced only by its tests.
- `MediaPlayerCompositionRuntime.cs` (191 lines) is referenced only by its tests.
- `LogoFallbackVideoOutput.cs` (393 lines) is used by the idle slate, but most of its live-frame cache,
  opacity, substitution, and restore behavior has no production caller after the ShowSession migration.
- `VideoPlayer.HoldLastFrameAtEnd` contains another repeated full-frame hold path, but no production code sets
  it today.

Tests that instantiate unused production types can make dead code appear supported. Confirm there is no plugin
compatibility promise, then delete the first two types/tests and reduce the logo wrapper to a static-slate
owner. If repeated hold is reintroduced, add an explicit retained-frame output capability rather than blindly
resubmitting CPU pixels.

### U7 — P2 correctness/API: most persisted `CueFaultPolicy` values do not do what they say

The enum exposes `SkipCue`, `HoldLastFrame`, fade, audio-only, video-only, and fallback-output policies, but
`CueGraph.FireEntryAsync` distinguishes only `StopShow`; every other value logs and continues. Comments warn
developers, but a persisted/public policy with a strong behavioral name should not silently degrade.

Implement the policies or reject unsupported values at validation/deserialization and remove them from UI/API
choices until supported. Keeping forward-looking enum members is not worth ambiguous show behavior.

### U8 — P2/P3: large classes concentrate unrelated responsibilities

The largest production files include:

- `CuePlayerViewModel.cs`: 3,037 lines;
- `ShowSession.cs`: 2,696 lines;
- `MediaPlayerViewModel.cs`: 2,439 lines, plus a 1,549-line ShowSession partial;
- `MediaContainerSharedDemux.cs`: 2,234 lines;
- `OutputManagementViewModel.cs`: 1,869 lines;
- `ClipCompositionRuntime.cs`: 1,820 lines;
- `AudioRouter.cs`: 1,764 lines;
- `MainViewModel.cs`: 1,741 lines;
- `GlVideoCompositor.cs`: 1,704 lines;
- `CueShowSessionCoordinator.cs`: 1,693 lines.

Line count is not itself a performance bug, and several hot-path classes are carefully written. It does make
ownership, timer teardown, and allocation regressions much harder to reason about. Prioritize responsibility
splits, not arbitrary file splits: ShowSession document/load, transport, completion monitoring, and metadata;
demux packet ownership, stream selection, and seek; UI view state versus runtime orchestration. Keep immutable
snapshots at those boundaries.

### U9 — P2 hardening: a late registry access intentionally resurrects native runtimes after shutdown

`UI/HaPlay/MediaRuntime.cs:61-72` logs/asserts when `Registry` is accessed after shutdown, then deliberately builds
a fresh host that nothing later disposes. The rationale is avoiding an exit crash, but a straggling timer can
reacquire PortAudio/NDI holds and leak them during in-process restart/test scenarios.

Prefer a disposed sentinel/exception after shutdown, or retain and deterministically dispose any late host.
The better fix is to stop all timers and async producers before `MediaRuntime.Shutdown`; add a shutdown test that
fails on any late registry access.

### U10 — P3: Server GC is unconditional without a representative playback benchmark

`UI/HaPlay.Desktop/HaPlay.Desktop.csproj:39-40` enables concurrent **and Server GC**. Server GC can improve
throughput and reduce some pause shapes, but on a 32-thread workstation it also creates more GC heaps/threads
and can raise idle memory or compete with audio scheduling. The existing comment acknowledges the tradeoff.

Make Server GC a publish/profile choice and compare workstation versus server GC with the same 4K decode,
multi-output, NDI, soundboard, and recovery workload. Capture `dotnet-counters` allocation rate, gen-2 pauses,
heap size, audio underruns, and CPU scheduling. Do not disable it based on theory alone.

### U11 — P3: NDI overflow diagnostic units are inconsistent

The combined source correctly exposes `AudioOverflowFloats`, while standalone `NDIAudioReceiver` exposes
`OverflowSamples` but increments it by interleaved float count. “Samples” is often interpreted as frames per
channel. Rename it to `OverflowFloats` (with a compatibility alias if needed), or expose both floats and frames,
so operators do not misread stereo/5.1 drop metrics.

### U12 — P3: active monitor/cache refresh is bounded but still coarse

The fixed UI now does no monitor snapshot or X32 rebuild while their versions are unchanged. On active traffic,
however, the monitor still copies the complete 1,000-record ring and the X32 table rebuilds in full on any cache
mutation, sampled at 4 Hz.

This is acceptable at current caps, but if meter-heavy X32 use shows UI cost, add `CopySince(sequence)` to the
monitor and update command-row cache text by key instead of rebuilding structural rows. Measure before adding
that complexity.

## Follow-up correctness fix — simultaneous cue groups (resolved)

The `HJ_Test.haplayproj` investigation found a correctness bug outside the original resource findings. HaPlay
correctly mapped all children of an authored cue group to one transport group, but `ShowSession.TransportGroup`
owns one active clip. `Fire all together` therefore opened all children concurrently and then let their commits
replace one another. HaPlay's lifecycle tracker independently kept one active cue per transport group, clearing
the displaced cue's red indicator and Now Playing entry even when media output was still settling.

The simultaneous-fire path now assigns each child a stable per-cue runtime group, arms all decoders in parallel,
waits at a cancellable start barrier until every sibling is ready, and then commits the batch under the normal
fire lock. Authored group IDs remain unchanged for sequential GO/replacement behavior. HaPlay tracks progress and
resolves single/group seeks against the active runtime group; global pause/stop already operate across every
active group. Per-cue cancel still finds clips by cue ID, so cancelling one stem leaves its siblings running.

Regression coverage proves that three same-authored-group clips remain active together, a fast decoder cannot
start before a deliberately delayed sibling is armed, cancellation releases clips waiting at the barrier, both
simultaneous children retain red/current row state, and runtime group IDs are stable and distinct.

## Areas that were already well bounded

The review also found important positive patterns that should be preserved:

- `AudioRouter` publishes immutable route snapshots, reuses source scratch, uses bounded output pumps/pools,
  and applies primary-output backpressure instead of allowing unbounded audio accumulation.
- `VideoOutputPump` uses bounded/drop-oldest ownership and moves conversion/presentation work off submitters.
- `ControlEventQueue` has an explicit 4,096-item bound, coalesces continuous controls, drops with accounting,
  and completes pending awaiters during teardown. It is a useful model for U1.
- NDI video receive queues and HTTP per-client channels are bounded.
- `HttpMediaServer` caps concurrent clients at 64; HaPlay's REST API caps requests at 32 and has request
  cancellation/time bounds.
- recovery is single-flight and offloads serialization/file I/O from the UI thread.
- the rolling file logger uses a bounded channel and batching.
- Release warnings-as-errors, architecture tests, and explicit frame release callbacks catch a meaningful
  class of regressions.

## Validation performed

Final results after the changes:

| Validation | Result |
|---|---|
| `dotnet restore MFPlayer.sln` | passed |
| Release solution build, no restore | passed, 0 warnings / 0 errors |
| solution NuGet audit, transitive included | 92 projects, 0 vulnerable projects |
| Server-GC override build | passed; generated runtime config contained `System.GC.Server: false` |
| default Server-GC solution build | passed; generated runtime config contained `System.GC.Server: true` |
| HaPlay real startup/clean-shutdown smoke | passed (exit 0) |
| `S.Media.Core.Tests` | 616 passed, 2 timing-dependent skipped |
| `S.Media.Arch.Tests` | 21 passed |
| `S.Media.NDI.Tests` | 21 passed |
| `S.Media.Session.Tests` | 143 passed |
| `S.Media.Stream.Http.Tests` | 21 passed |
| `S.Media.Source.MMD.Tests` | 45 passed |
| `S.Control.Tests` | 165 passed |
| `HaPlay.Tests` | 700 passed, 4 pre-existing UI timing tests skipped (dead-code-only tests were removed) |
| `git diff --check` | clean |

One parallel test invocation briefly failed while two MSBuild processes tried to overwrite the same MMD
reference assembly. The identical MMD suite was rerun alone and passed 45/45; this was shared-output build
contention, not a test/product failure.

## Residual validation work

There are no unresolved code-design questions from U1–U12. The remaining confidence work needs representative
hardware/workloads rather than another speculative code change:

1. exercise retained local slates across expose, resize, fullscreen, and display reconnect on each target OS;
2. soak an actual NDI sender/receiver through overflow, format changes, and reconnect while correlating the new
   float/frame diagnostics;
3. profile the same 4K decode, multi-output, NDI, soundboard, and recovery show under default Server GC and
   `-p:HaPlayUseServerGC=false`, capturing allocation rate, heap/Gen-2 pauses, audio underruns, and scheduling;
4. validate PortAudio device hot-plug/default-device changes on the deployment device matrix.
