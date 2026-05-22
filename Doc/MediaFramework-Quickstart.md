# MediaFramework quickstart

Minimum path from a media file to audible playback. Assumes FFmpeg is on `PATH` and the host references `S.Media.Core`, `S.Media.FFmpeg`, and (for file decode) `S.Media.Playback`.

## Six-line playback

```csharp
MediaFrameworkRuntime.Init().UseFFmpeg();

using var media = MediaContainer.OpenFile("clip.mkv");
using var router = new AudioRouter(media.Audio.Format.SampleRate);
var output = new PlainOutput(media.Audio.Format); // or PortAudio / NDI output from optional packages
var srcId = router.AddSource(media.Audio, autoResample: true);
var outId = router.AddOutput(output);
router.AddRoute(srcId, outId);
router.RouteLast();
router.Play();
```

`PlainOutput` is a test sink in `S.Media.Core`; real apps swap in `S.Media.PortAudio` or file encoders.

## Product entry (`MediaPlayer`)

For decode + `VideoRouter` + optional `AudioRouter` in one object:

```csharp
MediaFrameworkRuntime.Init().UseFFmpeg();

using var player = await MediaPlayer.OpenFile("clip.mkv").OpenAsync();
player.Play();
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
