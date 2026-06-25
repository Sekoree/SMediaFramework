# 08 — Decisions ledger

The cross-cutting decisions that aren't pinned down by the numbered design docs. All of D1–D15 are
now **locked**. Where a choice diverged from the recommended default it's marked ⚑ — those carry
extra consequences worth keeping visible.

**Status legend:** 🔒 LOCKED · ⚑ chosen over the recommended default

| ID | Decision | Choice | Blocks |
|----|----------|--------|--------|
| D1 | New-source physical layout | 🔒 parallel `next/` subtree | Phase 0 |
| D2 | Source-addressing / "open" model | 🔒 URI scheme + typed options | Phase 1–2 |
| D3 | Decoder/provider resolution | 🔒 confidence score + order tiebreak | Phase 1 |
| D4 | Master clock granularity | 🔒 per transport group | Phase 1 |
| D5 | Session API threading | 🔒 session dispatcher loop | Phase 1 / 4 |
| D6 | Registry vs device hot-plug | 🔒 frozen caps / dynamic devices | Phase 1–2 |
| D7 | GL context + plugin GPU ABI | 🔒 shared GL group, GL-explicit ABI | Phase 3 + ABI |
| D8 | Frames across the C-ABI | 🔒 ⚑ **GPU handles from v1** | Phase 6–7 |
| D9 | C-ABI v1 capability scope | 🔒 ⚑ **full vtable surface in v1** | Phase 6–7 |
| D10 | Show persistence | 🔒 Session-owned, STJ source-gen JSON | Phase 4 |
| D11 | Master audio output + rate | 🔒 per-group master, mix at its rate | Phase 2 |
| D12 | Compositor working color space | 🔒 ⚑ **auto (8-bit SDR / 16F HDR)** | Phase 3 |
| D13 | Platform matrix | 🔒 ⚑ **Windows + Linux only** | Phase 0 (CI) |
| D14 | Subtitle rendering | 🔒 libass + Skia + FFmpeg bitmap | Phase 6 |
| D15 | TFM / packaging / arch-test / Mond | 🔒 see below | various |

---

## D1 — New source lives in a parallel `next/` subtree
`next/` mirrors `MediaFramework/` + `UI/` and carries `MFPlayer.Next.sln`. Assembly/namespace names
stay identical to today's — separate build outputs mean no collision. Old `MFPlayer.sln` is untouched
and keeps shipping. The in-place stub dirs (`MediaFramework/Media/S.Media.Time` etc.) are superseded.
*Reflected in:* [07](07-Migration-and-Phasing.md) §0 + Phase 0.

## D2 — URI scheme + typed options for opening sources
A scheme selects the provider (`file:` `ndi:` `capture:` `mic:` `image:` `http(s):`); an optional typed
options object carries params. One dispatch path for `IMediaRegistry`, `Players`, and the C ABI; a
plugin adds a new scheme with no host change.
*Reflected in:* [05](05-Plugin-Model.md) `IMediaRegistry` (`CanOpen`/`TryOpen*` take a URI); [03](03-AV-Sync-Clocks-Routing.md) §6 track selection rides in the options object.

## D3 — Confidence-scored decoder resolution
Each `IMediaDecoderProvider` returns a confidence score for a URI; highest wins, ties broken by
registration order; a caller may pin an explicit provider. Automatic without silent shadowing.
*Reflected in:* the provider contract written in Phase 1.

## D4 — One master `SessionClock` per transport group
A transport group = a cue or a set fired/seeked together. Each group owns a `SessionClock`; live-only
groups use a session wall-clock. When a group's master output stops, that group idles — mastership
never floats between unrelated sources.
*Reflected in:* [03](03-AV-Sync-Clocks-Routing.md) §1–§2.

## D5 — Session API uses an internal dispatcher loop
Public `ShowSession` commands marshal onto an internal session thread; queries return immutable
snapshots; clock/audio/GL threads stay internal. The API **never** assumes the UI thread (the current
bug class — `ui_thread_observable_property_sets`).
*Reflected in:* [02](02-Project-Structure.md) Tier 5.

## D6 — Frozen capabilities, dynamic devices
Which modules/backends exist is frozen at compose time (AOT-clean, immutable `IMediaRegistry`). Device
enumeration *within* a backend (USB MIDI, capture cards, audio endpoints) is dynamic, surfaced via
change events on the backend — not by mutating the registry.
*Reflected in:* [05](05-Plugin-Model.md) registry contracts.

## D7 — Shared GL context per render thread; cross-boundary zero-copy via external images
*Refined after checking Avalonia 12 + the current SDL3/Avalonia code (see Reference check below).*
`S.Media.Gpu` shares **one GL context per render thread** (the existing `SharedSdlGlContext` pattern):
the compositor, its composition-FX, and per-output warp stages on that thread share it and pass
textures zero-copy. **SDL3** runs the compositor on its own thread, so it gets that zero-copy directly.
**Crossing a thread/context/API boundary** (to Avalonia, NDI/D3D11, a native plugin) is **not** a shared
GL context — it's an **exported external image** (dmabuf fd on Linux / D3D11-DXGI shared handle on
Windows) **+ a sync semaphore**, imported by the consumer. That is one currency: it's Avalonia's
`IGlContextExternalObjectsFeature.ImportImage` / `ICompositionGpuInterop`, the **D8** plugin frame
handle, and the existing `S.Media.Core` `DmabufNv12Backing`/`Win32SharedNv12Backing`. Plugin
`GpuContext`/`MfpGlContext` still exposes raw GL; `IGpuDevice` stays internal.
*Reflected in:* [04](04-Compositor-Warp-GPU.md) §4–§5, [02](02-Project-Structure.md) Tier 2, [05](05-Plugin-Model.md).

**Reference check (Avalonia 12):** `OpenGlControlBase` only shares a context Avalonia creates with
*itself* (`IOpenGlTextureSharingRenderInterfaceContextFeature.CreateSharedContext`) — you cannot inject
our SDL3/compositor context into Avalonia's share group, so a literal single global GL group is
impossible. The current `VideoOpenGlControl` therefore **re-uploads** each frame through the shared
`YuvVideoRenderer` shaders in Avalonia's own context (always-works fallback). True zero-copy into
Avalonia is the external-image path above, and it's **backend-dependent**: the handle type must match
Avalonia's active backend (D3D11 handle on Windows; dmabuf / Vulkan-opaque-fd on Linux).

## D8 — ⚑ GPU frame handles cross the C-ABI from v1
*Chosen over the recommended CPU-only-v1.* The v1 frame descriptor is **tagged** (`MfpFrameKind`) and
carries **either** CPU planes **or** a hardware handle (dmabuf fd / D3D11 shared handle / GL texture
id), using the same interop paths as `S.Media.Gpu` (D7). Native capture/output plugins get full
hardware paths immediately.
*Consequence:* the ABI is platform-specific from day one — GPU-handle structs must be gated behind the
kind tag (CPU-only plugins ignore them), and ownership needs explicit `acquire`/`release` + a sync
fence so producer/consumer don't race on a texture. Demands per-platform conformance tests in CI from
the start.
*Reflected in:* [05](05-Plugin-Model.md) `MfpVideoSourceVTable` descriptor + ownership + cost notes.

## D9 — ⚑ Full vtable surface in C-ABI v1
*Chosen over the recommended phased subset.* v1 ships **all** capability vtables: video-source,
video-output, audio-backend, layer-surface (GL), subtitle, control-decoder. Everything is available to
plugins immediately.
*Consequence:* the largest compatibility surface to freeze up front — pair with strict append-only
versioning and a conformance plugin exercising **every** vtable per platform. The layer-surface vtable
in particular freezes the GL-context contract (D7) at v1 rather than after in-process proving, so get
that surface reviewed hard before tagging v1.
*Reflected in:* [05](05-Plugin-Model.md) capability-vtable list (no v2 deferral).

## D10 — Session owns persistence (STJ source-gen JSON)
`S.Media.Session` owns a serializable `ShowDocument` (System.Text.Json source-generated, AOT-safe) so
shows load headless and via the C ABI. HaPlay layers only view-state (window/panel placement) on top.
Today's `ProjectIO`/`HaPlayProject`: runtime/data moves down into Session, view-state stays in the UI.
*Reflected in:* [02](02-Project-Structure.md) Tier 5.

## D11 — Per-group master output; mix at its rate
Each transport group designates one master output (default: first clocked device); the group mixes
internally at that device's native rate; sources resample on ingress. Resolves the
`portaudio_default_device_rate_desync` class by design. The master output is the group's clock source
(ties to D4).
*Reflected in:* [03](03-AV-Sync-Clocks-Routing.md) §5–§6.

## D12 — ⚑ Auto compositor color space (8-bit SDR / 16F HDR)
*Chosen over the recommended always-linear-16F.* The compositor blends in 8-bit BT.709 when every
input and output is SDR, and switches to linear-light RGBA16F when any HDR/wide-gamut content is
present. Best correctness/perf balance.
*Consequence:* the compositor must **reconfigure** when the input/output color-space set changes (and
must detect that from layer formats + output bindings). The `S.Media.Gpu` color pieces
(`YuvColorSpace`/`RgbGamutMatrix`/HDR-transfer) feed both paths.
*Reflected in:* [04](04-Compositor-Warp-GPU.md) §1 (working-space note).

## D13 — ⚑ Windows + Linux only (no macOS commitment)
*Chosen over the recommended add-macOS.* Tier-1 = Windows + Linux, matching today's release targets;
no macOS support obligation, sidestepping the deprecated-GL/ANGLE cost on the D7 shared-GL design. The
registry's capability-by-presence model means a future macOS port is additive (modules register where
they have a backend), not a rewrite.
*Reflected in:* [07](07-Migration-and-Phasing.md) DoD/CI (already "Windows + Linux"); no macOS in the
project docs.

## D14 — Subtitles: libass + Skia + FFmpeg bitmap
ASS/SSA via a new **`LibAssLib`** P/Invoke wrapper (full styling/positioning/karaoke); text (SRT/VTT)
via `Images.Skia`; bitmap (PGS/DVB) via `Decode.FFmpeg`'s registered subtitle capability. `S.Media.
Subtitles` owns rendering and references `LibAssLib`, never FFmpeg directly.
*Consequence:* +1 native dependency. `LibAssLib` joins Tier 0 with the same native-deployment
treatment as `PALib`/`PMLib` (`Directory.Build.targets` staging on Windows; package/lazy-resolve
elsewhere).
*Reflected in:* Tier 0 in [01](01-Architecture-and-Principles.md)/[02](02-Project-Structure.md)/[README](README.md); [04](04-Compositor-Warp-GPU.md) §6.

## D15 — Smaller defaults
- **TFM:** single `net10.0` across the framework.
- **Packaging:** internal `ProjectReference`s during the rewrite; defer public NuGet packaging of the
  Tier-A module contracts until the API stabilizes (post-Phase-7).
- **Arch-test:** add a test from Phase 0 asserting the [01](01-Architecture-and-Principles.md) §3
  reference rules (cheap insurance against the layering eroding again).
- **Mond script back-compat:** best-effort only — `S.Control` may break script APIs in the rewrite;
  provide a migration note, not a compatibility shim.
- **Minor doc nits to fold in as code lands:** how `S.Control` binds to a running `ShowSession`; and
  the exact labor split between `S.Media.Subtitles` and `Decode.FFmpeg` for subtitle track enumeration.

---

## Dependency reality check (`Reference/`)

Validated the plan against the vendored dependency source/SDKs in `Reference/`:

- **Mond 0.11.2** — README: *"fully compatible with Native AOT deployments (.NET 8+)"*. Confirms the
  scripting/AOT assumption ([06](06-Control-Surface.md), D5). ✓
- **libass 0.17.5** — source vendored, so the D14 `LibAssLib` wrapper is feasible. ✓
- **Native SDKs all present** — portaudio 19.7.0, portmidi 2.0.8, miniaudio 0.11.25, jack2 1.9.22, NDI
  SDK (Linux + Windows), Vortice.Windows 1.9.143 (D3D11 interop). The Tier-0 keep-list is real. ✓
- **FFmpeg.AutoGen (main / 8.1)** — full binding (hwcontext, swscale, subtitle decoders); aligns with
  the FFmpeg 8.x `FFmpeg.GPL` native. ✓
- **GL interop** — drove the D7 refinement: Avalonia can't join a foreign GL share group, so
  cross-boundary zero-copy is exported external images + semaphores. The existing `WglNvDxInterop` /
  `EglDmabufNv12Uploader` / `D3D11GlInteropDeviceHost` are the foundation for it. ✓
- **Leftover (unused):** `NodifyM.Avalonia-12.0.0` — referenced by **no** csproj or source (a dropped
  node-graph idea; the Control surface uses Mond + data-driven profiles, [06](06-Control-Surface.md)).
  Do **not** carry it into `next/`. (`Tmds.DBus.Protocol` has no direct refs either — it's only a
  transitive Avalonia/Linux dep, which is fine.)
- **macOS interop** (Metal/IOSurface) exists in Avalonia but is moot under D13 (Win/Linux only).

---

## How to use this file

When a decision is revisited, edit its entry here first (it's the source of truth for "what did we
decide?"), then update the doc(s) on its *Reflected in* line. Add new cross-cutting questions here
rather than letting them live only in chat.
