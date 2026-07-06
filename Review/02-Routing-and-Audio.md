# Routing and audio review

Scope: `S.Media.Routing`, `S.Media.Audio.PortAudio`, `PALib`, `S.Media.Audio.MiniAudio`, `MALib`.

## Assessment

The routing layer has good real-time instincts: bounded video pumps, drop-oldest behavior, conversion outside the main router lock, explicit clock/master-output concepts, and dedicated native wrapper projects. The most serious issue is lock/callback composition under overload. Backend coverage is uneven: PortAudio has focused tests, while MiniAudio/MALib has no dedicated test assembly.

## Findings

### ROUTE-01 — Pump callback occurs under a lock and forms an ABBA path (high)

`VideoOutputPump.Submit` holds `_gate` while dropping a frame and invoking `RaisePumpPressure` (`VideoOutputPump.cs:218-241, 308-309`). External event handlers therefore run in the hot submit critical section. Separately, `VideoRouter.TryGetVideoOutputPumpMetrics` holds the router `_gate` and reads `pump.CurrentQueuedDepth`, which locks the pump (`VideoRouter.cs:475-493`). A pressure subscriber that calls back into a router API creates the reverse order: pump lock → router lock versus router lock → pump lock.

Impact: deadlock or prolonged frame-submit stalls precisely when the system is overloaded.

Recommendation: collect the pressure event payload under the pump lock and invoke subscribers after releasing it. In the router, snapshot the pump reference under the router lock and read pump counters outside it. Add a stress test whose pressure subscriber queries metrics/removes an output while another thread polls metrics.

### ROUTE-02 — Output code executes while the global router lock is held (medium)

The one-output fast path calls `Output.Submit` inside `_owner._gate` (`VideoRouter.cs:798-815`), and fan-out delivery also submits every output under that lock (`VideoRouter.cs:906-939`). The default pump makes this normally brief, but the API permits synchronous outputs and subscriber/native implementations. One slow output can block route changes, metrics, and all deliveries.

Recommendation: introduce an output registration lease/generation. Snapshot targets under lock, submit outside it, then safely retire removed outputs after active leases drain. At minimum, state and enforce that router outputs must be non-blocking pumps.

### AUDIO-01 — Backend parity is not demonstrated (medium)

PortAudio/PALib has a dedicated test project, while MiniAudio/MALib has no equivalent. CI launch checks that would exercise actual native discovery are best-effort. This leaves format negotiation, enumeration failure, callback shutdown, underrun accounting, and repeated initialize/dispose behavior less protected for MiniAudio.

Recommendation: define a backend conformance test suite and run it against fake/native adapters for both implementations. Keep device-dependent cases tagged, but gate resolver, format, lifecycle, and error-translation tests without hardware.

### AUDIO-02 — Native error handling needs an explicit observability contract (low)

Several wrapper initialization/probe paths intentionally catch native-load exceptions to allow optional backends. That behavior is suitable for a modular host, but a library consumer needs a structured way to distinguish “not installed,” “installed but incompatible,” and “device open failed,” rather than relying on logs or empty device lists.

Recommendation: expose backend availability/diagnostic records from module registration and surface them consistently in HaPlay. Avoid making optional-backend probing fatal.

## What should remain

- **The audio router does not share ROUTE-01 (verified 2026-07-06).** The audio path raises pump
  pressure from `AudioRouter.OutputPump.RecordDrop` (`AudioRouter.OutputPump.cs:117-122`), reached via
  `Commit`, which operates on lock-free `ConcurrentQueue`/`BlockingCollection` buffers — no lock is held
  when the subscriber callback runs. ROUTE-01 is genuinely video-pump-specific; do not wrap the audio
  path in a lock "for symmetry" while fixing it. (One residual: the callback still runs on the
  above-normal-priority mix thread, so a slow subscriber can glitch audio — keep pressure subscribers
  cheap.)
- Bounded video queues and explicit dropped-frame counters are correct defaults.
- Conversion being staged outside the global router lock is a sound design.
- Native bindings being isolated in PALib/MALib keeps higher layers testable.
- Audio master/slave clock concepts and channel-matrix support are appropriate for the target use case.

