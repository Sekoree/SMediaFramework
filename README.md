# MFPlayer — a C# media framework + HaPlay show app

MFPlayer is a modular, NativeAOT-friendly media framework for **.NET 10**: FFmpeg decode → sync layers →
audio (PortAudio / MiniAudio) and video (SDL3 / Avalonia / NDI) outputs, with an N→M channel mixer, a
layered compositor (video + images + text), a headless show/cue engine, and MIDI/OSC control glued together
by an embedded scripting runtime (Mond). **HaPlay** is the demo/operator app built on top of it.

> **Status:** work in progress, developed with substantial AI assistance. The framework core is the focus;
> HaPlay's UX is still maturing. See [`Review/`](Review/) for the current engineering review and the
> [action checklist](Review/00-Action-Checklist.md) tracking known issues.

A prebuilt Windows/Linux test build is on the [Releases page](https://github.com/Sekoree/MFPlayer/releases).

---

## Supported platforms & native dependencies

Published and CI-gated for **`linux-x64`** and **`win-x64`** (.NET 10, `net10.0`). Other platforms (e.g.
macOS — the native wrappers carry `.dylib` names) may build but are not CI-tested. The framework core is
pure managed; capabilities that touch hardware/codecs load a native library.
Each backend module is **optional** — the host starts even when a backend's native lib is absent (that
capability simply doesn't register).

| Capability (module) | Native dependency | Source | Version (matched to the binding) |
|---|---|---|---|
| FFmpeg decode (`S.Media.Decode.FFmpeg`) | FFmpeg (avcodec/avformat/…) | apt / BtbN (Linux), `FFmpeg.GPL` NuGet (Windows) | FFmpeg 8.1 / avcodec-62 (FFmpeg.AutoGen 8.1.0) |
| PortAudio out/in (`S.Media.Audio.PortAudio` → `PALib`) | `libportaudio` | apt / prebuilt | 19.7.0 |
| MiniAudio out (`S.Media.Audio.MiniAudio` → `MALib`) | `miniaudio` | compiled from source | 0.11.25 |
| SDL3 video (`S.Media.Present.SDL3`) | SDL3 | `SDL3-CS` NuGet | (NuGet) |
| Avalonia video (`S.Media.Present.Avalonia`) | Skia | inherited from Avalonia (`SkiaSharp` NuGet) | (NuGet) |
| NDI in/out (`S.Media.NDI` → `NDILib`) | NDI runtime | DistroAV / NDI SDK | v6 |
| Subtitles (`S.Media.Subtitles` → `LibAssLib`) | `libass` (+ a font) | apt / prebuilt | 0.17.5 |
| MIDI (`S.Control` → `PMLib`) | `portmidi` | apt / prebuilt | 2.0.7 |
| MMD physics bake (`S.Media.Source.MMD`) | `mmd_bullet` (vendored Bullet) | built via `MediaFramework/Native/mmd_bullet/build.sh` | Bullet 3.25 |
| OSC (`S.Control` → `OSCLib`) | — (pure managed) | — | — |

CI stages the non-NuGet natives next to the published binary (`.github/workflows/build.yml`). To run
locally on Linux: `sudo apt-get install -y ffmpeg libass9 fontconfig fonts-dejavu-core libportaudio2 libportmidi0`.

---

## Repository layout

```
MediaFramework/
  Media/        S.Media.*        — the framework libraries (see the layering below)
  Control/      S.Control[.Abstractions]   — MIDI/OSC + Mond control scripting
  Interop/      S.Abi (plugin ABI host), S.Media.Interop (outbound C ABI → s_media_player)
  Audio|MIDI|NDI|OSC|Subtitles/  — PALib/MALib, PMLib, NDILib, OSCLib, LibAssLib (native P/Invoke wrappers)
  Native/mmd_bullet/             — the first-party MMD physics shim (C++/Bullet)
  Tools/        *Smoke           — headless smoke/probe programs (AotSmoke, AbiSmoke, …)
  Test/         *.Tests          — xUnit suites, incl. S.Media.Arch.Tests (layering enforcement)
UI/
  HaPlay        — the app view models/views (Avalonia)
  HaPlay.Desktop— the launcher (AOT-published as "HaPlayer" — the name disambiguates the exe from the library)
  HaPlay.Tests  — headless Avalonia + VM tests
```

**Module layering** (dependencies point down only; enforced by `S.Media.Arch.Tests`):

```
S.Media.Core ─┬─ S.Media.Time ── S.Media.Routing ── S.Media.Players ─┐
              ├─ S.Media.Gpu ─── S.Media.Compositor ─────────────────┼─ S.Media.Session
              └─ S.Media.FFmpeg.Common ── S.Media.Decode.FFmpeg      │
Backends: S.Media.Audio.{PortAudio,MiniAudio}, S.Media.Present.{SDL3,SDL3.Compositor,Avalonia},
          S.Media.NDI, S.Media.Subtitles, S.Media.Source.{YouTube,MMD}
Control:  S.Control.Abstractions → S.Control      Interop: S.Abi, S.Media.Interop
```

---

## Quick start

```bash
dotnet build MFPlayer.sln -c Release            # build everything
dotnet test  MFPlayer.sln -c Release            # run the unit + architecture suites
dotnet run --project UI/HaPlay.Desktop -c Release  # launch the HaPlay app
```

### Composing a host and playing a show (headless)

The framework has no process-global state: you compose a **registry** at the root from the backend modules
you want, then run a **`ShowSession`** over it.

```csharp
using var host = MediaHost.Build(b => b
    .Use(new FFmpegModule())        // decode
    .Use(new PortAudioModule()));   // audio out (add SDL3/NDI/Subtitles modules as needed)

await using var session = new ShowSession(host.Registry, audioBackend: /* an IAudioBackend, or */ null);
session.LoadDocument(ShowDocument.FromJson(showJson));   // validated before it touches the live show
await session.GoAsync();                                  // fire the next cue
var snapshot = (await session.SnapshotAsync())[0];        // query transport
```

The same session is drivable from any language through the outbound C ABI
(`MediaFramework/Interop/S.Media.Interop/include/s_media_player.h`), and third-party backends/sources can be
added as plugins through the inbound ABI (`MediaFramework/Interop/S.Abi/include/mfp_plugin.h`).

---

## Ownership, threading & real-time rules

These invariants hold across the framework — respect them when extending it:

- **Composition root, not globals.** The capability registry is built once at the root and is immutable
  afterwards; decoder/backend selection is deterministic (highest-confidence wins).
- **Explicit disposal.** Frames and sources are `IDisposable` with a single owner; a producer keeps a frame's
  CPU/GPU handles valid until the consumer releases it. Native handles live only in the `*Lib` wrapper
  projects.
- **Real-time callbacks return promptly.** Audio submit/read, video output submit, and layer render run on
  the engine's clock/compositor thread — push slow work to your own thread. The **audio router raises pump
  pressure lock-free**; the **video pump** raises it *after* releasing its queue lock (never call back into
  the router while holding a backend lock).
- **Bounded queues.** Video/control queues are bounded with explicit dropped/coalesced counters; pixel
  conversion is staged *outside* the router lock. SIMD channel mixers keep their `IsHardwareAccelerated`/
  `Avx.IsSupported` guards plus scalar fallbacks.
- **No `async void`** anywhere in the framework.
- **Session safety.** A replacement `ShowDocument` is validated and staged before the live show is touched;
  all mutations run on a dedicated dispatcher; cue traversal is bounded/cycle-checked; JSON is
  source-generated (AOT-safe).
- **ABI safety.** Handles are opaque monotonic tokens (never raw pointers); the outbound ABI uses
  thread-local last-error and per-session call leases so `destroy`/`shutdown` drain in-flight calls before
  releasing a session.

---

## HaPlay — build, run, config, security

```bash
dotnet run --project UI/HaPlay.Desktop -c Release
# AOT publish (per RID):
dotnet publish UI/HaPlay.Desktop -c Release -r linux-x64   # or win-x64
```

- **Config:** per-machine settings live at `%LocalAppData%/HaPlay/app-settings.json` (written atomically with
  a one-deep `.bak` backup). Projects/cues/soundboards are separate files you open/save.
- **Remote API:** an optional HTTP control surface (Bitfocus Companion, stream decks, …). **Off by default;
  binds loopback only** unless LAN is explicitly enabled. The access **token is optional** — set one to
  require authentication (compared in constant time); with no token the API is open on whatever it's bound to
  (intended for a closed show LAN). GET and POST are both accepted. See the Project workspace and
  `UI/HaPlay/Remote/`.
- **Control scripts are trusted code.** A project's Control system can carry Mond scripts that run arbitrary
  logic (MIDI/OSC glue) when the control system is **armed**. For safety a project always opens **disarmed**
  and is never persisted armed, so opening a project never runs its scripts — arming is an explicit operator
  action. Treat projects from untrusted sources the same as any code: review their scripts before arming.

---

## Testing & smoke commands

```bash
dotnet test MFPlayer.sln -c Release                                   # unit + architecture tests
dotnet publish MediaFramework/Tools/AotSmoke -c Release -r linux-x64  # NativeAOT publish smoke
bash MediaFramework/Native/mmd_bullet/build.sh <dir>                  # build the MMD physics shim
```

Smoke/probe tools live in `MediaFramework/Tools/` (backends, compositor, NDI, GL, playback, ABI, …). The C
plugin-ABI smoke (`Tools/AbiSmoke`) gcc-compiles a test plugin and exercises every adapter. HaPlay's launch
smoke self-exits under `HAPLAY_SMOKE=1`.

---

## Distribution

Consumed today via **project reference** (as HaPlay does). Per-project NuGet packaging is the intended model;
packaging metadata + API-compat checks are tracked in the review checklist (DOCS-02).

## Third-party dependencies

Avalonia + SkiaSharp (UI/text), FFmpeg / FFmpeg.AutoGen (decode), SDL3-CS (video), PortAudio + MiniAudio
(audio), PortMidi (MIDI), NDI (network A/V), libass (subtitles), Bullet (MMD physics), YoutubeExplode
(YouTube source), and [Mond](https://github.com/Rohansi/Mond) (AOT-friendly control scripting). MMD model
understanding drew on XRAnimator and blender_mmd_tools. See `Directory.Packages.props` and `External/` for
exact versions and licenses.
