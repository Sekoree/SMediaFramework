# MediaFramework quickstart

Minimum path from a media file to playback with the current (v2) framework. Reference the
`S.Media` meta-package (or `S.Media.Full` for everything); it pulls in the core, FFmpeg decode and
both audio backends. FFmpeg 8.x (avcodec-62) shared libraries must be loadable at runtime — see
[Native-Dependencies.md](Native-Dependencies.md).

## 1. Build a registry

Everything starts from an explicit `MediaRegistry` (no global state): each module registers its
decoders/backends and loads its native library.

```csharp
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Audio.PortAudio;

var registry = MediaRegistry.Build(b => b
    .Use(new FFmpegModule())        // decode
    .Use(new PortAudioModule()));   // audio devices (add MiniAudioModule() for the alternative backend)
```

## 2. Open and play a file

`MediaPlayer` is the product-level entry: decode + audio/video routing + A/V sync in one object.

```csharp
using S.Media.Players;
using S.Media.Audio.PortAudio;

using var player = MediaPlayer.Open(registry, "clip.mkv");

// Audio: attach a device output (the default output device here).
using var speakers = new PortAudioOutput(player.AudioRouter!.Format);
player.AttachAudioOutput(speakers);

// Video (optional): attach any IVideoOutput - an SDL3 window, NDI sender, Avalonia control, …
// using var window = new SDL3GLVideoOutput("Playback", 1280, 720);
// player.AttachVideoOutput(window);

player.Play();
while (!player.Video.IsSourceExhausted)
    Thread.Sleep(100);
```

`MediaPlayer.OpenFile(registry, path)` / `OpenUri(...)` give a fluent builder for options
(hardware acceleration, stream selection, output preset); `MediaPlayer.OpenLive(audio, video)`
wraps already-open live sources (NDI, capture) without a registry.

## 3. Where to go next

| Need | Use |
|---|---|
| Cue/show semantics, compositions, fan-out to many outputs | `ShowSession` (`S.Media.Show` package) |
| Recording / streaming | `S.Media.Encoding` (FFmpeg encode sessions + LAN HTTP/HLS server) |
| MIDI/OSC control + scripting | `S.Media.Control` |
| Embedding video in Avalonia | `S.Media.Present.Avalonia` |
| Publish the framework as a C ABI | `S.Media.Interop` (NativeAOT `s_media_player`) — see [NativeAOT.md](NativeAOT.md) |

The runnable smokes under `MediaFramework/Tools/` (e.g. `MultiOutputSmoke`, `BackendsSmoke`,
`EncoderSmoke`, `CompositeToNDISmoke`) are maintained, compiling examples of every subsystem, and
the `ShowSession` tests under `MediaFramework/Test/S.Media.Session.Tests/` show the cue/show API
end to end.
