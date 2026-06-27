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
`next/` mirrors the framework layout and carries `MFPlayer.Next.sln`. Carried-forward projects keep
their existing assembly/namespace names where they survive as the same module (`S.Media.Core`,
`S.Media.NDI`, `S.Control`, `S.Media.Interop`); split or renamed rewrite modules use the planned new
names (`S.Media.Decode.FFmpeg`, `S.Media.Present.SDL3`, `S.Media.Images.Skia`, etc.). Separate build
outputs mean no collision as long as old and next managed assemblies are never loaded into one process.
Old `MFPlayer.sln` is untouched and keeps shipping. The in-place stub dirs
(`MediaFramework/Media/S.Media.Time` etc.) are superseded.
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
**Crossing a thread/context/API boundary** (to Avalonia, a D3D11/Vulkan consumer, a native plugin) is
**not** a shared GL context — it's an **exported external image** (dmabuf fd on Linux / D3D11-DXGI
shared handle on Windows) **+ a negotiated sync primitive** (keyed-mutex / semaphore, per OQ2), imported
by the consumer. (NDI is **not** here — its SDK send path is CPU `p_data`, so NDI is a CPU readback
target; see OQ3.) That is one currency: it's Avalonia's
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
  SDK (Linux + Windows), Vortice (D3D11 interop — use the **3.8.x NuGet**, not the stale `Reference/`
  1.9.143 copy whose 1.x API differs; OQ5). The Tier-0 keep-list is real. ✓
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

## Follow-up open questions

These do not reopen D1-D15. They are implementation-contract details that should be answered before
the affected phase freezes its public API or ABI.

| ID | Question | Why it matters | Blocks |
|----|----------|----------------|--------|
| OQ1 | What is the exact tagged C-ABI frame descriptor for GPU frames? | D8 is feasible, but one `uint64 gpu_handle` is not enough for dmabuf, D3D11 shared textures, and GL textures. Define separate structs for CPU planes, dmabuf planes/fds/modifiers, D3D11 shared handles + keyed-mutex/sync metadata, and GL textures scoped to a declared context/share group. | Phase 6 ABI |
| OQ2 | What is the external-image synchronization contract per backend? | Avalonia 12 exposes capability queries and may use keyed mutex, semaphore, timeline semaphore, or automatic sync. The docs should say "sync primitive/capability", not assume every external image has a semaphore. | Phase 3 GPU / Phase 6 ABI |
| OQ3 | Is NDI output CPU-frame only in v1? | The referenced NDI SDK send path is CPU-buffer based, and today's sender requires CPU-backed pixel planes. If a future GPU NDI encoder exists it should be a separate target/backend; otherwise `NDI` should not be listed as an `ExternalImageCompositeTarget` consumer. | Phase 5 NDI / Phase 3 compositor |
| OQ4 | What native bundle does `LibAssLib` require on each platform? | libass itself is only the wrapper target; deployment also needs the native libass runtime dependencies such as FreeType, FriBidi, HarfBuzz, and the platform font provider path. The Phase 6 packaging plan should name these explicitly. | Phase 6 subtitles / packaging |
| OQ5 | Which Vortice version/source is authoritative for next? | `Reference/` contains `Vortice.Windows-1.9.143`, while the current package props use `Vortice.Direct3D11`/`Vortice.DXGI` 3.8.3. Pick one source of truth before freezing D3D11 interop code. | Phase 0 deps / Phase 3 GPU |
| OQ6 | Can old and next assemblies ever be loaded together? | D1 keeps a parallel managed generation with overlapping carried-forward assembly/namespace names. That is fine for separate solutions/build outputs, but unsafe if a host/test process loads old and next at the same time. Define the boundary or introduce a temporary shim/distinct package names. | Phase 0 solution / migration |
| OQ7 | How does the compositor transition between 8-bit SDR and RGBA16F HDR? | D12's auto mode is feasible, but a mid-session working-space switch can cause a visible reset unless the trigger, frame boundary, resource rebuild, and test expectations are specified. | Phase 3 compositor |
| OQ8 | What are the session-dispatcher reentrancy rules? | D5 avoids UI-thread assumptions, but callbacks from UI/plugins must not synchronously call back into `ShowSession` and deadlock the dispatcher. Define snapshot/event delivery and blocking-call rules. | Phase 1 session API |
| OQ9 | Which backends provide real device-change events and which poll? | D6 says dynamic devices are surfaced via backend change events, but native libraries differ. Define whether PortAudio/PortMidi/miniaudio/capture devices use native notifications, polling, or a hybrid. | Phase 1-2 registry/devices |
| OQ10 | Where do the control capability contracts live so `S.Abi` stays Session-free? | Finding 1 made `S.Abi` Session-free (`[Core, Compositor]`). But `IControlRegistryBuilder`/`IControlFeedbackDecoder`/`ControlDeviceProfile` currently imply living in `S.Control`, which references `S.Media.Session`; a Phase-6 `S.Abi → S.Control` ref would *transitively* re-introduce Session into the plugin host — reopening finding 1. | Phase 6 control adapter (decide contract home by Phase 1) |

### Resolutions (recommended approaches)

- **OQ1 — resolved: tagged union, one struct per kind, mirroring the Core HW-backings.** `MfpCpuFrame` ·
  `MfpDmaBufFrame` (per-plane fds/offsets/strides/modifiers + fourcc) · `MfpD3D11Frame` (luma/chroma NT shared handles +
  dxgi format + array slice + strides) · `MfpGlTextureFrame` (id/target/**context id — same-context only**, so
  layer-surface plugins, never cross-process). Common header = kind + w/h + pixfmt + pts + `MfpSync`.
  *Reflected in:* [05](05-Plugin-Model.md) `MfpVideoSourceVTable`. **Review hard at Phase 6 — forever-surface.**
- **OQ2 — a negotiated sync primitive, not "a semaphore".** Frame carries `MfpSync { None | KeyedMutex |
  BinarySemaphore | TimelineSemaphore }` + handle; the producer picks the best type the consumer
  advertises (Avalonia's `Supported*ExternalSemaphoreTypes`). D3D11 = keyed-mutex; Vulkan/GL =
  semaphore fd. *Reflected in:* [04](04-Compositor-Warp-GPU.md) §4, [05](05-Plugin-Model.md), D7.
- **OQ3 — NDI is a CPU target.** Validated: `NDIlib_video_frame_v2_t` sends from `uint8_t* p_data`. NDI
  output = `CpuFrameCompositeTarget` (one readback) in v1, **not** an external-image consumer; a future
  GPU NDI encoder would be a separate backend. *Reflected in:* [04](04-Compositor-Warp-GPU.md) §4, D7.
- **OQ4 — `LibAssLib` bundle = libass + FreeType + FriBidi + HarfBuzz + a font provider** (fontconfig on
  Linux; DirectWrite/GDI on Windows — validated from libass `meson.build`). Prefer a self-contained
  build with deps statically linked (one `.so`/`.dll`); document font discovery per platform. Fallback
  if bundling bites (no D14 reopen): route ASS through `Decode.FFmpeg`'s libass-backed decoder.
  *Reflected in:* D14, [07](07-Migration-and-Phasing.md) Phase 6.
- **OQ5 — Vortice 3.8.x (NuGet) is authoritative.** `Reference/Vortice.Windows-1.9.143` is a stale copy
  (1.x API differs); pin 3.8.x in next's package props and don't mirror 1.x patterns. *Reflected in:*
  dep-check above.
- **OQ6 — one generation per process.** Old HaPlay stays on the old sln; next builds/tests reference
  only next; overlapping carried-forward names never collide because they never share a process. If a
  process must ever bridge old↔next, cross via the `s_media_player` C ABI (native — no managed-identity
  clash).
  *Reflected in:* [07](07-Migration-and-Phasing.md) §4.
- **OQ7 — choose working space at `Configure`/graph-rebuild, not per-frame.** Promote eagerly to RGBA16F,
  demote only at cue/idle boundaries (hysteresis) so a show never resets mid-playback; rebuild FBOs at a
  frame boundary (same path as a resolution change). Tests: SDR→8-bit, HDR-layer→16F, add/remove-HDR
  rebuild w/o leak. *Reflected in:* [04](04-Compositor-Warp-GPU.md) §1 (D12 note).
- **OQ8 — dispatcher reentrancy.** Public API = `Post` (fire-and-forget) + `InvokeAsync` (awaitable)
  only; **no blocking `Invoke` from within a dispatcher callback** (debug guard throws); events deliver
  immutable snapshots off the dispatcher thread; plugin/UI callbacks run outside the dispatcher lock.
  *Reflected in:* [02](02-Project-Structure.md) Tier 5 (D5 note).
- **OQ9 — hybrid, declared per backend** via a `SupportsDeviceChangeNotifications` capability + a shared
  coalescing poller + one uniform `DevicesChanged`. Validated: **native** = miniaudio
  (`ma_device_notification_type`, incl. `rerouted`) + NDI (`find_wait_for_sources`); **poll** =
  PortAudio (list fixed until `Pa_Terminate`/`Pa_Initialize`) + PortMidi (header: reinit to rescan);
  **capture** = OS notify (udev / `WM_DEVICECHANGE`) or poll. *Reflected in:* D6.
- **OQ10 — resolved: control decoder contracts live in `S.Control.Abstractions`.** The scoped
  `ControlMeterBlobDecoderRegistry` and `IControlMeterBlobDecoder` are referenced by both `S.Control` and
  `S.Abi`; the plugin host's allowed set is `[Core, Time, Compositor, Control.Abstractions]` with **no
  Session, direct or transitive**. The arch-test allow-list enforces this boundary.
  *Reflected in:* [02](02-Project-Structure.md) Tier 6/7; `S.Media.Arch.Tests` (Phase 6).

When a decision is revisited, edit its entry here first (it's the source of truth for "what did we
decide?"), then update the doc(s) on its *Reflected in* line. Add new cross-cutting questions here
rather than letting them live only in chat.
