# MALib

Low-level P/Invoke binding for miniaudio 0.11.25 (hand-mirrored ABI; the resolver accepts only that exact version). Most applications should reference S.Media.Audio.MiniAudio instead.

This is a **low-level binding / transitive dependency package**. It is published because higher-level packages depend on it; most applications should not reference it directly.

## Native prerequisites

- libminiaudio.so / miniaudio.dll (exactly 0.11.25)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
