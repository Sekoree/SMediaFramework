# 07 — Migration & Phasing

Strategy (your call): **a fresh parallel solution** built alongside the current one, salvaging code
file-by-file, cutting over at parity. You already started this — the stubbed `S.Media.Time`,
`S.Media.Routing`, `S.Media.Session`, `S.Media.Players`, `S.Media.Compositor`, `S.Media.Gpu`,
`S.Media.Decode.FFmpeg`, `S.Media.Audio.*`, `S.Media.Present.*` projects still have build artifacts on
disk — proof of the intended graph. New code lands in a clean `next/` tree (D1), not those in-place
stub dirs, which are superseded.

## 0. Ground rules

- **`Next/` holds planning; code lives in a new `next/` source tree (D1).** Create `next/` with
  `MFPlayer.Next.sln`; carried-forward modules keep their names where they remain the same module,
  while split/renamed modules use the planned new names. Separate build outputs avoid collisions, so
  the old `MFPlayer.sln` keeps building and shipping releases untouched. No long-lived broken `master`.
- **Parity is defined by the existing tools + tests**, ported first (see §3). A phase is "done" when
  its parity gate is green.
- **Salvage, don't rewrite blank.** Most engine code moves with namespace/dependency edits only. The
  rewrite is in the *seams* (registry, layering, one session), not the math.
- **Every project compiles under `PublishAot`** from day one (CI runs a publish-aot smoke per phase).
- **Delete the global as you go:** nothing in the new tree reads `MediaFrameworkPlugins.*`. If you
  reach for it, you've found something that needs a registry capability instead.

## 1. Salvage map (old → new)

| Today | New project | Action |
|---|---|---|
| `S.Media.Core/Audio/AudioRouter*`, router clocks | `S.Media.Routing` | move |
| `S.Media.Core/Audio/ChannelMap*`, `AudioChannelLayoutPresets`, formats | `S.Media.Core` | keep |
| `S.Media.Core/Clock/*`, `OutputSyncGroup`, `VideoPresentSyncGroup`, `VideoPtsClock` | `S.Media.Time` | move |
| `S.Media.Core/Video/VideoRouter`, `VideoOutputPump`, `Retiming/SyncPresent…` | `S.Media.Routing` | move |
| `S.Media.Core/Video/VideoPlayer`, `Playback/AvPlaybackCoordinator`, `MediaPlaybackSession` | `S.Media.Players` | move |
| `S.Media.Core/Video/*Frame*`, `PixelFormat*`, `*Backing*`, negotiators, contracts | `S.Media.Core` | keep (slim) |
| `S.Media.Core/Diagnostics/MediaFrameworkPlugins/Runtime/ExtensionRegistry` | — | **delete** → registry |
| `S.Media.FFmpeg` / `S.Media.FFmpeg.Encode` shared FFmpeg glue | `S.Media.FFmpeg.Common` | split first: runtime init, native loading, error helpers, stream/format mapping |
| `S.Media.FFmpeg` (decode, hw, swscale, yadif, capture) | `S.Media.Decode.FFmpeg` | move + wrap as module; use `FFmpeg.Common` |
| `S.Media.FFmpeg.Encode` | `S.Media.Encode.FFmpeg` | move + module; use `FFmpeg.Common`, not `Decode.FFmpeg` |
| `S.Media.OpenGL/*` (YuvVideoRenderer, uploaders, interop) | `S.Media.Gpu` | move |
| `S.Media.Effects/*` (compositor, layers, warp) | `S.Media.Compositor` | move + **drop FFmpeg dep (P3)** |
| `S.Media.PortAudio` / `S.Media.MiniAudio` | `S.Media.Audio.PortAudio` / `…MiniAudio` | move + module |
| `S.Media.SDL3` / `S.Media.Avalonia` | `S.Media.Present.SDL3` / `…Avalonia` | move + module |
| `S.Media.NDI` (send+recv) | `S.Media.NDI` | move + module |
| `S.Media.SkiaSharp` | `S.Media.Images.Skia` | move + module |
| `S.Media.Playback/*` (clip composition, standby, soundboard, cue graph, routing scene) | `S.Media.Session` | move + merge |
| `UI/HaPlay/Playback/CuePlaybackEngine`, `HaPlayPlaybackSession*`, `SoundboardEngine` | `S.Media.Session` | **move out of UI (P1)** + de-Avalonia — *superseded by the fresh-built `ShowSession`; retire during the **Phase 8** UI port (re-filed from Phase 4)* |
| `UI/HaPlay/Models/*` (AudioMatrix, OutputDefinitions, ControlGraphConfig, ProjectIO…) | `S.Media.Session` (runtime) + HaPlay (view models) | split: data/runtime down, VM up |
| `S.Control/*` engine | `S.Control` | move |
| `S.Control/X32*`, `XTouch*`, device factories | profiles + `x32.meters` decoder module | convert to data (P6) |
| dynamic plugin host | `S.Abi` | general native C-ABI host for media, compositor, and control capabilities |
| `S.Media.Interop/*` | `S.Media.Interop` | move + retarget to registry |
| `PALib/MALib/PMLib/NDILib/OSCLib/JackLib` | same names | move as-is |
| `Tools/*`, `Test/*` | same | port early as parity gates |
| `MediaFrameworkPlugins` consumers everywhere | registry injection | rewrite call sites |

## 2. Phase plan

Each phase ends on a **green parity gate** (the named tool/test, ported).

| Phase | Deliverable | Parity gate |
|---|---|---|
| **0. Scaffold** | `MFPlayer.Next.sln` under `next/` (D1); empty `Core/Time/Routing/Gpu/Compositor/Players/Session` + module project skeletons with the dependency rules from [01](01-Architecture-and-Principles.md); CI `publish-aot` smoke. | solution builds + AOT-publishes empty |
| **1. Core + Time + Routing** | Slim `Core` (primitives + contracts + **media registry**); move clocks→`Time`, routers→`Routing`. Add `SessionClock`/`SourceTimeline`/`SourceSyncGroup`. | `S.Media.Core.Tests` port green; clocks covered by the ported Clock tests (`TransportSyncProbe` needs the full stack → Phase 3) |
| **2. First end-to-end playback** *(audio-first)* | `FFmpeg.Common` + `Decode.FFmpeg` + `Audio.PortAudio` as modules; `Players.MediaPlayer` receives registry/source-resolver contracts and plays a file's **audio**, synced, via the registry (no globals, no concrete backend refs). Video **decodes** here (`FrameDump`); on-screen present (`Present.SDL3`) + the `VideoPlayer`/`AvPlaybackCoordinator` salvage need `Gpu`/`Compositor` → Phase 3. | `PlaybackSmoke` plays audio + `FrameDump` decodes video at parity; `…FFmpeg.Tests`, `…PortAudio.Tests` (need natives). `VideoPlaybackSmoke` → Phase 3. |
| **3. GPU + Compositor + Players** | Move `Gpu`; move `Compositor` **without FFmpeg dep** (P3 fixed, uses registry converter); add compositor registry extension; add `Present.SDL3` + salvage `VideoPlayer`/`AvPlaybackCoordinator`; `Players` complete (transport, seek, rate, multi-output fan-out). | `VideoPlaybackSmoke`, `TransportSyncProbe`, `CompositorSmoke`, `GlProbe`, `FormatSwitchProbe`, `…OpenGL.Tests` green; compositor builds with FFmpeg **absent** |
| **4. Session (the show)** | Merge `S.Media.Playback` + the UI's `CuePlaybackEngine`/`HaPlayPlaybackSession`/`SoundboardEngine` into headless `S.Media.Session`: cues, soundboard, output mapping (warp sections), routing scene, group-seek barrier. **✅ Framework done (2026-06-25):** Playback merged + `MediaPlayer` decoupled; `ShowSession`/`ShowDocument`; cue→composition + affine output-mapping gated. **Re-filed:** UI god-objects → **Phase 8** (their engine *is* `ShowSession`, built fresh — next/ has no UI yet, so retire during the UI port); multi-track + N→M routing scene → **Phase 5** (registry audio open is single-track + needs multi-output). | `SoundboardSmoke` + `SessionSmoke` (cue fire/seek/go + video composite, headless) + `S.Media.Session.Tests` ✅ |
| **5. Live + multi-out + more backends** | `NDI` (send+recv) + `Audio.MiniAudio` + `Present.Avalonia`; converge live onto `SourceTimeline` + `SourceSyncGroup`; `CompositeMulti` target-domain outputs + sync groups for stitched/combined outputs. | `NDIPlayer`/`NDIReceiver`; multi-output drift soak; live A/V-sync targets from [03](03-AV-Sync-Clocks-Routing.md) §7 |
| **6. Subtitles + Control + plugin host** | `S.Media.Subtitles` (SRT/VTT/ASS/PGS); `S.Control` engine + X32/XTouch as **profiles**; `S.Abi` general native plugin host + a conformance sample plugin covering media and control capabilities. | subtitle render test; `OSCLib.Tests`/`PMLib.Tests`; sample C-ABI plugin loads + provides a source/control decoder |
| **7. Outbound C ABI** | Retarget `S.Media.Interop` (`s_media_player`) to build a registry + drive the new session; keep the ABI stable. | C ABI smoke (open/play/close via `s_media_player.h`) |
| **8. UI port** *(separate effort)* | Rebuild HaPlay as `HaPlay.Core/Controls/App/Desktop` over `S.Media.Session`; decompose god-VMs; strangle the old app workspace-by-workspace. | HaPlay.Tests port; manual workspace parity |

Phases 1–3 are the spine (a file plays with HW decode, GPU compositing, perfect sync, zero globals).
4–5 restore the show/live feature set. 6–7 deliver the new capabilities (subtitles, real plugins) and
the C ABI. 8 is the UI, deliberately last and decoupled.

> **Working checklists:** [09 — Phase Checklists](09-Phase-Checklists.md) has a per-phase `- [ ]` list
> wired to the D#/OQ# decisions, with each phase's gate and exit criteria. Use this table for the *why*
> and 09 for the *do*.

## 3. Parity harness (build this in Phase 0–1, not at the end)

The existing `Tools/*` smoke apps are your spec. Port them onto the new APIs **first** in each phase
so "did I break sync/format/teardown?" is a `dotnet run`, not a guess:
`PlaybackSmoke`, `VideoPlaybackSmoke`, `CompositorSmoke`, `EncoderSmoke`, `SoundboardSmoke`,
`TransportSyncProbe`, `FormatSwitchProbe`, `FrameDump`, `GlProbe`, `NDIPlayer`, `NDIReceiver`. Add
`SessionSmoke` (headless cue/soundboard) in Phase 4. The xUnit suites move alongside their projects.

## 4. The HaPlay shim (so the old app keeps working during 1–7)

Per the scope decision, HaPlay isn't rebuilt until Phase 8 — but it shouldn't block framework work
either. Options, simplest first:

- **Keep old HaPlay on old `MFPlayer.sln`** untouched; develop `Next/` in parallel. Cut HaPlay over
  only in Phase 8. (Cleanest — recommended; no shim needed.)
- *If you want the new engine in the old UI sooner:* add a thin `HaPlayPlaybackSession`-shaped
  façade over `S.Media.Session` so the existing view-models call the new engine with minimal edits.
  More work; only worth it if you need new-engine features in the current UI before the rebuild.

**Never load old + next managed assemblies in one process** (D1 keeps an overlapping managed
generation, so carried-forward assemblies would clash — OQ6). If a transitional process must bridge
old↔next, cross via the `s_media_player` C ABI (native — no managed-identity collision), not by
referencing both managed sets.

## 5. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Live-sync regressions during convergence (Phase 5) | the `SourceTimeline`/`SourceSyncGroup` model is testable headless; gate on the §03 soak targets before declaring parity |
| GPU/interop platform quirks (D3D11↔GL, dmabuf) move with `Gpu` | `GlProbe`/`FrameDump` per platform each phase; keep the existing Windows/Linux HW-decode fixes (memory notes) intact during the move |
| C-ABI plugin surface churn | append-only structs + version gate + conformance sample plugin in CI |
| Session merge (Phase 4) is the biggest single lift (P1) | do it after 1–3 are rock-solid; move file-by-file with `SessionSmoke` green at each step. **✅ Framework side done (2026-06-25)**; the UI-collapse half is firewalled into Phase 8 (the rows below) |
| Scope creep into the UI | Phase 8 is firewalled; framework parity is judged by tools/tests, not the app |

## 6. Definition of done (framework)

- A file plays with HW decode, GPU compositing, and < ±1-frame A/V sync — with **no** static plugin
  state anywhere.
- Live (NDI/mic) composited with a file stays in sync to the §03 targets.
- Mesh-warp/keystone splitting and one-canvas→many-outputs work as one composite pass + N warp passes, with zero readbacks for GPU outputs and one async readback per CPU-bound output.
- Audio remap + multi-track + subtitle selection all work headless.
- A third-party native plugin adds a video source, a GL layer surface, and a control decoder without touching the host.
- `s_media_player` drives the new session; `CompositorSmoke`/`PlaybackSmoke`/`SessionSmoke`/NDI soak
  all green on Windows + Linux.
- `Core` ≈ 6k LOC; product logic exists once; no framework file is a god-object.
