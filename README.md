# HaPlay / MFPlayer

Media player and framework built on .NET 10, Avalonia UI, FFmpeg, and PortAudio.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (version pinned in `global.json`)

## Building

```bash
dotnet restore MFPlayer.sln
dotnet build MFPlayer.sln --no-restore
```

On **Windows**, the first build automatically downloads FFmpeg 8.1 DLLs from BtbN (~45 MB) and copies PortAudio into the output directory. Subsequent builds skip the download. No extra tools required.

On **Linux and macOS**, install native dependencies manually before building (see below).

## Native dependencies

### Windows

| Library | How it's provided |
|---------|-------------------|
| FFmpeg | Downloaded automatically on first build |
| PortAudio | Copied automatically from the NuGet package |
| PortMidi | Downloaded automatically on first build |
| NDI | Manual — install NDI 6 Runtime or Tools from [ndi.video](https://ndi.video/download-ndi-sdk/), then rebuild |

After installing the NDI Runtime, rebuild once and `Processing.NDI.Lib.x64.dll` will be copied to the output directory automatically.

### Linux

```bash
# Debian / Ubuntu
sudo apt install ffmpeg libportaudio2

# Arch
sudo pacman -S ffmpeg portaudio

# Fedora
sudo dnf install ffmpeg portaudio
```

NDI requires the NDI 6 Runtime from [ndi.video](https://ndi.video/download-ndi-sdk/). The installer sets `NDI_RUNTIME_DIR_V6`, which the runtime resolver uses to locate the library automatically — no further steps needed.

### macOS

```bash
brew install ffmpeg portaudio
```

NDI: install the NDI 6 Runtime from [ndi.video](https://ndi.video/download-ndi-sdk/).

## Running

```bash
dotnet run --project UI/HaPlay.Desktop/HaPlay.Desktop.csproj
```

## Further reading

- [Build environment and CI](Doc/Build-Environment.md)
- [Architecture overview](Doc/MediaFramework-Architecture.md)
- [Quickstart (code)](Doc/MediaFramework-Quickstart.md)
- [Public API map](Doc/MediaFramework-PublicAPI.md)
- [Supported formats and pixel formats](Doc/MediaFramework-Format-Support.md)
