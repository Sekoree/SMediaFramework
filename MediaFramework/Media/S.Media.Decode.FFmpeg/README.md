# S.Media.Decode.FFmpeg

FFmpeg-backed decoding for MFPlayer: shared demux, audio/video decode queues and non-file sources. Requires FFmpeg 8.x (avcodec-62) native libraries at runtime.

This package is an independently installable **feature module**. If you are starting fresh, begin with the `S.Media` / `S.Media.Show` / `S.Media.Full` entry packages and add this only when you need it directly.

## Native prerequisites

- FFmpeg 8.x shared libraries (avcodec-62 ABI)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
