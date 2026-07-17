# S.Media.Encoding

Recording, muxing and network-stream output for MFPlayer. Composes FFmpeg encode/mux sessions
(`S.Media.Encode.FFmpeg`) with the HTTP/HLS media server (`S.Media.Stream.Http`).

This package is a supported **entry point** of the MFPlayer framework. It is dependency-only:
no assembly of its own, just the composed leaf packages.

## Native prerequisites

FFmpeg 8.1 (avcodec 62). See the
[native dependency matrix](https://github.com/Sekoree/MFPlayer/blob/master/Doc/Native-Dependencies.md).

## Documentation

Quickstart, architecture, examples and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).
