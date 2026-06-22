# Media player → compositions, sizing, and NDI timecode — plan & status

Tracks three asks (2026-06-15). One shipped; two are substantial and staged with concrete designs
so they can each get a focused, validated turn.

## 1. Composition sizing — auto-size is a MEDIA-PLAYER feature (corrected 2026-06-15)

The auto-size-to-output belongs to the **regular media player**, not the cue player (the cue player keeps
explicit, operator-chosen composition sizes — an earlier "Fit to output" button + default-from-output on the
cue composition editor was reverted).

* **Media player** (default composition path, §2): the composition canvas auto-sizes to the **first video
  output that declares a resolution, else 1080p** (`HaPlayPlaybackSession.FirstOutputResolutionOr1080`).
  Resolution source: `LocalVideoOutputDefinition.WindowWidth/Height` and
  `NDIOutputDefinition.ResolutionLockWidth/Height` via `HaPlayPlaybackSession.TryGetOutputResolution`
  (unit-tested in `CompositionSizingTests`).
* **Cue player**: unchanged — composition Width/Height are set explicitly in the Output-setup → Compositions
  list (default 1080p). No auto-fit button.

> Limitation: a resolution is only known when the output declares one (window size / NDI lock). Windowed
> local previews now write resize events back into `LocalVideoOutputDefinition.WindowWidth/Height`, so a
> manually resized local output becomes the reported size used by later composition/layout decisions.
> Fullscreen outputs still rely on their configured/declared size policy.

## 2. Media player uses compositions — DEFAULT PATH WIRED (2026-06-15)

**Goal:** route the regular media-player decks through the composition pipeline like the cue player, with
the decoder video on **layer 0** and the hold/logo image as a layer on top (**layer 1**; "the top layer"
so it overlays everything — only higher than 1 if more overlays are added later).

**Shipped (default on; set `HAPLAY_MEDIAPLAYER_COMPOSITIONS=0`, `false`, or `off` for the legacy path):**
* `MediaPlayerCompositionRuntime` (`UI/HaPlay/Playback/`) — owns a `ClipCompositionRuntime`, a layer-0
  video slot (`VideoSink`), and an optional layer-1 logo slot, with `SetHold` / `SetVideoOpacity` /
  `SetHoldFrame` / `SetClockMaster` / `EnsurePumpStarted`. Unit-tested (`MediaPlayerCompositionRuntimeTests`:
  layer-0 fans to all outputs; logo layer present only with a logo).
* `HaPlayPlaybackSession.TryCreate` (file path): when the file has video, it builds the
  composition over the deck's video output lines (`TryBuildMediaPlayerComposition`) and routes the decoder
  video into layer 0 instead of the per-output `LogoFallbackVideoOutput` fan-out. Canvas size defaults to the
  first output's resolution, else 1080p. The env var above keeps the old direct-router path available for
  debugging.
* `ApplyFallbackImage` / `SetHoldFallback` now feed the deck hold image into the composition's layer-1 slot
  and toggle that layer instead of relying on a per-output hold pump. The old logo wrapper path is still used
  only when composition mode is explicitly disabled.
* Windowed local output resize events update the output definition, so later media-player sizing and the
  cue-player layout editor see the current reported raster.

**Remaining refinements (need the running app to validate):**
* **Clock master** — currently the composition runs freerun (presents the latest decoded frame at canvas
  rate); call `SetClockMaster(Player.AudioClock, Player.PlayClock)` after audio is wired for tight A/V sync.
* **Per-line health** — the comp path doesn't populate `LineWiring.LogoOutput`, so per-line presentation
  stats degrade in comp mode; wire comp pump metrics to the health panel.
* **Live inputs + the live path** (NDI/PortAudio decks) — the composition path covers file playback only so far.
* **Media-player mapping UI/model** — the regular media player now uses a composition internally, but it
  does not yet own saved output mappings/layouts like cue compositions do. Add a media-player mapping model
  before claiming cue-style Layout editor control for deck outputs.
* **Retire `LogoFallbackVideoOutput`** from the media-player path once composition parity is confirmed in the
  running app.

### Validate in-app
Play a file deck to one or more video outputs and confirm: video shows on every output; HOLD shows the
configured fallback image without the old 30 Hz hold pump; multi-output decks stay frame-locked (same canvas
frame per tick, §`HaPlay-MultiOutput-Sync.md`); the full canvas fans out to every selected video output;
teardown releases the outputs cleanly (no stuck windows / NDI carriers). Set `HAPLAY_MEDIAPLAYER_COMPOSITIONS=0`
only when comparing against the old path.

---

### Original design notes (retained)

**Current path** (`UI/HaPlay/Playback/HaPlayPlaybackSession.cs`): `decoder.Video` → `player.VideoRouter`
(one input) → fan-out to each video output line, each wrapped in a `LogoFallbackVideoOutput`. The hold /
logo / fault-fallback behaviour lives in `LogoFallbackVideoOutput` (substitutes a static frame, caches the
last real frame, drives a hold toggle, per-branch opacity for video fades).

**Target path:** build a composition (the framework `ClipCompositionRuntime`, as the cue engine does via
`CueCompositionRuntime`) sized per §1, leased to the same output lines:
* **Layer 0** = the decoder video (route the player's video into the composition's layer-0 slot).
* **Layer 1** = the hold/logo image as a `StaticFrameSource` (or image source) slot, shown on
  hold/idle/fault, positioned/scaled like any layer.
* The composition fans to the outputs (replacing the per-output `LogoFallbackVideoOutput`), which also
  gives the media player the framework hook needed for **output mapping / warp / multi-output layout** once
  the regular-media-player side has a saved mapping model/UI.

**Why it's staged, not rushed:**
* `HaPlayPlaybackSession` (~2,200 lines) is the **live** path every deck plays through; a regression
  here breaks core playback.
* `LogoFallbackVideoOutput` does more than show a logo (last-frame cache, hold toggle, fault fallback,
  per-branch opacity). Re-expressing each of those as composition-layer behaviour needs care so nothing
  regresses (e.g. cover-art-only audio that produces one frame; live-fault hold).
* It can't be validated headlessly — it needs the running app + real outputs.

**Suggested increments for a focused turn:**
1. A `MediaPlayerCompositionRuntime` adapter (mirroring `CueCompositionRuntime`) that owns a
   `ClipCompositionRuntime`, a layer-0 video slot fed by the deck, and a layer-1 logo slot.
2. Wire it behind an opt-in per-player flag first (so the existing path stays the default while it's
   validated), then flip the default once proven. **Current status: default flipped; legacy path remains
   available via `HAPLAY_MEDIAPLAYER_COMPOSITIONS=0/false/off`.**
3. Map the existing hold/fade/fault behaviours onto layer ops (layer-1 logo visibility = hold; per-deck
   fade = layer-0 opacity tween; fault = show layer-1).
4. Retire `LogoFallbackVideoOutput` from the media-player path once parity is confirmed.

## 3. NDI receiver-side timecode alignment — FRAMEWORK MODE SHIPPED (2026-06-15); host opt-in + hardware next

**Egress is already timecoded** (`NDIOutputPreviewRuntime` stamps
`NDIVideoTimecodeMode.PresentationRelativeTicks`), so a composition's NDI outputs carry a shared egress
timecode. The receive side already presented frames at their timecode — but **first-frame-relative + rebased
to local play-start** (`NDISource.ResolveVideoPresentationTime` via `NDIFrameTiming.TryMapPresentationTime`).
That is smooth for a single receiver but uses a **per-receiver origin**, so two receivers of one sender don't
agree on absolute time — the gap for wall sync.

**Shipped:** an **absolute-timecode receive mode** (opt-in, default off):
* `NDIFrameTiming.TryGetAbsolutePresentationTime(timecode, timestamp, out TimeSpan)` — maps the raw 100 ns
  egress timecode straight to a `TimeSpan` with **no session origin / no rebase**, so the *same frame* resolves
  to the *same* time on every receiver. Unit-tested (`NDIFrameTimingTests`: timecode-direct, two-receivers-agree,
  timestamp fallback).
* `NDISourceOptions.PresentVideoByAbsoluteTimecode` (default `false`) → `NDISource.ResolveVideoPresentationTime`
  uses the absolute mapping instead of the relative+rebase one. Default off keeps single-receiver playback
  exactly as before (64 NDI tests green).

**Remaining:**
* **Host opt-in** — expose `PresentVideoByAbsoluteTimecode` on HaPlay NDI-input items (a per-input toggle) and
  thread it into the `NDISourceOptions` HaPlay builds for that input.
* **Shared clock reference** — cross-receiver alignment needs the receivers' presentation clocks to share a
  reference (PTP / display genlock) and the receiving playback clock to run on the same absolute timeline; the
  framework now supplies the absolute per-frame timeline, the reference is a deployment concern.
* **Audio** — the analogous absolute-timecode path on the audio receive side, for A/V-locked absolute mode.
* **Validate** with two receivers of one sender on a real network — arrival jitter / timecode behaviour don't
  reproduce headlessly, so end-to-end is hardware-gated.

> Reminder from `Doc/HaPlay-MultiOutput-Sync.md`: wire-accurate sub-frame alignment across *separate*
> displays/receivers ultimately needs hardware genlock (display genlock / NDI Discovery + receiver
> timecode); the framework's job is to feed the right frame at the right time — which the absolute mode now does.

## 4. Multi-output layout editor — corrections (2026-06-15)

* **Moved to the composition list.** The **Layout…** button now lives on each composition row (Output-setup
  → Compositions), not on the per-output binding rows — the layout is a property of the composition (all its
  outputs together), so that's where it belongs.
* **Physical output sizing.** The layout editor now carries each output's reported raster separately from the
  composition canvas. A 3840×1080 composition with two 1920×1080 outputs opens as two half-width 1080p tiles,
  and saved mappings keep `CueOutputMapping.OutputWidth/Height` at the physical output size instead of the
  source-slice size.
* **Numeric fine-tuning.** Selecting an output exposes numeric X/Y/W/H fields in canvas pixels.
* **Aspect-locked resize.** Dragging the bottom-right handle in `OutputLayoutCanvas` now preserves the output's
  physical pixel aspect ratio by default (no distortion while fine-tuning); uncheck **Lock aspect ratio** or
  hold **Shift** to resize freely.
* **Keyboard fine-positioning.** A selected output nudges with the **arrow keys** — one canvas pixel per
  press (the dialog passes the canvas pixel size), **Shift = 10 px**.

## 5. GL compositor crash — fixed (2026-06-15)

Combining two outputs on one composition threw `composite_layer program missing required uniforms`
(`GlVideoCompositor.BuildPipeline` via `SDL3GLVideoCompositor.TryProbe`). Root cause: `SharedGlProgramCache`
keyed programs by shader-pair string only, so a program linked in one GL context was returned to a compositor
in another context (a multi-output composition creates the canvas compositor plus the probe / per-output
mapping-stage compositors, each with its own context) — every `glGetUniformLocation` then returned -1. Fixed
by scoping the cache **per `GL` instance** (one `GL` per context here) via `ConditionalWeakTable`, preserving
legitimate same-context program sharing. See `Doc/Explained/15-Issues-and-Improvements.md`.
