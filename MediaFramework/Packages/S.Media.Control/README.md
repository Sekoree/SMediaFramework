# S.Media.Control

Show control for MFPlayer. Composes the control runtime (`S.Control` — surfaces, Mond scripting,
command dispatch, bindings), its contracts (`S.Control.Abstractions`), PortMidi MIDI (`PMLib`)
and OSC (`OSCLib`).

This package is a supported **entry point** of the MFPlayer framework. It is dependency-only:
no assembly of its own, just the composed leaf packages.

## Native prerequisites

PortMidi 2.0.7 for MIDI devices; OSC is pure managed. See the
[native dependency matrix](https://github.com/Sekoree/MFPlayer/blob/master/Doc/Native-Dependencies.md).

## Documentation

Control setup guides, the scripting reference and device-profile layouts live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc)
(`HaPlay-Control-*.md`).
