# PALib

Low-level P/Invoke binding for PortAudio. Most applications should reference S.Media.Audio.PortAudio (or the S.Media meta-package) instead.

This is a **low-level binding / transitive dependency package**. It is published because higher-level packages depend on it; most applications should not reference it directly.

## Native prerequisites

- libportaudio.so.2 / portaudio.dll (PortAudio 19.7.x)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
