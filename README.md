# MFPlayer.Next — source tree

The parallel rewrite tree planned in [`../Next/`](../Next/README.md). It builds independently of the
old `MFPlayer.sln`, which is left untouched.

## Build / test / AOT smoke

    dotnet build  next/MFPlayer.sln -c Release
    dotnet test   next/MediaFramework/Test/S.Media.Arch.Tests/S.Media.Arch.Tests.csproj
    dotnet publish next/MediaFramework/Tools/AotSmoke/AotSmoke.csproj -c Release -r linux-x64 -p:PublishAot=true

(Windows: `-r win-x64`. Linux NativeAOT needs `clang` + `zlib1g-dev`.)

## ⚠️ One generation per process (D1 / OQ6)

next/ keeps carried-forward assembly names where a module remains the same logical module
(`S.Media.Core`, `S.Media.NDI`, …); split/renamed modules intentionally use their new names
(`S.Media.Decode.FFmpeg`, `S.Media.Present.SDL3`, …). There is no collision **as long as old and next
are never loaded into the same process**. Never reference an old-tree assembly from next/ (or
vice-versa). If a transitional process must bridge the two, cross the boundary via the `s_media_player`
C ABI (native — no managed identity clash), never by loading both managed sets.

## Layout

| Path | Contents |
|---|---|
| `MediaFramework/Media/*` | framework (Core, Time, Routing, Gpu, Compositor, Players, Session) + backend modules |
| `MediaFramework/Control/S.Control` | MIDI/OSC/Mond control surface |
| `MediaFramework/Interop/{S.Media.Interop, S.Abi}` | outbound C ABI + inbound native plugin host |
| `MediaFramework/Test/*` | tests (arch-test today) |
| `MediaFramework/Tools/*` | smoke/parity harness (AotSmoke today) |

Dependencies point **down only**, enforced by `S.Media.Arch.Tests` against
[`../Next/01-Architecture-and-Principles.md`](../Next/01-Architecture-and-Principles.md) §3. Build
settings live in `Directory.Build.props`; package versions in `Directory.Packages.props`. Work the
phases from [`../Next/09-Phase-Checklists.md`](../Next/09-Phase-Checklists.md).
