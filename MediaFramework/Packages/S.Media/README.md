# S.Media

Default playback runtime for MFPlayer applications. One reference brings in the core contracts
(`S.Media.Core`), clocks/sync (`S.Media.Time`), audio routing (`S.Media.Routing`), players
(`S.Media.Players`), FFmpeg decode (`S.Media.Decode.FFmpeg`) and both audio device backends
(`S.Media.Audio.PortAudio`, `S.Media.Audio.MiniAudio`).

This package is a supported **entry point** of the MFPlayer framework. It is dependency-only:
no assembly of its own, just the composed leaf packages.

## Native prerequisites

FFmpeg 8.1 (avcodec 62) for decode; PortAudio 19.7 and/or miniaudio 0.11.25 for audio devices.
See the [native dependency matrix](https://github.com/Sekoree/MFPlayer/blob/master/Doc/Native-Dependencies.md)
for pins and acceptance policies.

## Documentation

Quickstart, architecture, examples and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).
