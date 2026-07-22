# S.Media.FFmpeg.Common

Shared FFmpeg runtime plumbing for MFPlayer's decode and encode packages: native library resolution and FFmpeg.AutoGen initialization. Low-level; most applications should reference S.Media or S.Media.Encoding instead.

This is a **low-level binding / transitive dependency package**. It is published because higher-level packages depend on it; most applications should not reference it directly.

## Native prerequisites

- FFmpeg 8.x shared libraries (avcodec-62 ABI)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
