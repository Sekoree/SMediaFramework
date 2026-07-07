# Control, MIDI, and OSC review

Scope: `S.Control.Abstractions`, `S.Control`, `PMLib`, `OSCLib`.

## Assessment

The control subsystem has a useful separation between protocol decoding, device profiles, script execution, state, monitoring, and show actions. Project-relative script loading correctly checks traversal, and Mond execution has an instruction budget. The queue between live input and scripts is nevertheless unbounded, which defeats those protections under sustained input.

## Findings

### CTRL-01 — Script event queue is unbounded (high)

`ControlEventQueue` uses `Channel.CreateUnbounded` with a single reader (`ControlEventQueue.cs:15-25`). Each event allocates a closure, item, linked cancellation state during execution, and `TaskCompletionSource` (`:106-131`). A MIDI/OSC burst, feedback loop, or merely slower script can grow pending memory and end-to-end control latency without a limit.

Impact: memory exhaustion and minutes-old control actions executing after they are operationally relevant. The Mond instruction limit bounds one invocation, not queue growth.

Recommendation: use a bounded channel and define overload by event class. Preserve ordered lifecycle/button edges, coalesce replaceable continuous controls (fader/encoder/meter/value-cache updates), and expose dropped/coalesced counters. Never synchronously block native MIDI/UDP receive callbacks. Add flood tests with a deliberately slow runtime.

### CTRL-02 — Synchronous disposal can wait forever on non-cooperative work (medium)

`ControlEventQueue.Dispose` cancels and then blocks on `_worker.GetAwaiter().GetResult()` with no deadline (`ControlEventQueue.cs:222-239`). Built-in operations receive the shutdown token, but native/plugin/user-script boundaries are not all guaranteed to return promptly.

Recommendation: make `DisposeAsync` the primary path, give shutdown a documented upper bound, and log/abandon safely if a plugin operation violates cancellation. A synchronous adapter should use a bounded wait rather than an unbounded UI-thread block.

### CTRL-03 — Monitor buffer shifts a list on every overflow (medium, performance)

`ControlMonitorBuffer.Record` appends and repeatedly calls `List.RemoveAt(0)` while holding its lock (`ControlMonitor.cs:47-56`). At high event rates this copies the remaining list for every record.

Recommendation: replace it with a fixed-size circular buffer and snapshot in chronological order. Add a benchmark at the configured maximum capacity and expected meter/update rate.

### CTRL-04 — Script security boundary should be explicit (low)

File loading correctly normalizes project-relative paths and verifies that resolved files remain under the project root (`ControlScriptFileHost.cs:27-68, 112-121`). Scripts can still drive network/MIDI/show actions by design. Users need to know that opening an untrusted project grants automation authority.

Recommendation: document projects/scripts as trusted code, show the script sources/capabilities before first activation for downloaded projects, and provide a “disable control scripts on open” preference.

### MIDI-01 — Native MIDI wrapper has tests; device lifecycle still needs platform lanes (low)

PMLib covers message parsing and important managed behavior, and its poll loop uses a wake signal rather than a raw sleep. Actual hot-plug, virtual ports, duplicate device names, shutdown during callback, and PortMidi ABI resolution vary by OS.

Recommendation: retain unit tests and add opt-in Windows/Linux device-loop tests using virtual MIDI where available. Surface PortMidi version/capability in diagnostics.

### OSC-01 — UDP/control overload policy must align with the queue (medium)

OSCLib has codec/server tests and bounded shutdown behavior, but UDP is lossy by nature while the next-stage control queue currently attempts to retain everything. Once `CTRL-01` is fixed, document which OSC messages may be coalesced/dropped and whether bundles retain atomic ordering.

Recommendation: test malformed packets, maximum datagrams, bundle ordering/timetags, sender churn, and receive-flood shutdown. Keep the codec independent from HaPlay semantics.

### AOT-01 — Mond produces a known trim warning (low, tracked risk)

The AOT smoke publishes and runs, but publishing reports `IL2026` from Mond's stack-frame error path. This is currently acknowledged in the desktop project rather than eliminated.

Recommendation: pin and document the accepted warning, add a runtime test of the affected error/stack-trace path in the published AOT smoke, and revisit on Mond upgrades. Do not blanket-suppress unrelated trim warnings.

## What should remain

- The instruction budget is a necessary defense against runaway scripts.
- Project-root containment on `require` is implemented correctly.
- Data-driven device profiles and helper modules are preferable to device-specific branches in the runtime.
- Protocol libraries remain usable independently of HaPlay and `ShowSession`.

