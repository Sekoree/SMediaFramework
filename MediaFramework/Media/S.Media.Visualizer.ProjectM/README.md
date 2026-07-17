# S.Media.Visualizer.ProjectM

projectM (Milkdrop) audio visualizer for MFPlayer, rendered as a GPU layer surface. Requires a desktop-GL libprojectM-4 build (4.1.6 with the repo's bound-FBO patch).

This package is an independently installable **feature module**. If you are starting fresh, begin with the `S.Media` / `S.Media.Show` / `S.Media.Full` entry packages and add this only when you need it directly.

## Native prerequisites

- libprojectM-4 (desktop-GL build, 4.1.6; see scripts/build-projectm.sh)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
