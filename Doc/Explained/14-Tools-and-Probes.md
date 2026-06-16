# 14 · Tools & Probes

`MediaFramework/Tools/*` are small console programs. They serve two purposes:
**canonical wiring examples** (the cleanest place to see how to assemble the framework
by hand, without HaPlay's UI noise) and **diagnostic probes** (reproduce and verify
the hard timing/format bugs). When you're unsure how to wire something, read the
matching tool — it's the executable spec.

Most are a single `Program.cs`; `VideoPlaybackSmoke` is a small structured host.

| Tool | One-liner | Read it for |
|------|-----------|-------------|
| `VideoPlaybackSmoke` | Full local A/V playback host (file → GL/SDL + PortAudio, optional NDI) | The canonical "wire a `MediaContainerPlaybackBundle` by hand" example |
| `PlaybackSmoke` | Audio file → PortAudio device | Minimal audio path + device enumeration |
| `SoundboardSmoke` | Fire N overlapping voices of one clip | Clip/voice allocation-free playback |
| `CompositorSmoke` | Composite two frames → PNG; or render an output-mapping pattern | GL compositor + warp-mesh pixel correctness |
| `EncoderSmoke` | Decode a clip and re-mux to H.264/AAC | The encode/mux wiring |
| `NDIPlayer` | Play a file out as an NDI source | NDI send wiring |
| `NDIReceiver` | Receive a discovered NDI source | NDI receive wiring |
| `FormatSwitchProbe` | Probe native pixel-format switching | Mid-stream format-change handling |
| `TransportSyncProbe` | Seek/pause cycles with A/V drift + content verification | **The seek-desync probe** |

## `VideoPlaybackSmoke` — the reference host

The most complete example and the one to copy from. It's split into small files so the
wiring reads clearly:

* `Program.cs` — parse args (`PlaybackCli`) and run.
* `VideoPlaybackSmokeHost` / `VideoPlaybackSmokeSession` — build and own the graph: GL/
  NDI/PortAudio outputs, `MediaContainerDecoder`, a `VideoPlayer`, optional
  `VideoRouter`, a freerun/slaved `MediaClock`, all wrapped in a
  `MediaContainerPlaybackBundle` with `SmokeToolDefaultOwnership` so teardown order is
  correct (player → router → clock → decoder).
* `SmokeToolOptions` / `SmokePlaybackOptions` / `SmokePresentationOptions` — the knobs
  (hardware decode on/off, output backend, presentation mode, HDR preference, …).
* `SmokeVideoRouting` — how outputs are added/fanned out.
* `SmokeHud` / `SmokeDefaults` — the `\r` status line (via Core's `PlaybackHud`) and
  defaults.

This mirrors the wiring `MediaPlayer`/`MediaSession` do internally, but in plain sight.
`Doc/MediaFramework-Architecture.md` (the `MediaContainerPlaybackBundle` section) walks
through it step by step.

## `PlaybackSmoke` — minimal audio

```
PlaybackSmoke <audio-file> [--hostapi <substr>] [--device <substr>]
PlaybackSmoke --list
```

Decode an audio file and play it through a chosen PortAudio device. `--list` enumerates
host APIs + output devices; `--hostapi`/`--device` pick by case-insensitive substring
(e.g. `--hostapi JACK --device Scarlett`). The smallest end-to-end audio path:
`AudioFileDecoder` → `AudioRouter` → `PortAudioOutput`.

## `SoundboardSmoke` — voices under load

```
SoundboardSmoke <audio-file> [voice-count] [duration-sec]
```

Fires many overlapping voices of one clip. This is the practical check that
`AudioClipVoice` is allocation-free on the read path ([04](04-Core-Audio-Engine.md)) —
fire dozens of simultaneous one-shots and listen/measure for glitches or GC churn.

## `CompositorSmoke` — compositor & output-mapping correctness

Two modes:

1. **Default:** composite the first decoded frame of a foreground video over a
   background video and write a PNG. Verifies the **GL compositor accepts the layers'
   native pixel formats** (e.g. `yuva444p12le` over `yuv422p10le`) *without* falling
   back to BGRA32 — a real test that the high-bit-depth paths work on the GPU.
2. **`--pattern`:** render a synthetic quadrant pattern through an output-mapping spec
   (affine sections and/or **mesh warp**) on the real GL stack and write a PNG. This is
   the pixel-correctness + measurement harness for the warp-mesh work
   ([10](10-Effects-and-Compositing.md), `Doc/HaPlay-Output-Mapping-Plan.md` §8). No
   media files needed.

> Because it runs the *real* GL stack, this is the tool that catches the "GL-only flip
> passes headless CPU tests but looks wrong on screen" class of bug (project memory:
> composition orientation GL vs CPU). Run it under `xvfb` in CI.

## `EncoderSmoke` — encode wiring

```
EncoderSmoke <input> [output.mp4]
```

Decode a short clip and re-mux/encode to H.264/AAC. The canonical example for
`FFmpegMuxFileOutput` / `FFmpegVideoFileOutput` / `FFmpegAudioFileOutput`
([08](08-FFmpeg-Decode-and-Encode.md)).

## `NDIPlayer` / `NDIReceiver` — network A/V

* `NDIPlayer` (~511 lines) — play a media file out to the network as an NDI source
  (decode → `NDIOutput` send legs). The send-side example.
* `NDIReceiver` — `NDIReceiver <ndi-source-name-substring>`: discover and receive a
  source. The receive-side example (`NDISource` → graph).

## `FormatSwitchProbe` — mid-stream format changes

Probes how the decoder handles a video stream whose pixel format / dimensions change
mid-file (`ProbeNative(label, path)`). Validates the negotiation/reconfigure paths in
`VideoFileDecoder` / `VideoRouter` don't crash or leak when the format switches.

## `TransportSyncProbe` — the seek-desync probe

```
TransportSyncProbe <media-file> [--no-hw] [--cycles N] [--hold-ms N]
                   [--targets s1,s2,...] [--audio-out-rate N] [--verify-content]
```

The diagnostic that exists *because* A/V sync across seeks is delicate
([06](06-Clocks-and-AV-Sync.md), [08](08-FFmpeg-Decode-and-Encode.md)). It runs
repeated seek/pause cycles to a list of target positions and measures A/V drift.

The crucial flag is **`--verify-content`**: it decodes each seek target **once with
hardware decode and once with software decode** and compares the *actual pixel content*
at the landed frame. This is what catches **label-blind seeks** — the bug where a
hardware frame lost its PTS (missing `av_frame_copy_props`) and a keyframe got
mislabeled as the target, leaving audio "ahead." A probe that only trusted timestamps
would miss it; comparing content does not. (Project memory: `hwframe-pts-seek-desync`.)

---

### How these map to the test suite

The `Tools/*` are interactive/observable; the `Test/*` projects
(`S.Media.Core.Tests`, `S.Media.FFmpeg.Tests`, `S.Media.NDI.Tests`,
`S.Media.OpenGL.Tests`, `S.Media.Playback.Tests`, `S.Media.PortAudio.Tests`,
`S.Media.SkiaSharp.Tests`, `OSCLib.Tests`, `PMLib.Tests`, `HaPlay.Tests`) are the
automated xUnit coverage. Several probes have a test counterpart that asserts the same
property headlessly (e.g. the channel-map SIMD paths, the router backpressure, the
compositor CPU path). When changing a hot path, run the matching probe *and* the test
project — the probe shows you the behaviour, the test guards it.

Next: [15 · Issues & Improvements](15-Issues-and-Improvements.md).
