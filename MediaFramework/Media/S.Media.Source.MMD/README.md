# S.Media.Source.MMD

MikuMikuDance (PMX/VMD) scene source for MFPlayer with Bullet-physics-backed animation. Requires the mmd_bullet native shim built by this repo.

This package is an independently installable **feature module**. If you are starting fresh, begin with the `S.Media` / `S.Media.Show` / `S.Media.Full` entry packages and add this only when you need it directly.

## Native prerequisites

- libmmd_bullet.so / mmd_bullet.dll (built from MediaFramework/Native/mmd_bullet)

## Documentation

Architecture, examples, the native dependency matrix and the release-tier contract live in the
[MFPlayer repository docs](https://github.com/Sekoree/MFPlayer/tree/master/Doc).

Threading/ownership rules: frames and sessions have explicit owners - see the XML documentation
on the public types and `Doc/MediaFramework-Architecture.md`.
