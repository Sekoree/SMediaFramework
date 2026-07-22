# S.Media.Presentation.SDL3

Standalone/windowed SDL3 presentation for MFPlayer. Composes the SDL3 window/GL presenters
(`S.Media.Present.SDL3`) with the SDL3 compositor output path (`S.Media.Present.SDL3.Compositor`).

This package is a supported **entry point** of the MFPlayer framework. It is dependency-only:
no assembly of its own, just the composed leaf packages. For embedding video into Avalonia
applications use `S.Media.Present.Avalonia` instead.

## Native prerequisites

SDL 3.4.12. See the
[native dependency matrix](https://github.com/Sekoree/MFPlayer/blob/master/Doc/Native-Dependencies.md).

## Documentation

Quickstart, architecture, examples and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).
