# MFPlayer "Next" — Rewrite Plan

> A complete, API-breaking rewrite of the media framework (and, in a later phase, the
> HaPlay UI). This directory is the **planning** for that rewrite. No code here yet.

Today's codebase works and is feature-rich, but a few years of "Claude/Codex, can you add X?"
has left it **overengineered in the places that matter**: product logic duplicated between the
framework and the UI, process-wide mutable plugin state, a compositor that secretly depends on
the decoder, god-objects in the UI, and device-specific protocol code baked into the control
surface. The engine ideas are sound — the *packaging* is not.

You already started this exact restructure once (the stubbed `S.Media.Time`, `S.Media.Routing`,
`S.Media.Session`, `S.Media.Players`, `S.Media.Compositor`, `S.Media.Gpu`, `S.Media.Decode.FFmpeg`,
`S.Media.Audio.*`, `S.Media.Present.*` projects still have build artifacts on disk). This plan
**formalizes and finishes that direction.**

---

## The three decisions that shaped this plan

| Decision | Choice |
|---|---|
| **Scope** | Framework first. HaPlay keeps running on a thin shim and is ported workspace-by-workspace afterward (see [07](07-Migration-and-Phasing.md)). |
| **Plugins** | Two tiers, one dynamic mechanism: **(A)** AOT-pure typed registration into scoped registries, **(B)** a general **native C-ABI plugin** host (`S.Abi`) for third parties — any NativeAOT/C/C++/Rust `.so`/`.dll` exposing the plugin C API. No managed reflection/ALC loading. See [05](05-Plugin-Model.md). |
| **Migration** | Fresh parallel `MFPlayer.Next.sln` built alongside the current one; salvage code file-by-file; cut over at parity. See [07](07-Migration-and-Phasing.md). |

Three further architecture blockers are now locked: new source lives in a `next/` subtree, the master
clock is **per transport group**, and the GPU layer shares **one GL context per render thread** (cross-
boundary zero-copy via exported external images) with a GL-explicit plugin ABI. Those plus the remaining open items (with recommended defaults) live in the ledger,
**[08 — Open Decisions](08-Open-Decisions.md)**.

---

## Reading order

1. **[01 — Architecture & Principles](01-Architecture-and-Principles.md)** — what's wrong today, the design rules, the layered dependency graph.
2. **[02 — Project Structure](02-Project-Structure.md)** — every project in the new solution: responsibility, dependencies, and what salvages into it.
3. **[03 — A/V Sync, Clocks & Routing](03-AV-Sync-Clocks-Routing.md)** — the one sync model for file + live, multi-input, multi-output.
4. **[04 — Compositor, Warp & GPU](04-Compositor-Warp-GPU.md)** — layers, transforms, mesh warp/splitting, multi-output-from-one-canvas, GPU layer-surface plugins.
5. **[05 — Plugin Model](05-Plugin-Model.md)** — scoped typed registries + the native C-ABI plugin contract.
6. **[06 — Control Surface](06-Control-Surface.md)** — MIDI/OSC/Mond scripting, data-driven device profiles.
7. **[07 — Migration & Phasing](07-Migration-and-Phasing.md)** — fresh solution, phase plan, salvage map, parity gates.
8. **[08 — Decisions Ledger](08-Open-Decisions.md)** — locked cross-cutting decisions plus follow-up open questions.

---

## Target picture at a glance

```
                         ┌───────────────────────────────────────────────┐
   Native wrappers       │  PALib MALib PMLib NDILib OSCLib LibAssLib (JackLib) │ ← KEEP/extend (P/Invoke + OSC)
   (+ NuGet: FFmpeg.AutoGen, SDL3-CS, Silk.NET.OpenGL, SkiaSharp)         │
                         └───────────────────────────────────────────────┘
                                              ▲
   ┌──────────────────────────────────────────────────────────────────────────────┐
   │ S.Media.Core      pure primitives + contracts + media registry                  │  Tier 1
   ├──────────────────────────────────────────────────────────────────────────────┤
   │ S.Media.Time      clocks / sync        S.Media.Gpu     GL device + shaders     │  Tier 2
   │ S.Media.Routing   audio+video routers                                          │
   ├──────────────────────────────────────────────────────────────────────────────┤
   │ S.Media.Compositor   layers · transforms · mesh warp · multi-output            │  Tier 3
   ├──────────────────────────────────────────────────────────────────────────────┤
   │ BACKEND MODULES (plugins, build-time):                                         │  Tier 3b
   │   FFmpeg.Common · Decode.FFmpeg · Encode.FFmpeg · Audio.PortAudio              │
   │   Audio.MiniAudio                                                              │
   │   Present.SDL3 · Present.Avalonia · NDI(send+recv) · Images.Skia · Subtitles   │
   ├──────────────────────────────────────────────────────────────────────────────┤
   │ S.Media.Players   single-stream playback (file / live → sync → outputs)        │  Tier 4
   ├──────────────────────────────────────────────────────────────────────────────┤
   │ S.Media.Session   cues · soundboard · output mapping · show orchestration      │  Tier 5
   ├──────────────────────────────────────────────────────────────────────────────┤
   │ S.Control   MIDI/OSC/Mond + control profile/capability registry               │  Tier 6
   ├──────────────────────────────────────────────────────────────────────────────┤
   │ S.Abi   general native C-ABI plugin host  ·  S.Media.Interop   outbound C ABI  │  Tier 7
   └──────────────────────────────────────────────────────────────────────────────┘
                                              ▲
   HaPlay (Avalonia)  — rebuilt later as Core/Controls/App/Desktop, thin MVVM over Session
```

**One rule above all:** dependencies only ever point **down**. `Core` knows nothing about FFmpeg,
GL, SDL, PortAudio, or NDI. Every backend is a peer behind an interface, and any of them can be
absent — capability follows from which modules are registered.

---

## What this rewrite must preserve (non-negotiable feature parity)

- Perfect A/V sync across **multiple inputs** (file + live: NDI, mic/line capture) and **multiple outputs**, including grouped A/V streams from one live sender.
- Hardware acceleration end-to-end: GPU decode, shader-level pixel-format handling, GPU compositing.
- Compositions: layers (video/image/text), 2D transform/zoom/warp, opacity, blend, transitions.
- **Mesh warp + splitting** at both the **composition** and **output** level (Catmull-Rom / corner-pin).
- **Multiple outputs combined in one composition layout** (one canvas → N warped, phase-locked outputs).
- Audio: easy channel remapping (N→M matrix) and **multi-track selection** (none / one / many).
- MIDI + OSC scripting (Mond) with device automations / compatibility profiles.
- Both local video outputs — **SDL3** (own thread, performance) and **Avalonia GL** (embeddable).
- Both **PortAudio** and **miniaudio** audio I/O — as optional modules.
- **NDI** input + output — as an optional module.

## What this rewrite adds

- **Subtitles** — SRT/WebVTT/ASS-SSA/PGS, selectable like audio tracks, rendered as composited layers.
- **Real modularization / plugins** — see decision table above.
- **Simplicity** — one home for product logic, no global mutable state, no leaky couplings, smaller files.
