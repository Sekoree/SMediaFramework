# S.Media.Audio.PortAudio

PortAudio audio backend for MFPlayer (the mature primary host API). Requires the native PortAudio 19.7 library at runtime.

This package is an independently installable **feature module**. If you are starting fresh, begin with the `S.Media` / `S.Media.Show` / `S.Media.Full` entry packages and add this only when you need it directly.

## Native prerequisites

- libportaudio.so.2 / portaudio.dll (PortAudio 19.7.x)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
