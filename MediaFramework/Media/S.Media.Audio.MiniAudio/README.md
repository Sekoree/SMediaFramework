# S.Media.Audio.MiniAudio

miniaudio audio backend for MFPlayer (alternative backend; also proves the backend seam). Requires the exact pinned miniaudio 0.11.25 native build - the binding refuses any other version.

This package is an independently installable **feature module**. If you are starting fresh, begin with the `S.Media` / `S.Media.Show` / `S.Media.Full` entry packages and add this only when you need it directly.

## Native prerequisites

- libminiaudio.so / miniaudio.dll (exactly 0.11.25; the resolver version-gates it)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
