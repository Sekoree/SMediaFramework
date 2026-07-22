# S.Media.Core

Core contracts of the MFPlayer media framework: frames, formats, clocks, buses, diagnostics and ownership rules. Dependency-light SDK for writing custom sources, outputs and backends.

This package is a supported **entry point** of the MFPlayer framework.

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
