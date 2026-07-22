# S.Media.Full

Batteries-included MFPlayer framework feature set — everything a full HaPlay-class application
uses. Composes the entry packages `S.Media.Show` (which brings `S.Media`), `S.Media.Encoding`,
`S.Media.Presentation.SDL3` and `S.Media.Control`, plus Avalonia embedding
(`S.Media.Present.Avalonia`), NDI send/receive (`S.Media.NDI`), projectM visualization
(`S.Media.Visualizer.ProjectM`), MMD and YouTube sources (`S.Media.Source.MMD`,
`S.Media.Source.YouTube`) and the native plugin/C-ABI surfaces (`S.Abi`, `S.Media.Interop`).

**Framework only** — this package never contains the HaPlay executable or its view models.
HaPlay remains the reference/full application.

This package is a supported **entry point** of the MFPlayer framework. It is dependency-only:
no assembly of its own, just the composed leaf packages.

## Native prerequisites

The full tier: FFmpeg 8.1, PortAudio 19.7, miniaudio 0.11.25, SDL 3.4.12, libass 0.17.5,
PortMidi 2.0.7, projectM 4.1.6 (+ presets/textures), Bullet 3.25 (MMD physics) and the NDI v6
runtime. See the
[native dependency matrix](https://github.com/Sekoree/MFPlayer/blob/master/Doc/Native-Dependencies.md)
and [release tiers](https://github.com/Sekoree/MFPlayer/blob/master/Doc/Release-Tiers.md).

## Documentation

Quickstart, architecture, examples and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).
