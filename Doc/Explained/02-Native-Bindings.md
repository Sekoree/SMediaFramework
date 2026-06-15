# 02 · Native Bindings

These are the low-level P/Invoke libraries. They have **no dependency on the
framework** — they're standalone wrappers over a C library, usable on their own.
The framework's backend packages (e.g. `S.Media.PortAudio`) sit *on top* of them
and adapt them to the `IAudioOutput`/`IVideoOutput` world.

| Lib | Wraps | Used by |
|-----|-------|---------|
| `PALib` | PortAudio (cross-platform audio I/O) | `S.Media.PortAudio` |
| `NDILib` | NewTek/NDI SDK (network A/V) | `S.Media.NDI` |
| `PMLib` | PortMidi (MIDI I/O) | `S.Control` |
| `OSCLib` | Open Sound Control (pure C#, UDP) | `S.Control` |
| `JackLib` | JACK2 audio server | (optional pro-audio routing) |

## The shared "make native loading boring" pattern

Every binding lib that loads a native `.so`/`.dll`/`.dylib` uses the **same three
pieces**, and it's worth understanding once because it's repeated five times:

1. **`*LibraryNames`** — the per-platform candidate file names. PortAudio, for
   example, tries `portaudio` then `libportaudio.so.2` on Linux. This handles the
   reality that distros name the same library differently.

2. **`*LibraryResolver`** — installs a `NativeLibrary.SetDllImportResolver` for the
   assembly that walks the candidate names until one loads. It can also attach an
   `ILogger` so failures are diagnosable. This is *the* thing that makes the bare
   `[DllImport("portaudio")]` calls work regardless of the actual filename.

3. **`*LibModuleInit`** — a `[ModuleInitializer]` that calls `Resolver.Install()`
   *exactly once, before any other code in the assembly runs* (including static
   constructors). That guarantees the resolver is registered before any `Native.*`
   P/Invoke can fire — you never have to remember to initialize anything.

```csharp
[ModuleInitializer]
internal static void Initialize() => PortAudioLibraryResolver.Install();
```

So: **add the package, the native call just works** (assuming the OS has the
library installed). That's the whole point. The `Install()` methods are also public
so a host can re-call them at startup to attach a real logger.

---

## PALib — PortAudio

PortAudio is the cross-platform audio device library. PALib is a thin, faithful
binding split by host API so you only pull in what you use.

* `Native.cs` — the core `Pa_*` entry points (init, open/close stream, start/stop,
  device enumeration, callback registration).
* `Types/Core/` — `PaConstants`, `PaEnums`, `PaStructs`: the device-independent
  structs (`PaStreamParameters`, `PaDeviceInfo`, sample formats, error codes).
* **Host-API submodules**, each its own `Native.cs` + types: `ALSA`, `ASIO`,
  `DirectSound`, `JACK`, `WASAPI`, `WDMKS`, `WMME`. These expose the host-specific
  extension structs (e.g. `PaWasapiStreamInfo`, `PaAsioStreamInfo`) so a host can
  request exclusive mode, channel masks, etc.
* `Errors/PaErrorHelpers` — turns a `PaError` code into a readable message.
* `Runtime/PALibLogging` + the resolver/module-init trio described above.

PALib does *not* manage lifetime or expose a stream object — that's the job of the
framework package on top (`PortAudioOutput`, `PortAudioInput`, `PortAudioRuntime`,
see [09 · Output Backends](09-Output-Backends.md)). PALib's contract is "call the
C API safely from C#."

---

## NDILib — the NDI SDK wrapper

This is the most complete binding in the repo (`NDIWrappers.cs` alone is ~1,550
lines) and it's a *full* NDI surface, not just the bits the framework needs. NDI
("Network Device Interface") sends uncompressed/low-latency A/V over a LAN.

Lifetime: **`NDIRuntime` must be created before any other NDI object and disposed
last** — it owns the global `NDIlib_initialize`/`destroy`.

The managed wrappers (`NDIWrappers.cs`) are organized around the SDK's objects:

* **`NDIFinder` / `NDIFinderSettings`** — discovers sources on the network
  (`NDIDiscoveredSender`/`Receiver` records describe what it finds).
* **`NDIReceiver` / `NDIReceiverSettings`** — connects to a source and captures
  video/audio/metadata. `NDICaptureScope` is a `using`-disposable that auto-frees
  the captured native buffer.
* **`NDISender`** — publishes a source to the network.
* **`NDIFrameSync`** — a time-base corrector that converts NDI's push model into a
  pull model, dynamically resampling audio to the host clock. (Relevant to the
  drift discussion in [06](06-Clocks-and-AV-Sync.md).)
* **`NDIRouter`** — a virtual source that transparently redirects connected
  receivers to another source (switcher workflows, no reconnect needed).
* **`NDIAudioUtils`** — converts between NDI's native planar FLTP and interleaved
  formats (wraps `NDIlib_util_*`). This is what the framework uses to turn received
  planar float into the packed-float the `AudioRouter` speaks.
* **Discovery-server types** (`NDIRecvAdvertiser`/`Listener`, `NDISendAdvertiser`/
  `Listener`) — centralized monitoring/control via an NDI Discovery Server.
* **Structs** mirror the SDK 1:1: `NDISendCreate`, `NDITally`, `NDIRecvPerformance`,
  `NDIRecvQueue`, the interleaved audio frame structs (`NDIAudioInterleaved16s/32s/32f`),
  etc. `NDIConstants` mirrors the SDK's compile-time defines.

`Errors/NDIErrorCode` enumerates creation/init failures (e.g. runtime not found).

> Note the layering: NDILib speaks the SDK; `S.Media.NDI` (doc 09) adapts
> `NDISender`/`NDIReceiver` into `IVideoOutput`/`IAudioOutput`/`IVideoSource` and
> adds the ingest clock.

---

## PMLib — PortMidi

PortMidi is a small cross-platform MIDI I/O library. PMLib wraps it and adds a
*strongly-typed message layer* on top, which is the nicest part.

**Thread safety is enforced centrally.** `PmNativeGate` serializes *all* PortMidi
native calls — PortMidi is not thread-safe, and poll threads, output writers, and
shutdown must never overlap. The gate is deliberately *not* held while invoking
managed event handlers (so a slow handler can't deadlock the MIDI subsystem).
`Native.cs` is `internal`; **`PMUtil` is the public entry surface.**

**Devices:**

* `MIDIDevice` (base) → `MIDIInputDevice` / `MIDIOutputDevice`. The input device
  polls on a background thread and raises strongly-typed message events; the output
  device writes messages.
* `Devices` resolve via `PmDeviceEntry` (a *fully-managed snapshot* of device
  metadata — strings are eagerly copied from native memory so it stays valid after
  the native info is freed).

**Strongly-typed messages** (`MessageTypes/`) — every MIDI message is a struct with
proper semantics, not a raw byte triple:

* Channel voice: `NoteOn`, `NoteOff`, `ControlChange` (supports 7-bit *and* 14-bit),
  `ProgramChange`, `PitchBend` (inherently 14-bit, −8192..+8191), `ChannelAftertouch`,
  `PolyphonicAftertouch`.
* Composite: `NRPN` / `RPN` (14-bit param + 14-bit value across multiple CCs),
  `SysEx`, `MIDITimeCode` (quarter-frame SMPTE), `SongPosition`/`SongSelect`.
* System real-time: `TimingClock`, `MIDIStart`/`Continue`/`Stop`, `ActiveSensing`,
  `MIDIReset`, `TuneRequest`.
* `MIDIMessageParser` decodes raw `PmEvent`s into these; `MIDIMessageType` is the
  discriminant.

**Accumulators** reassemble multi-message values as they arrive byte-by-byte:

* `HighResCCAccumulator` — pairs coarse CC (0–31, MSB) with fine CC (+32, LSB) into
  one 14-bit value.
* `NRPNAccumulator` — assembles the multi-CC NRPN/RPN sequences.
* `MIDISysExAccumulator` — stitches SysEx split across multiple `PmEvent`s.

**`MidiTriggerBridge` + `MidiTriggerProfile`** map incoming MIDI to framework
`TriggerBus` ids (see [07](07-Triggers-Diagnostics-Runtime.md)) — this is the seam
between "a MIDI controller moved" and "the framework did something."

> The 14-bit handling here matters: a high-resolution fader physically arrives as
> two separate CC bytes, and the framework recombines them. `S.Control` builds on
> this with profile-driven combining (see [12](12-Control-and-Scripting.md)).

---

## OSCLib — Open Sound Control

A **pure C#** OSC implementation (no native dependency — it's just UDP + a binary
codec). OSC is the protocol mixers and lighting desks speak.

* **Packets:** `OSCPacket` (top-level), `OSCMessage` (address + typed args),
  `OSCBundle` (timetag + nested packets). `OSCPacketKind` discriminates.
* **Codec:** `OSCPacketCodec` does the wire encode/decode (the big one, ~715 lines —
  OSC's type-tag system and alignment rules are fiddly). `OSCDecodeOptions` controls
  strictness; unknown args can be preserved as `OSCUnknownArgument` in non-strict mode.
* **Args:** `OSCArgument` is a compact tagged union; `OSCArgumentType` is the logical
  type; specials include `OSCTimeTag` (NTP) and `OSCMIDIMessage` (the OSC `m` tag).
* **Transport:** `OSCClient`/`IOSCClient` (UDP send) and `OSCServer`/`IOSCServer`
  (UDP receive), with `OSCClientOptions`/`OSCServerOptions`. Oversize datagrams are
  handled per `OSCOversizePolicy` (or throw `OSCPacketTooLargeException`).
* **Routing:** `OSCRouter` dispatches received messages to handlers registered by
  address pattern; `OSCAddressMatcher` does the matching; `OSCBundleScheduler`
  honors bundle timetags (deliver-at-time).
* **`OscTriggerBridge`** maps inbound OSC messages to `TriggerBus` ids using the
  address as the id — the OSC analogue of `MidiTriggerBridge`.

---

## JackLib — JACK2

A binding for the JACK audio server (pro-audio, sample-accurate routing between
apps). Optional/peripheral in this codebase.

* `Native.cs` — raw `internal` `jack_*` P/Invoke.
* **`JackClient`** — a managed RAII wrapper: client lifecycle, GC-rooting of
  callback delegates (so the GC can't collect a delegate the C side still calls),
  port registration, and connection management (including autoconnect to physical
  outputs).
* `JackException`, `JackLogging`, and the enums/flags (`JackOptions`, `JackStatus`,
  `JackPortFlags` — note these are 64-bit `unsigned long` on Linux LP64,
  `JackLatencyCallbackMode`) plus `JackPortType` constants.

JackLib follows the same naming-candidates idea (`JackLibraryNames`): on Linux the
package provides `libjack.so.0` and the bare name `libjack` resolves via the normal
search order; macOS uses Homebrew's `libjack.dylib`.

---

### Why bindings are separate from backends

Keeping `PALib`/`NDILib`/`PMLib` independent of the framework means:

* They can be tested in isolation (and they are — `OSCLib.Tests`, `PMLib.Tests`).
* The framework depends on a *small adapter* per platform, not a giant SDK surface.
* You can swap or upgrade the binding without touching engine code.

Next: [03 · Core Data Primitives](03-Core-Data-Primitives.md) — the frames and
formats that travel through the graph.
