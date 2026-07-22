# S.Media.Interop

Outbound C ABI for MFPlayer: publishes the framework as the NativeAOT s_media_player shared library for non-.NET hosts.

This package is a supported **entry point** of the MFPlayer framework.

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
