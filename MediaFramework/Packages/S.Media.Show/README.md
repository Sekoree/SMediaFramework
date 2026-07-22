# S.Media.Show

Cue/show/session and composition runtime for MFPlayer. Composes the `S.Media` playback runtime
with show sessions and documents (`S.Media.Session`), the layered video compositor
(`S.Media.Compositor`), libass subtitle rendering (`S.Media.Subtitles`) and text sources
(`S.Media.Source.Text`).

This package is a supported **entry point** of the MFPlayer framework. It is dependency-only:
no assembly of its own, just the composed leaf packages.

## Native prerequisites

Everything `S.Media` needs, plus libass 0.17.5 for subtitles. See the
[native dependency matrix](https://github.com/Sekoree/MFPlayer/blob/master/Doc/Native-Dependencies.md).

## Documentation

Quickstart, architecture, examples and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).
