# Core, Time, and Players review

Scope: `S.Media.Core`, `S.Media.Time`, `S.Media.Players`.

## Assessment

The foundation is one of the stronger parts of the repository. Media capabilities are composed through an immutable registry rather than process-global defaults; frame ownership is explicit; audio/video interfaces are small; clocks and players have substantial deterministic test coverage. The split between source, output, routing, clock, and player responsibilities is understandable.

## Findings

### CORE-01 — Registry disposal is not concurrency-safe (medium)

`MediaRegistry.Dispose` uses an unsynchronized Boolean and public open/factory methods neither check disposal nor lease module lifetimes (`MediaRegistry.cs:102, 120-133, 163-260`). The comment assigns ordering to the host, but the public type otherwise looks safe to share. A concurrent open can race native-runtime teardown, and calls after disposal can invoke providers backed by disposed native state.

Recommendation: make disposal an interlocked state transition, reject capability operations after disposal, and document that opened sources lease or do not lease registry lifetime. Add a `Dispose` versus `TryOpen*` stress test. If strict external serialization is intentional, state it on `IMediaRegistry`, not only in an implementation comment.

### CORE-02 — Builder failure can leak already-registered lifetimes (low)

`MediaRegistryBuilder.Use` invokes a module directly and `MediaRegistry.Build` does not dispose `builder.Lifetimes` if a later registration/configuration step throws (`MediaRegistry.cs:65-76, 136-142`). A native module that registers a lifetime and then fails, or a later module that fails, leaves earlier runtimes initialized.

Recommendation: wrap configuration/build in a rollback that disposes accumulated lifetimes in reverse order. Test with one successful fake module followed by a throwing module.

### TIME-01 — Thread-per-clock/player should be capacity-tested (low, optimization)

`MediaClock` owns an above-normal-priority thread (`MediaClock.cs:254-258`), and each `VideoPlayer` also owns a decode thread (`VideoPlayer.cs:303`). This is defensible for timing isolation, but sessions with many decks/cues can create many OS threads and priority competition.

Recommendation: add a benchmark/soak case at the intended maximum simultaneous clip count and record thread count, context switches, wakeups, and missed deadlines. Consolidate scheduling only if the measurement shows pressure; do not replace deterministic timing with generic thread-pool work without evidence.

### PLAYER-01 — Large player state machine remains costly to change (low)

`VideoPlayer` owns decode threading, seek/flush behavior, clock interaction, output negotiation, and lifecycle coordination. Existing tests mitigate the risk, but the class is still a high-change hotspot.

Recommendation: keep public behavior, but extract an internal decode-loop state object and explicit state-transition helpers. Add tests around transition legality rather than growing branch-level tests only.

## What should remain

- Highest-confidence decoder selection with stable registration-order tie-breaking is simple and deterministic.
- Registry construction at the composition root is preferable to static global registration.
- Explicit `IDisposable` ownership on frames and sources is appropriate for native media buffers.
- The existing clock, allocation, seek, and lifecycle tests are valuable and should remain gating.

