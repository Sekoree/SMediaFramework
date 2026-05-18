# MediaFramework Action Checklist — 2026-05-18

Derived from `Doc/MediaFramework-Review-2026-05.md`. Ordered to minimize wasted work: fix correctness first (so symptoms stop), then do renames (so later code anchors on the right names), then defaults / ergonomics, then features, then polish. Breaking existing tests and HaPlay is fine — note where it'll hurt so you can re-test.

Legend: **size** = XS (<1 h) · S (<½ day) · M (1–2 days) · L (1 week) · XL (multi-week feature). **breaks** = which downstream code likely needs updates.

**Status (updated 2026-05-18):** Phase 0 complete. Phase 1 complete. Phase 2 is the next planned batch.

---

## Phase 0 — Stop the bleeding ✅ COMPLETE (2026-05-18)

Correctness defects from §11.1. Land these before any refactor; they're cheap and the symptoms they cause are confusing.

- [x] **C1. Refcount race on all four `VideoFrame` backings** — `S.Media.Core/Video/VideoDmabufNv12Backing.cs:70-75`, `VideoDmabufP010Backing.cs`, `VideoDmabufP016Backing.cs`, `VideoWin32Nv12Backing.cs`. Replace the `Volatile.Read(_closed)` + `Interlocked.Increment(_refCount)` pair with a single CAS loop on `_refCount`; remove `_closed` (refcount == 0 *is* closed). Add a unit test that races `AddReference` against `Dispose` on a synthetic backing. **Size:** S. **Breaks:** nothing — internal pattern only.
  - Done: `_closed` removed from all four backings; both AddReference and Dispose use a CAS loop on `_refCount`. Added `AddReference_RacesDispose_NeverIncrementsThroughZero` (500-iteration Barrier-synchronized race test) in `VideoDmabufNv12BackingRefCountTests.cs`. The other three backings share the identical pattern so the test covers them by construction.

- [x] **C2. `PortAudioOutput.Flush()` calibration reset reordering** — `S.Media.PortAudio/PortAudioOutput.cs:125-137`. Move `Volatile.Write(ref _streamSmoothCalibrated, 0)` to **before** `Pa_StartStream`. Add a test that flushes mid-stream and asserts `ElapsedSinceStart` is monotonic across the flush. **Size:** XS. **Breaks:** nothing.
  - Done: write reordered with explanatory comment. **No unit test added** — the race window is microseconds and reliably reproducing it would need real PortAudio hardware plus instrumented callbacks. The one-line reorder is small enough to review by inspection.

- [x] **C3. Replace `AudioFormat(48000, 0)` sentinel with `AudioFormat(0, 0)`** — `S.Media.FFmpeg/MediaContainerSharedDemux.cs:230`. Update the XML doc on `MediaContainerDecoder.Audio` to say "consumers must guard with `HasAudio`". Add `Debug.Assert(Channels > 0)` at every router-side site that reads `SampleRate` so a regression fails fast. **Size:** XS. **Breaks:** any consumer that today reads `decoder.Audio.Format.SampleRate` without checking `HasAudio` (none in-tree; check `Tools/` to be sure).
  - Done: sentinel changed to `AudioFormat(0, 0)`. Bonus fix: `Tools/NDIPlayer/Program.cs` was reaching into `dec.Audio.Format` without checking `HasAudio` — guarded both the `EnableAudio` call sites and the diagnostic print so video-only files no longer try to wire an audio sink. Skipped the proposed `Debug.Assert(Channels > 0)` peppering — the now-zero rate will fail fast at `AudioRouter`'s positive-rate check (AudioRouter.cs:141) without needing per-site asserts.

- [x] **C4. `AudioRouter` per-sink pump capacity is a flat 8 chunks for everyone.** AudioRouter.cs:136. For hardware sinks (those implementing `IClockedSink` with their own ring), expose a smaller default. At minimum document the latency implication on `AddSink` and on `AudioPlayer.AddOutput`. **Size:** XS doc, S if you also retune defaults.
  - Done (doc-only path): expanded the XML `<remarks>` on `AddSink` to call out the ~80 ms @ default settings and the rationale for hardware vs network sinks. Deferred the auto-tune for `IClockedSink` to a Phase 2 ergonomics pass — touching the default behavior risked surprising existing consumers and is better paired with the auto-resample / pump-by-default ergonomics work.

- [x] **Verify the §11.2 suspects.** Each has enough context for a focused ~15-minute look. Do this before §0 closes so any extra defects join this phase rather than slip later.
  - [x] **S1.** `AudioRouter.RetargetSlaveClock` (AudioRouter.cs:459-471) constructs the new clock under `_gate`. Either move construction outside the lock or add a comment that the constructor must never invoke router APIs. **Size:** XS. **Confirmed and fixed:** `SinkSlavedRouterClock` is now constructed before the lock; only the field swap happens under `_gate`.
  - [x] **S4.** `S.Media.Avalonia/VideoOpenGlControl.cs` detach/render race on `_pendingFrame`. Trace what happens if a render fires between `_sinkDisposed = true` and the lock acquisition that drains the pending frame. **Size:** S. **Confirmed real and fixed:** Submit could pass the `_sinkDisposed` check at the top, detach could then run and drain `_pendingFrame`, and Submit would park a *new* frame in `_pendingFrame` with no further `OnOpenGlRender` callback to drain it (frame leak). Fix: re-check `_sinkDisposed` inside the frame lock and dispose-then-throw if disposed.
  - [x] **S5.** `S.Media.OpenGL/Nv12Win32SharedHandleGpuUploader.cs` keyed-mutex pairing on early-return paths. Confirm every `TryAcquireForGpuRead` success has a `Release` on every exit. **Size:** S. Windows-only test. **Rejected — no defect.** The agent claim misread C# semantics: the `try { … } finally { keyedScope?.Dispose(); }` block at lines 395-454 wraps every early `return false` inside the try; C# runs the finally on return from a try block. The acquire-failure return at line 392 is before keyedScope exists. All paths paired.
  - [x] **S6.** Resampler delay-buffer flush across seek. Write a test: open a sine-tone file, seek to a different position, decode the first 100 ms post-seek, FFT — should not show residue from pre-seek samples. **Size:** S. **Rejected — no defect.** `swr_close(_swr); swr_init(_swr)` at `MediaContainerSharedDemux.cs:806-808` is the canonical FFmpeg way to drain the resampler — `swr_close` frees `in_buffer`/`out_buffer` (the delay buffer). Combined with the preceding `avcodec_flush_buffers` + `av_frame_unref`, the path is correct. The agent's claim that `swr_close` "doesn't exist in standard FFmpeg" was wrong (it's been in libswresample since 1.0).

**Phase exit:** confirmed defects are gone, all suspects resolved one way or the other. ✅

**Outcomes:**
- 4 confirmed defects fixed (C1–C4).
- 2 suspects confirmed real and fixed (S1, S4 — S4 was the surprise: agent flagged it as suspect, turned out to be a real frame leak).
- 2 suspects verified as agent misreads, not defects (S5, S6).
- 1 new unit test added (CAS-loop refcount race).
- Tests: 504/505 pass. The one failure (`NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize`) pre-exists on master and is flaky FP-tolerance unrelated to Phase 0.
- Build: clean, zero warnings, zero errors across all 23 projects including HaPlay.

**Files touched:**
```
MediaFramework/Media/S.Media.Avalonia/VideoOpenGlControl.cs       (S4 fix)
MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs            (C4 doc + S1 fix)
MediaFramework/Media/S.Media.Core/Video/VideoDmabufNv12Backing.cs (C1 fix)
MediaFramework/Media/S.Media.Core/Video/VideoDmabufP010Backing.cs (C1 fix)
MediaFramework/Media/S.Media.Core/Video/VideoDmabufP016Backing.cs (C1 fix)
MediaFramework/Media/S.Media.Core/Video/VideoWin32Nv12Backing.cs  (C1 fix)
MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs  (C3 fix)
MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs         (C2 fix)
MediaFramework/Test/S.Media.Core.Tests/Video/VideoDmabufNv12BackingRefCountTests.cs  (new test)
MediaFramework/Tools/NDIPlayer/Program.cs                         (C3 follow-up: HasAudio guards)
```

---

## Phase 1 — Names that will anchor everything else ✅ COMPLETE (2026-05-18)

These are mechanical but produce big diffs. Doing them early means new code in Phase 3+ uses the correct names from the start.

- [x] **Rename `AvRouter` → `MediaContainerSession`.** Chose `MediaContainerSession` over bare `MediaSession` so the type sits with `MediaContainerDecoder` / `MediaContainerPlaybackBundle`. `MediaContainerAvRouter` static factory folded into `MediaContainerSession.Create`. `MediaPlayer.Av` → `MediaPlayer.Session`. Tests: `MediaContainerSessionTests` (was `AvRouterTests`). **Size:** S.

- [x] **Rename `MediaContainerMegaPlaybackHost` → `MediaContainerPlaybackBundle`.** `MediaContainerMegaPlaybackOwnedParts` → `MediaContainerPlaybackBundleOwnedParts`. **Size:** S.

- [x] **Delete `MediaContainerPlaybackGraph`.** No in-tree callers remained; non-disposable grouping is covered by the bundle with `OwnedParts.None` when needed. **Size:** XS.

- [x] **Rename `S.Media.PortAudio.MediaContainerPlaybackHost` → `PortAudioPlaybackHost`.** `MediaContainerPlaybackHostPlayerOwnership` → `PortAudioPlaybackHostPlayerOwnership`. Kept a dedicated type (vs folding into `AudioPlayerPortAudioExtensions`) because PortAudio wiring + ownership flags are substantial. **Size:** S.

- [x] **Drop the "Tier E / Tier F / row N" annotations.** Stripped checklist row references from MediaFramework XML/docs; kept technical substance. `Doc/MediaFramework-Checklist-2026-05.md` is now the canonical checklist in-repo. **Size:** S.

- [x] **NDI jargon — keep names, add glossary.** Added `Doc/NDI-Terminology.md`; NDI types unchanged. **Size:** XS.

**Phase exit:** public taxonomy is consistent — no "Mega", no `AvRouter` misnomer, PortAudio vs FFmpeg lifecycle types are distinct. ✅

**Outcomes:**
- Build: clean (0 warnings / 0 errors) across full `MFPlayer.sln` including HaPlay.
- Tests: 503/504 pass. The one failure (`NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize`) is the same pre-existing flaky FP-tolerance test noted in Phase 0.
- Fix during finish: restored `MediaContainerSharedDemux.VideoIsAttachedPicture` (property referenced by `MediaContainerDecoder` / HaPlay but missing from demux — blocked compile).

**Files touched (representative):**
```
MediaFramework/Media/S.Media.FFmpeg/MediaContainerSession.cs              (new; replaces AvRouter)
MediaFramework/Media/S.Media.FFmpeg/MediaContainerPlaybackBundle.cs     (new; replaces MegaPlaybackHost)
MediaFramework/Media/S.Media.PortAudio/PortAudioPlaybackHost.cs         (new; replaces MediaContainerPlaybackHost)
MediaFramework/Media/S.Media.Playback/MediaPlayer.cs                    (Session, Bundle)
MediaFramework/Tools/VideoPlaybackSmoke/*                               (smoke wiring)
UI/HaPlay/Playback/HaPlayPlaybackSession.cs                           (MediaContainerSession)
MediaFramework/Test/.../MediaContainerSessionTests.cs                   (renamed)
MediaFramework/Test/.../MediaContainerPlaybackBundleTests.cs            (renamed)
MediaFramework/Test/.../PortAudioPlaybackHostTests.cs                   (renamed)
Doc/NDI-Terminology.md                                                  (new)
+ Tier-annotation cleanup across Core / FFmpeg / NDI XML remarks
```

---

## Phase 2 — Defaults & ergonomics (do before features)

Small changes that improve the API consumers will use. Doing these before Phase 3 means new high-level helpers inherit good defaults instead of papering over bad ones.

- [ ] **Drop the `48000` default on `AudioPlayer`.** `S.Media.Core/Audio/AudioPlayer.cs:75`. Either remove the default (force callers to be explicit) or add an overload that accepts an `IAudioSource` and infers. **Size:** XS. **Breaks:** every test that calls `new AudioPlayer()` (several in `S.Media.Core.Tests`). Easy mechanical fix.

- [ ] **`NDIAudioReceiver` ring capacity in time, not samples.** `S.Media.NDI/Audio/NDIAudioReceiver.cs:90`. Change the ctor param from `int ringCapacityFrames = 96000` to `TimeSpan ringCapacityDuration = TimeSpan.FromSeconds(2)`; compute frames from the first observed format. **Size:** XS. **Breaks:** any caller passing the int; in-tree probably only tests.

- [ ] **Default video sinks behind a `VideoSinkPump`.** Today `IVideoSink.Submit` runs on the clock-driver thread — `VideoPlayer.cs:24-29`. New sinks block by default and only learn this from a stutter. Make `VideoRouter.AddOutput` wrap in a pump unless `asyncPump: false` (or a new `synchronous: true` flag). **Size:** S. **Breaks:** `MediaPlayer.TryOpen` discarding-sink path, smoke tools that wire a sink directly; both fine to update.

- [ ] **Document `IVideoSink.Submit`'s thread contract on the interface itself**, not just in `VideoPlayer` remarks. One sentence in XML doc. **Size:** XS.

- [ ] **`AudioRouter.AddSource(autoResample: true)`** option. When set and the source's rate differs from the router's nominal rate, transparently wrap with `ResamplingAudioSink` (currently `S.Media.FFmpeg/Audio/ResamplingAudioSink.cs`). Required for soundboard use cases where clips have mixed rates. **Size:** M. **Breaks:** rate-mismatch today throws — autoResample changes the call site contract.

- [ ] **`AudioFormat` validation.** `S.Media.Core/Audio/AudioFormat.cs:13` is a `record struct(int, int)` with no validation. Today nothing stops `AudioFormat(-1, 99999)`. Add a single `Validate()` helper and call it at every public constructor / setter on AudioPlayer / Router / Sinks. **Size:** XS.

- [ ] **Convert `*PumpPressureEventArgs` to `readonly record struct`.** `S.Media.Core/Audio/AudioRouterEvents.cs`, `S.Media.FFmpeg/Video/VideoPumpPressureEvents.cs`. Keeps the otherwise-zero-alloc steady state intact under heavy pressure. **Size:** S. **Breaks:** any handler that captures `EventArgs` (probably none externally; rebuild and follow compiler errors).

**Phase exit:** the framework's defaults stop surprising consumers; the most likely API ergonomics complaints (rate mismatch rejection, blocking sinks, baked-in 48000) are gone.

---

## Phase 3 — Foundational features blocking product use cases

These unblock the *primary product surfaces* you named: cue players, soundboards, image triggers. Bigger pieces, build them once Phase 0–2 are solid.

- [ ] **`StaticFrameSource : IVideoSource`** in `S.Media.Core`. Wraps a single pre-built `VideoFrame` and replays it forever. No external deps. **Size:** XS. Unlocks "hold last frame at EOF" patterns immediately.

- [ ] **`ImageFileSource : IVideoSource`** in a new project (`S.Media.Image` or `S.Media.SkiaSharp`). Loads PNG/JPEG/WebP into a `VideoFrame` once. SkiaSharp is the obvious dependency; `StbImageSharp` is the dependency-light alternative. **Size:** M. **Breaks:** nothing — new code. **Blocks:** soundboard "image trigger", cue-player static cues.

- [ ] **`VideoPlayer` "hold last frame at EOF" mode.** `S.Media.Core/Video/VideoPlayer.cs:81`. New `HoldLastFrameAtEnd: bool` property — when source `IsExhausted` and queue drains, the player keeps the last frame on screen instead of falling silent. **Size:** S.

- [ ] **`MediaPlayer.Quick`** facade in `S.Media.Playback`. One-method open + play with sensible defaults (PortAudio default device, GL window, audio resample-as-needed). Hides routers, clocks, ownership flags. Power users still use `MediaPlayer.TryOpen`. **Size:** M. **Breaks:** nothing — additive. **Depends on:** Phase 2 auto-resample + pump-by-default.

- [ ] **`AudioGraphBuilder`** fluent helper for declaring routes. Today every consumer writes `AddSource → AddSink → AddRoute(channelMap)` manually. A fluent builder collapses common cases (`From(source).To(sink).Channels(stereo)` etc.). **Size:** M. **Breaks:** nothing — additive.

- [ ] **`AudioRouter` sub-mix bus.** A `BusSink` that is both `IAudioSink` and `IAudioSource`. Routes summing into the bus feed a downstream chain; lets you build mixer-style "drum group → comp/limiter → master out". Today the only path is direct source→sink and adding the bus must be done by the consumer. **Size:** M. **Breaks:** nothing — additive.

- [ ] **Move `VideoRouter` skeleton into `S.Media.Core`**, leave only `VideoCpuFrameConverter` and the FFmpeg-specific glue in `S.Media.FFmpeg`. Today consumers cannot use the video router without pulling in `FFmpeg.AutoGen`. **Size:** M. **Breaks:** the FFmpeg project's namespace, several test refs. Worth the disruption.

- [ ] **`MediaContainerSharedDemux` packet queue depth as `MediaPlayerOpenOptions` field.** Currently hardcoded at audio=192, video=384 (cs:27-28). Tight for some HEVC 4K B-frame streams. **Size:** XS.

**Phase exit:** the framework can demonstrably back a working soundboard (mixed clip rates, static images, click-free triggers) and a basic cue player without consumers fighting it.

---

## Phase 4 — Compositing & overlays (the next product frontier)

Once Phase 3 lands, the largest remaining gap is N→1 video compositing — needed for picture-in-picture, transitions, lower-thirds, text overlays. Plan up front so abstractions land in the right place.

- [ ] **Define `IVideoCompositor`** in `S.Media.Core`. Inputs: ordered list of `VideoFrame` (layers, back to front) plus per-layer transform / opacity / blend mode. Output: single `VideoFrame` (or direct write to a sink). The implementation runs in shader on the sink's GL context. **Size:** S (interface design).

- [ ] **`GlVideoCompositor`** in `S.Media.OpenGL`. Builds on existing `YuvVideoRenderer` plane-upload code. Supports `Source`, `Source-over`, `Multiply`, opacity, affine transform. **Size:** L. **Breaks:** nothing — additive.

- [ ] **`CompositorVideoSink`** in `S.Media.OpenGL` (or Core, if you keep the compositor interface generic). N input slots, each one an `IVideoSink`. One `IVideoSource` output that the `VideoRouter` can hand to a real display sink. **Size:** M.

- [ ] **`TextLayerSource : IVideoSource`** in `S.Media.Image` (or a sibling). GPU-side SDF font rendering preferred; SkiaSharp CPU rasterization is a fine first cut. Inputs: string + font + size + color + transform. **Size:** L.

- [ ] **Layer transitions** (fade, dip-to-black, cut) as wrapping sources that animate parameters of an underlying layer. Land after compositor primitives stabilize. **Size:** M.

**Phase exit:** PiP and lower-thirds are demonstrable; the cue player can do "PNG fades in, video plays under it, text label appears, all out one NDI sender".

---

## Phase 5 — Broadcast-grade format support

These move the framework from "playout / streaming" to "broadcast-ready". Each is sizable; sequence them only when a real consumer needs them.

- [ ] **BT.2020 / Rec.2020 color primaries.** `S.Media.OpenGL/YuvColorSpace.cs` — add the matrices; `YuvVideoRenderer` shader uniforms. **Size:** M.

- [ ] **Interlaced video.** Field metadata on `VideoFrame` (top-field-first vs bottom-field-first vs progressive), a deinterlace converter (FFmpeg `yadif` is the obvious choice; pre-wrap it in `S.Media.FFmpeg/Video/`). **Size:** L. **Breaks:** `VideoFrame` ctor — add an optional field-order parameter; existing call sites become "progressive".

- [ ] **SMPTE LTC / drop-frame timecode.** New `VideoTimecode` struct on `VideoFrame`; NDI sender writes it into the NDI timecode slot; NDI receiver reads it out. Drop-frame computation in a small helper. **Size:** M.

- [ ] **Embedded ancillary data** (closed captions, AFD, SCTE-35). Add a generic `VideoAncillary` envelope on `VideoFrame` (or a sidecar callback per frame). **Size:** L. Defer until a concrete consumer asks.

**Phase exit:** demonstrably correct ingest/playout for SMPTE-source content; the framework is plausibly usable in a broadcast environment.

---

## Phase 6 — Cleanup (do whenever)

Quality-of-life. No correctness or feature consequences; can be opportunistic.

- [ ] **Split `ChannelMap.cs` (2106 lines)** into `ChannelMap.cs` (data + small ops) + `ChannelMap.SimdAccumulate.cs` (14 SIMD shortcut methods, partial class). **Size:** S.

- [ ] **Move long-form XML `<remarks>` into `Doc/` files**, leave 3–4 lines of IntelliSense doc on the type. Worst offenders: `MediaContainerSession.cs`, `AudioRouter.cs:1-86`, `MediaContainerPlaybackBundle.cs:39-66`. **Size:** S (per type).

- [ ] **Refresh project memory** (`~/.claude/projects/-home-seko-RiderProjects-MFPlayer/memory/project_overview.md`). The 2026-05-10 snapshot predates `S.Media.Playback`, `VideoRouter`, `MediaContainerSharedDemux`, the NDI clock zoo. **Size:** XS.

- [ ] **Clean up untracked files in tree.** `git status` claims `Doc/HaPlay-Review-2026-05.md` is untracked but it doesn't exist; `UI/HaPlay/OutputPreview/PortAudioOutputRuntime.cs` is genuinely new. Either commit or delete. **Size:** XS.

- [x] **Add `Doc/NDI-Terminology.md` glossary** (Egress, Ingest, Mux, Fusion, Aggregating, Pump) if you kept the names. **Size:** XS. *(Done in Phase 1.)*

- [ ] **CI smoke-test the `Tools/` projects.** They double as documentation; make sure `dotnet build` + a no-arg run is part of CI so they don't bitrot. **Size:** S.

- [ ] **Replace `Action _release` closure on `VideoFrame` with `IVideoFrameReleaser` (struct).** Eliminates one closure allocation per dma-buf / shared-handle frame in fan-out scenarios. Profile-driven; defer until §6 of the review flags it as live cost. **Size:** S.

- [ ] **`AudioFileDecoder.TryReadNextFrame` array-pool** (S3 from §11.2). Switch `new float[]` to `ArrayPool<float>` with a release callback. **Size:** S.

---

## Suggested first sprint (1–2 weeks)

~~If picking a starting batch: **everything in Phase 0**, plus the §11.2 suspect verifications, plus **the AvRouter and MediaContainerMegaPlaybackHost renames from Phase 1**.~~ ✅ Phase 0 done. **Continue this sprint with the Phase 1 renames** (`AvRouter` → `MediaSession`, drop "Mega", delete `MediaContainerPlaybackGraph`, disambiguate the two `MediaContainerPlaybackHost`s, strip Tier-E/Tier-F annotations). The C4 pump-capacity auto-tune for `IClockedSink` also slips into this sprint or the next — it's small.

## Suggested second sprint

**Phase 2 in full + `StaticFrameSource` from Phase 3.** Small wins that immediately improve every consumer (HaPlay included once it's updated).

## Suggested third onwards

Phase 3 features in any order; pick by what's most needed for the next product milestone (cue player vs. soundboard vs. multi-output mixer). Phase 4 is a quarter-sized chunk on its own. Phase 5 only when a paying use case demands it.
