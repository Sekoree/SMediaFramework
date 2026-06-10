# MediaFramework quickstart

Minimum path from a media file to audible playback. Assumes FFmpeg is on `PATH` and the host references `S.Media.Core`, `S.Media.FFmpeg`, and (for file decode) `S.Media.Playback`.

## Six-line playback

```csharp
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;            // UseFFmpeg() lives here

MediaFrameworkRuntime.Init().UseFFmpeg();

using var media = MediaContainer.OpenFile("clip.mkv");
using var router = new AudioRouter(media.Audio.Format.SampleRate);
var output = DiscardingAudioOutput.ForRouter(router); // swap in S.Media.PortAudio etc. for real playback
var srcId = router.AddSource(media.Audio, autoResample: true);
var outId = router.AddOutput(output);
router.Route(srcId, outId);   // identity channel map, gain 1.0
router.Play();
```

`Route(srcId, outId)` is the identity-map shorthand; pass an explicit
`ChannelMap` (or call `AddRoute(...)`) when you need per-channel mixing.
`router.RouteLast()` is the soundboard-style one-shot wiring that uses the
most-recently added source and output. For a full per-cell gain matrix
(e.g. a 5.1 → stereo downmix), use `router.ApplyMatrix(srcId, outId,
AudioChannelLayoutPresets.Downmix(6, 2))` — one route per non-zero cell,
reconciled atomically on re-apply.

## Product entry (`MediaPlayer`)

For decode + `VideoRouter` + optional `AudioRouter` in one object:

```csharp
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.Playback;

MediaFrameworkRuntime.Init().UseFFmpeg();

using var player = await MediaPlayer.OpenFile("clip.mkv").OpenAsync();
player.AttachVideoOutput(myVideoOutput);   // add + route in one call (rolls back on failure)
player.AttachAudioOutput(myAudioOutput);   // identity map from the decoder's audio source
player.Play();
// Router-level APIs remain for matrix/multi-route hosts:
// player.VideoRouter.AddOutput(...); player.VideoRouter.TryAddRoute(player.VideoRouterInputId, outId, out _);
```

Builders: `MediaPlayer.OpenFile`, `OpenUri`, `OpenStream`, `Open(decoder)`, `OpenLive(audio, video)`. Options: `MediaPlayerOpenOptions` (`SpoolStreamToDisk`, `StreamIsSeekable`, queue depths, live presentation).

## Images (still frame)

```csharp
MediaFrameworkRuntime.Init().UseFFmpeg().UseSkiaSharpImages(); // S.Media.SkiaSharp

using var img = VideoSource.OpenImage("slide.png");
using var player = MediaPlayer.OpenLive(null, img)
    .WithOptions(o => o with { IncludeAudioRouter = false })
    .TryBuild(out var p, out var err) ? p! : throw new InvalidOperationException(err);
player.Video.HoldLastFrameAtEnd = true;
player.Play();
```

## Shutdown

Call `MediaFrameworkRuntime.Shutdown()` when unloading the framework in long-lived hosts (tests do this around Skia image registration).

## Next steps

- [MediaFramework-Architecture.md](MediaFramework-Architecture.md) — router drift, session flush, bundle ownership
- [MediaFramework-PublicAPI.md](MediaFramework-PublicAPI.md) — namespace map
- [MediaFramework-Triggers.md](MediaFramework-Triggers.md) — `TriggerBus` ids for OSC/MIDI
- [MediaFramework-Format-Support.md](MediaFramework-Format-Support.md) — pixel format matrix
