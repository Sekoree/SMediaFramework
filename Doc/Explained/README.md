# MFPlayer — Framework Explained

A guided, in-depth tour of the whole codebase: what every assembly does, how the
pieces fit together, and ELI5 ("explain like I'm 5") breakdowns of the genuinely
tricky parts (A/V sync, the audio mixer, the shared demux, hardware video, the
compositor and output-mapping math).

This folder is **new and additive**. The existing `Doc/*.md` files are the terse,
IDE-hover-companion reference (architecture notes, format matrix, control-surface
layer guides). These "Explained" docs are the long-form narrative you read once to
understand the system, then keep around as a map.

> Scope note: this set was refreshed against the live source on 2026-06-14.
> Where a class carries an authoritative `<summary>`, these docs paraphrase it and
> add context, mechanics, and diagrams. The conceptual chapters explain the hot
> paths; [16 · Type Coverage Appendix](16-Type-Coverage-Appendix.md) names and
> places every production source type.

## How to read this

If you are brand new, read in order: **01 → 06 → 04/05 → 11**. That gives you the
big picture, the clock/sync model (the single most important concept), the audio
and video engines, and the product-facing API. Everything else is reference depth.

| # | Doc | What's inside |
|---|-----|---------------|
| 01 | [Overview & Data Flow](01-Overview-and-Data-Flow.md) | What MFPlayer is, the assembly layering, and the end-to-end path from a file on disk to speakers / screen / network. Start here. |
| 02 | [Native Bindings](02-Native-Bindings.md) | The P/Invoke layers: `PALib` (PortAudio), `NDILib`, `PMLib` (PortMidi), `OSCLib`, `JackLib`. How native libs are resolved, lifetime-managed, and made safe. |
| 03 | [Core Data Primitives](03-Core-Data-Primitives.md) | The values that flow through the graph: `AudioFrame`, `VideoFrame`, formats, pixel formats, `ChannelMap`, `Rational`, hardware backings. |
| 04 | [Core Audio Engine](04-Core-Audio-Engine.md) | `AudioRouter` (the N→M mixer), sources/outputs, clips/voices/buses, pumps, backpressure, SIMD mixing. ELI5 the mixer loop. |
| 05 | [Core Video & Compositor Pipeline](05-Core-Video-Pipeline.md) | `VideoRouter`, `VideoPlayer`, `VideoOutputPump`, format negotiation, deinterlacing, hardware interop, fan-out. |
| 06 | [Clocks & A/V Sync](06-Clocks-and-AV-Sync.md) | **The keystone doc.** `MediaClock`, `IPlaybackClock`, master/slave, drift, the pause-fold, seek desync. Fully ELI5. |
| 07 | [Triggers, Diagnostics & Runtime](07-Triggers-Diagnostics-Runtime.md) | `TriggerBus`, the plugin/extension registry, `MediaFrameworkRuntime`, diagnostics, the playback coordinator/session. |
| 08 | [FFmpeg Decode & Encode](08-FFmpeg-Decode-and-Encode.md) | `MediaContainer`, the shared demux, decoders, resampling, adaptive rate, hardware decode; plus the encode/mux side. |
| 09 | [Output Backends](09-Output-Backends.md) | PortAudio, OpenGL, SDL3, NDI, SkiaSharp, Avalonia — how each turns frames into sound/pixels/packets. |
| 10 | [Effects & Compositing](10-Effects-and-Compositing.md) | The CPU and GL compositors, layers/transforms/transitions, opacity tweens, and the warp-mesh output-mapping math. |
| 11 | [Playback (Product) Tier](11-Playback-Product-Tier.md) | `MediaPlayer` + builders, `MediaSession`/`MediaGraph`/`MediaPlayerController`, `Soundboard`, cue/clip standby, output mapping. |
| 12 | [Control & Scripting](12-Control-and-Scripting.md) | `S.Control`: device profiles, the Mond script runtime, OSC/MIDI bridges, X32/X-Air protocol handling, 14-bit CC. |
| 13 | [HaPlay UI](13-HaPlay-UI.md) | The Avalonia demo app: media player, cue player, soundboards, control workspace, REST API, output preview runtimes. |
| 14 | [Tools & Probes](14-Tools-and-Probes.md) | The smoke tools and diagnostic probes (`VideoPlaybackSmoke`, `TransportSyncProbe`, `CompositorSmoke`, …) and what each verifies. |
| 15 | [Issues & Improvements](15-Issues-and-Improvements.md) | Honest findings: bugs, smells, refactor/optimization opportunities (including API-breaking ideas), ranked by impact. |
| 16 | [Type Coverage Appendix](16-Type-Coverage-Appendix.md) | A source-derived inventory of all production classes, records, structs, enums, and interfaces, with chapter ownership links. |
| 17 | [NativeAOT C-ABI](17-NativeAOT-C-ABI.md) | `S.Media.Interop`: the flat `mfp_*` C API (NativeAOT shared library) for driving the framework from other languages, with the GC isolated behind the boundary. |

## The 30-second mental model

```
            decode            mix / route          pace & convert        present
  file ───► MediaContainer ───► AudioRouter  ───────────────────────────► PortAudio / NDI (sound)
   │         (shared demux)     VideoRouter   ───► VideoPlayer/Pump ─────► OpenGL / SDL3 / NDI / Avalonia (picture)
   │                                ▲
   └──────────────────────────► MediaClock ◄─── slaved to the audio output's real sample count
                                (the conductor)
```

* **Sources** *produce* frames (decoders, NDI receivers, capture devices).
* **Routers** *connect* sources to outputs (audio sums many→one; video fans one→many).
* **Outputs** *consume* frames (devices, windows, network senders, file muxers).
* **The clock** is slaved to whatever output owns the real hardware crystal (usually
  the audio device) so picture and sound stay locked to *heard* audio, not wall time.

Everything else — cues, soundboards, the compositor, the control surface, the UI —
is built on top of those four roles.
