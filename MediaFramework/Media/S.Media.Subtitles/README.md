# S.Media.Subtitles

libass subtitle rendering for MFPlayer (ASS/SSA and converted text subtitles). Requires libass 0.17.5+ at runtime.

This package is an independently installable **feature module**. If you are starting fresh, begin with the `S.Media` / `S.Media.Show` / `S.Media.Full` entry packages and add this only when you need it directly.

## Native prerequisites

- libass.so / ass.dll (0.17.5 or newer)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
