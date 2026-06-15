# Media player → compositions, sizing, and NDI timecode — plan & status

Tracks three asks (2026-06-15). One shipped; two are substantial and staged with concrete designs
so they can each get a focused, validated turn.

## 1. Composition sizing — auto-size is a MEDIA-PLAYER feature (corrected 2026-06-15)

The auto-size-to-output belongs to the **regular media player**, not the cue player (the cue player keeps
explicit, operator-chosen composition sizes — an earlier "Fit to output" button + default-from-output on the
cue composition editor was reverted).

* **Media player** (opt-in composition path, §2): the composition canvas auto-sizes to the **first video
  output that declares a resolution, else 1080p** (`HaPlayPlaybackSession.FirstOutputResolutionOr1080`).
  Resolution source: `LocalVideoOutputDefinition.WindowWidth/Height` and
  `NDIOutputDefinition.ResolutionLockWidth/Height` via `HaPlayPlaybackSession.TryGetOutputResolution`
  (unit-tested in `CompositionSizingTests`).
* **Cue player**: unchanged — composition Width/Height are set explicitly in the Output-setup → Compositions
  list (default 1080p). No auto-fit button.

> Limitation: a resolution is only known when the output declares one (window size / NDI lock). A running
> SDL window's *live* size isn't read. Good enough for the common "configured rig" case; a future enhancement
> could read the live output size, and surface a manual override / explicit auto-fit in a media-player
> composition settings UI if wanted.

## 2. Media player uses compositions — OPT-IN MVP WIRED (2026-06-15); refinements next

**Goal:** route the regular media-player decks through the composition pipeline like the cue player, with
the decoder video on **layer 0** and the hold/logo image as a layer on top (**layer 1**; "the top layer"
so it overlays everything — only higher than 1 if more overlays are added later).

**Shipped (opt-in, default off — `HAPLAY_MEDIAPLAYER_COMPOSITIONS=1`):**
* `MediaPlayerCompositionRuntime` (`UI/HaPlay/Playback/`) — owns a `ClipCompositionRuntime`, a layer-0
  video slot (`VideoSink`), and an optional layer-1 logo slot, with `SetHold` / `SetVideoOpacity` /
  `SetClockMaster` / `EnsurePumpStarted`. Unit-tested (`MediaPlayerCompositionRuntimeTests`: layer-0 fans
  to all outputs; logo layer present only with a logo).
* `HaPlayPlaybackSession.TryCreate` (file path): when the flag is set and the file has video, it builds the
  composition over the deck's video output lines (`TryBuildMediaPlayerComposition`) and routes the decoder
  video into layer 0 instead of the per-output `LogoFallbackVideoOutput` fan-out. Canvas size defaults to the
  first output's resolution, else 1080p. **When the flag is off the path is byte-identical to before** (the
  whole branch is guarded), so the default deck behaviour is untouched. 539 HaPlay tests green.

**Remaining refinements (need the running app to validate):**
* **Feed layer 1** — the session currently builds the composition video-only; pass the deck's hold/logo
  image into `MediaPlayerCompositionRuntime`'s logo slot, and route the deck's HOLD state to `SetHold` and
  per-deck video fade to `SetVideoOpacity` (replacing the `LogoFallbackVideoOutput` hold/fade/fault).
* **Clock master** — currently the composition runs freerun (presents the latest decoded frame at canvas
  rate); call `SetClockMaster(Player.AudioClock, Player.PlayClock)` after audio is wired for tight A/V sync.
* **Per-line health** — the comp path doesn't populate `LineWiring.LogoOutput`, so per-line presentation
  stats degrade in comp mode; wire comp pump metrics to the health panel.
* **Live inputs + the live path** (NDI/PortAudio decks) — the opt-in covers file playback only so far.
* **Flip the default / retire `LogoFallbackVideoOutput`** from the media-player path once parity is confirmed.

### Validate the opt-in (in-app)
Set `HAPLAY_MEDIAPLAYER_COMPOSITIONS=1`, play a file deck to one or more video outputs, and confirm: video
shows on every output; multi-output decks stay frame-locked (same canvas frame per tick, §`HaPlay-MultiOutput-Sync.md`);
output mapping / the Layout editor now apply to the media player too; teardown releases the outputs cleanly
(no stuck windows / NDI carriers). Then report so the refinements above can be prioritised.

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
  gives the media player **output mapping / warp / multi-output layout** for free.

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
   validated), then flip the default once proven.
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
* **Aspect-locked resize.** Dragging the bottom-right handle in `OutputLayoutCanvas` now preserves the box's
  aspect ratio by default (no distortion while fine-tuning); hold **Shift** to resize freely.
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
