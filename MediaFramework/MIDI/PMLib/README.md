# PMLib

Low-level P/Invoke binding for PortMidi 2.x. Most applications should reference S.Media.Control instead.

This is a **low-level binding / transitive dependency package**. It is published because higher-level packages depend on it; most applications should not reference it directly.

## Native prerequisites

- libportmidi.so.2 / portmidi.dll (PortMidi 2.x ABI)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
