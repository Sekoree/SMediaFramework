# MediaFramework public API map

Post-refactor v2 surface (May 2026). Obsolete `TryOpen*` / `TryOpenFile` shims on `MediaPlayer` remain for compatibility; prefer builders (`MediaPlayer.OpenFile(path).TryBuild()` / `OpenAsync()`).

## Shipping assemblies

| Assembly | Role | Introduced |
|---|---|---|
| `S.Media.Core` | Audio/video graph, clocks, frames, compositor, triggers | baseline |
| `S.Media.FFmpeg` | Decode, demux, swscale, adaptive rate, `MediaContainer` | baseline |
| `S.Media.Playback` | `MediaPlayer`, HUD, metrics, `TriggerBus` wiring | baseline + Phase 4/11 |
| `S.Media.FFmpeg.Encode` | H.264/HEVC/ProRes + AAC/Opus/FLAC file outputs | Phase 12 |
| `S.Media.Effects` | CPU/GL effect hooks (extracted from monolith) | Phase 7 |
| `S.Media.SkiaSharp` | `VideoSource.OpenImage` | Phase 2 + ext registry (11) |
| `S.Media.OpenGL` | GL video output | baseline |
| `S.Media.SDL3` | Window + GL host | baseline |
| `S.Media.PortAudio` | Audio output device | baseline |
| `S.Media.NDI` | Send/receive, ingest clocks | Phase 6 |
| `S.Media.Avalonia` | Avalonia video surface | baseline |
| `S.Media.OSC` / `S.Media.MIDI` | Trigger bridges | Phase 11 |

## Entry points (by task)

| Task | Type / method | Namespace |
|---|---|---|
| Init plugins | `MediaFrameworkRuntime.Init().UseFFmpeg()` … | `S.Media.Core.Diagnostics` |
| Open container | `MediaContainer.OpenFile` / `OpenStream` | `S.Media.FFmpeg` |
| Low-level decode | `MediaContainerDecoder.Open` | `S.Media.FFmpeg` |
| Audio clip | `AudioSource.OpenFile` / `OpenStream` | `S.Media.Core.Audio` |
| Video clip | `VideoSource.OpenFile` / `OpenImage` | `S.Media.Core.Video` |
| Route audio | `AudioRouter`, `AudioGraphBuilder` | `S.Media.Core.Audio` |
| Route video | `VideoRouter` | `S.Media.Core.Video` |
| Play file (product) | `MediaPlayer.OpenFile` … `TryBuild` / `OpenAsync` | `S.Media.Playback` |
| Live graph | `MediaPlayer.OpenLive` | `S.Media.Playback` |
| Metrics | `MediaPlayer.GetMetrics()` | `S.Media.Playback` |
| Triggers | `MediaPlayer.Triggers`, `TriggerBus` | `S.Media.Playback` / `S.Media.Core` |
| Encode to file | `FFmpegMuxFileOutput`, `FFmpegVideoFileOutput` | `S.Media.FFmpeg.Encode` |
| OSC / MIDI | `OscTriggerBridge`, `MidiTriggerBridge` | `S.Media.OSC`, `S.Media.MIDI` |

## Clocks (`S.Media.Core.Clock`)

| Type | Implements | Notes |
|---|---|---|
| `MediaClock` | `IMediaClock`, `IPlayhead` | Master driver; slaves to `IPlaybackClock` |
| `CompositePlaybackClock` | `IPlaybackClock` | Priority pick among candidates |
| `VideoPtsClock` | `IPlaybackClock` | Video-only / freerun PTS |
| `NDIIngestPlaybackClock` | `IPlaybackClock` | NDI receive (`S.Media.NDI.Clock`) |

Legacy aliases (obsolete): `IPlaybackPlayhead`, `IPlaybackTimeline` → use `IPlayhead` / clock types above.

## Outputs renamed (Phase 1)

`IAudioSink` → `IAudioOutput`, `IVideoSink` → `IVideoOutput`. Obsolete sink names may still compile with `[Obsolete]`.

## Deprecation policy (Phase 13)

- **Keep for v2.0**: `MediaPlayer.TryOpen*` family (builder internals and HaPlay still reference them).
- **Remove in next major**: duplicate open overloads, `IVideoCpuFrameConverter` on wrong assembly, `AudioRouterAutoResample` type alias if unused.
- **Documented replacements**: builders + `MediaContainer` / `AudioSource` facades (Phase 2).

## Snapshot counts (2026-05-22)

Regenerate with:

```bash
find MediaFramework/Media -name '*.cs' | xargs wc -l | tail -1
rg -c '^public (sealed |abstract |static |class|interface|enum|record|struct)' MediaFramework/Media --glob '*.cs' | awk -F: '{s+=$2} END {print s}'
```

Approximate at Phase 13 close: **~36k** LOC under `MediaFramework/Media` (`find … | xargs wc -l`), **~200+** top-level `public` declarations per the ripgrep snapshot command above (nested types add more; use IDE / reflection for an exact export).
