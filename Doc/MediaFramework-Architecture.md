# MediaFramework architecture

The current (v2) framework in one page. Deeper discussion lives in the XML `<remarks>` on the
orchestration types; this is the map.

## Layering

Dependencies point strictly downward (enforced by `S.Media.Arch.Tests`):

```
S.Media.Core            frames, formats, clocks, buses, rings, diagnostics, registry contracts
  ↑ S.Media.Time        clocks, session timelines, output sync groups
  ↑ S.Media.Routing     AudioRouter (multi-route mixing, shared outputs, channel maps)
  ↑ S.Media.Players     MediaPlayer / playback sessions (decode ↔ routing ↔ sync)
  ↑ S.Media.Session     ShowSession (document-like show topology reconciled with live runtime)
S.Media.Compositor      layered CPU/GPU composition, placements, output mapping/warp
S.Media.Decode.FFmpeg   shared demux, decode queues, seek/epoch handling
S.Media.Encode.FFmpeg   encode sessions (recording/streaming), bounded per-leg queues
backends/sources        Audio.PortAudio, Audio.MiniAudio, NDI, Present.SDL3(+Compositor),
                        Present.Avalonia, Subtitles, Source.{Text,MMD,YouTube}, Visualizer.ProjectM
bindings (leaf)         PALib, MALib, PMLib, NDILib, OSCLib, LibAssLib, ProjectMLib
```

## Core ideas

- **Explicit registry, no globals.** `MediaRegistry.Build(b => b.Use(new FFmpegModule())…)` is the
  composition root; modules register decoders/backends and load their natives there.
- **Ownership is explicit.** Frames, sessions and outputs have a single owner; `Dispose` order is a
  contract, documented on the type. Queues are bounded; overflow policies (drop-oldest/newest) are
  chosen per seam and counted in diagnostics.
- **Whole-frame audio rings.** All backend rings share `FrameAlignedFloatRing` (S.Media.Core), which
  guarantees interleaved frames are never split for ANY channel count (1–32 supported).
- **Clocks discipline outputs.** The audible position (device latency subtracted) is the master;
  video presents against it. Multi-output sync composes through one master playhead
  (see [HaPlay-MultiOutput-Sync.md](HaPlay-MultiOutput-Sync.md)).
- **ShowSession reconciles documents.** The cue player edits a document (cues, clips, compositions,
  routes); `LoadDocumentAsync` diffs it against live resources, optionally preserving matching
  compositions so visualizers/clips survive edits. All session mutation is serialized on one
  dispatcher.
- **Health is built in.** Endpoints publish health snapshots and counters (overruns, drops, late
  frames); HaPlay's I/O workspace and the REST status endpoint surface them.
- **NativeAOT is a real constraint.** Trim/AOT analyzers run on every library
  (see [NativeAOT.md](NativeAOT.md)); `S.Media.Interop` publishes the whole framework as the
  `s_media_player` C ABI, and `S.Abi` loads inbound C plugins.

## Native boundary policy

Each binding has an explicit acceptance policy (see [Native-Dependencies.md](Native-Dependencies.md)):
stable-soname libraries (PortAudio, libass) accept compatible system builds; layout-sensitive
libraries (miniaudio — hand-mirrored ABI) accept only the exact pinned version; modified builds
(projectM with the bound-FBO patch) prefer the app-local/dev build and reject unusable variants
(e.g. GLES-only system builds). Release artifacts prove versions at build time via
`scripts/check-native-versions.sh`.

## Platform policy

Linux is the primary target, Windows is supported, macOS is best-effort portability only (resolver
branches exist but are untested). See [Release-Tiers.md](Release-Tiers.md) for what each artifact
tier promises.
