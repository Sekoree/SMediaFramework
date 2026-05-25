# Build Environment

This repository is currently validated with the .NET SDK pinned in `global.json`.
The pin uses `rollForward: latestFeature`, so later compatible feature-band SDKs
can still be used when `10.0.300` itself is not installed.

## Required Commands

Use the solution as the normal developer entry point:

```bash
dotnet restore MFPlayer.sln
dotnet build MFPlayer.sln --no-restore /nr:false
```

Run focused tests by project:

```bash
dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore --no-build
dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore --no-build
dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --no-restore --no-build
```

Some test runners open local sockets. In restricted sandboxes, `dotnet test`
may fail before tests start with a socket permission error; rerun outside the
sandbox or in CI with normal loopback socket permissions.

## Managed Dependencies

Package versions are centrally managed in `Directory.Packages.props`.

Important package groups:

- Avalonia UI: `Avalonia`, `Avalonia.Desktop`, themes, fonts, diagnostics.
- FFmpeg bindings: `FFmpeg.AutoGen`.
- OpenGL: `Silk.NET.OpenGL`, `Silk.NET.Core`.
- SDL3: `SDL3-CS`, `SDL3-CS.Native`.
- SkiaSharp: `SkiaSharp`, `SkiaSharp.NativeAssets.Linux`.
- Windows GPU interop: `Vortice.Direct3D11`, `Vortice.DXGI`.
- Tests: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`.

## Native Runtime Dependencies

The solution builds without every native device or SDK being present, but runtime
features require the matching native libraries:

- FFmpeg: libavcodec, libavformat, libavutil, libswresample, libswscale.
- PortAudio: native PortAudio library and a working host backend such as ALSA,
  PulseAudio, PipeWire, CoreAudio, WASAPI, or ASIO depending on platform.
- JACK: native JACK client library when using `JackLib`.
- NDI: NewTek/NDI runtime for sender/receiver features.
- SDL3: provided by `SDL3-CS.Native` for supported platforms; platform GL/window
  drivers are still required.
- SkiaSharp: native assets are provided for Linux by package; other platforms
  should use the matching SkiaSharp native asset package if needed.
- Avalonia OpenGL previews: platform windowing/OpenGL drivers.
- PipeWire and PulseAudio: optional runtime backends, useful for Linux audio and
  capture workflows.

## CI Baseline

A practical CI build should run:

```bash
dotnet --info
dotnet sln MFPlayer.sln list
dotnet restore MFPlayer.sln
dotnet build MFPlayer.sln --no-restore /nr:false
dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore --no-build
dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore --no-build
dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --no-restore --no-build
```

Keep hardware-dependent smoke tools separate from the normal build. Run them on
machines with the required devices and native runtimes installed.
