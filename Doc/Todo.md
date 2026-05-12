# Media Framework — Review & Improvement Checklist

**Last updated:** 2026-05-12 (night) — `ChannelMap` mono/duplex‑wide SIMD (SSE/AVX unpack + shuffles), `YuvVideoRenderer` ctor‑bound frame upload delegate (no per‑frame switch), `VideoPtsClock` + `VideoPlayer.FramePresentationTimePresented`, `AvPlaybackCoordinator` + `VideoPlaybackSmoke` wiring, `SinkSlavedRouterClock` / resume padding remarks. Larger items (`MediaContainerDecoder`, full `MediaPlayer` API surface) remain backlog.

**Previous update (2026-05-12 morning):** Implementation audit: every previously-checked box was verified present. §"Audit findings (2026-05-12)" called out one likely bug (P010 bit-scale), three minor bugs, four robustness/contract gaps, plus optimization and cleanup opportunities.

**Previous update (2026-05-11):** Linux NV12 DRM PRIME dma-buf → EGL/GL (`RetainDmabufForGl`, `YuvDmabufEglInterop`, `Nv12DmabufGpuUploader`); FFmpeg VAAPI zero-copy MVP.

Legend: `[x]` done, `[~]` partial / best-effort, `[ ]` intentional future work.

---

## Executive summary — where to look

| Area | Implementation |
|------|----------------|
| Extended pixel formats | `PixelFormat.cs`, `PixelFormatInfo.cs`, `VideoFileDecoder` `MapNativePixelFormat` / `ToAVPixelFormat` |
| GL recipes + shaders | `GlVideoFormatSupport.cs`, `YuvVideoRenderer.cs`, `Shaders/argb.frag.glsl`, `abgr.frag.glsl`, `gray.frag.glsl`, `yuva_planar.frag.glsl` |
| Frame transfer metadata | `VideoTransferHint.cs`, `VideoFrame.ColorTransferHint`, libav `AVFrame.color_trc` → `VideoFileDecoder.MapTransferHint` |
| FFmpeg hardware decode (CPU transfer) | `VideoDecoderOpenOptions.TryHardwareAcceleration`, `VideoHardwareDecodeContext`, `av_hwframe_transfer_data` |
| Linux NV12 DRM PRIME → GL | `VideoDecoderOpenOptions.RetainDmabufForGl`, `VideoFrame.DmabufNv12` / `CreateNv12Dmabuf`, `DrmPrimeNv12BackingFactory`, `YuvDmabufEglInterop`, `Nv12DmabufGpuUploader` (incl. **`EGL_EXT_image_dma_buf_import_modifiers`**), `SDL3GLVideoSink` + `YuvVideoRenderer` |
| SDL GL HDR | `SDL3GLVideoSink.ApplyTransferHintToRenderer` + optional `GlVideoSinkHdrPreference` / `HdrPreference` property |
| Router SIMD (stereo+) | Stereo identity/swap, **mono `[0,0]` → duplex**, **`[0,1,0,1]` 2→4 widen** (`ChannelMap.TryAccumulate*`), `AudioRouter.ApplyRoute` |
| libav resampling | `AudioResampler` (`swresample`) for packed float audio |
| Thread join slicing | **`CooperativePlaybackJoin`** (`S.Media.Core.Threading`) — **`MediaClock`**, **`VideoPlayer`**, **`AudioRouter`**, **`SinkPump`**, **`SDL3*VideoSink`**, **`NDIAudioReceiver`**; short-slice **`MIDIInputDevice.Close`**, **`OSCServer.Dispose`** |
| Clock / player join with cancel | **`IMediaClock.Pause` / `Stop`**, **`VideoPlayer.StopInternal`** observe **`CancellationToken`** while draining decode / driver threads (via **`CooperativePlaybackJoin`**)
| Router stop + cancel | `AudioRouter.Stop(CancellationToken)` cooperatively joins the mixer thread between short sleeps |
| Zero-copy video interop (stub) | `IHardwareVideoInterop`, **`HardwareVideo*Descriptor`** structs, default `TryDescribeImportedSurface`, **`NoOpHardwareVideoInterop`** |
| NDI optional wall-clock pacing | `NDIOutput` ctor `minimumVideoSubmitSpacing`, `NDIVideoSender.PaceBeforePack` |
| A/V coordinated playback | **`AvPlaybackCoordinator`** (`S.Media.Core.Playback`), **`VideoPtsClock`** (`IPlaybackClock` for PTS + wall), **`VideoPlayer.FramePresentationTimePresented`** |
| Concept / format matrix docs | `Doc/MediaFramework-concepts.md`, `Doc/PixelFormats-OpenGL.md` |

---

## 1. OpenGL renderer (`S.Media.OpenGL`)

### 1.1–1.4

- [x] Prior correctness / architecture items unchanged (unpack restore, viewport fit, HDR uniforms, samplers, native pointer upload, etc.).
- [x] **Extended format coverage** — `Argb32`, `Abgr32` (FFmpeg memory order + swizzle shaders), `Gray8`/`Gray16` (`gray.frag.glsl`), `Yuv420P10Le` / `Yuv420P12Le` / `Yuv444P10Le` (planar R16 + `yuv_planar.frag.glsl` bitScale), `Yuva420p` (`yuva_planar.frag.glsl` + fourth alpha plane).
- [x] **Hardware GL decode (Linux NV12)** — EGL `EGL_EXT_image_dma_buf_import` (+ **`EGL_EXT_image_dma_buf_import_modifiers`** when non-zero **`DRM_FORMAT_MOD_*`**) + GL `GL_EXT_EGL_image_storage`, split Y (`DRM_FORMAT_R8`) / UV (`DRM_FORMAT_GR88`). **non-NV12 PRIME** / multi-planar FFmpeg layouts remain backlog §Suggested backlog below.

---

## 2. Core video (`S.Media.Core`)

- [x] **Argb32 / Abgr32** — enum + `PixelFormatInfo` plane metadata + alpha flag.
- [x] **Gray8 / Gray16**, **420 10/12**, **444 10**, **YUVA420P** — descriptors in `PixelFormatInfo`; tests in `PixelFormatInfoTests`.
- [x] **NV12 dma-buf metadata** — `VideoDmabufNv12Backing`, `VideoFrame.DmabufNv12` / `CreateNv12Dmabuf` (CPU `Planes` are empty stubs on that path).
- [x] **Hardware video interop surface contract** (`IHardwareVideoInterop`, `HardwareVideoSurfaceDescriptor`, `NoOpHardwareVideoInterop`) — concrete import adapters remain future work (dma-bufs use `VideoFrame.DmabufNv12` directly today).

---

## 3–5. FFmpeg / NDI / SDL3

- [x] **FFmpeg mappings** for new `PixelFormat` values where libav exposes a stable `AV_PIX_FMT_*`.
- [x] **`color_trc` → `VideoTransferHint`** per frame on pass-through and sws output paths.
- [x] **`SDL3GLVideoSink`** applies hint to `YuvVideoRenderer.HdrTransfer`; optional `GlVideoSinkHdrPreference` overrides or ignores per-frame hints.
- [x] **NDI pacing** — optional minimum spacing between submits (pairs with SDK `clockVideo:false` scenarios).
- [x] **Hardware FFmpeg decode** — VAAPI / D3D11VA / QSV / … via libav; **Linux VAAPI** zero-copy path uses `RetainDmabufForGl` → `drm_prime` + `VideoDmabufNv12Backing` → GL. **`IHardwareVideoInterop`** adapters for DXGI/Metal/etc. remain future work (stub contract + backlog).
- [x] **Resampling helper** — `AudioResampler` wraps `swresample` for packed float interleaved audio (see also `AudioFileDecoder` internals).

---

## 10. Cross-cutting

### 10.2 Cancellation

- [x] **`IMediaClock.Pause` / `Stop`** accept `CancellationToken` while joining the driver thread.
- [x] **`VideoPlayer.Pause` / `Stop`** accept token while joining decode thread.
- [x] **`AudioRouter.Stop`** uses short `Thread.Join` slices and honors `CancellationToken` during the join loop.
- [x] **Blocking shutdown paths** — short-slice cooperative joins (**`CooperativePlaybackJoin`** playback stack); **`AudioRouter`** / **`SinkPump`** / SDL / NDI patterns; **`MIDIInputDevice.Close`** (wake + threaded join slices); **`OSCServer.Dispose`** (task **`Wait`** slices after cancel).

### 10.3 Testing

- [x] **Transfer hint mapping tests** (`VideoTransferHintMappingTests`), **pixel layout tests** (`Yuva420p`, gray high depth, expanded alpha carriers).
- [x] **`ChannelMap`** — SIMD regressions (**`StereoSimd_*`**, **`MonoDupSimd_*`**, **`StereoDuplexWideSimd_*`**) plus seeded **`Apply_RandomLayouts_Deterministic_MatchNaive`**; **`AudioResampler`** (identity/up + **`Resampler_RandomishBuffer`**); **`HardwareVideoInteropTests`**. Full property/fuzz harness — optional backlog.

### 10.4 Docs

- [x] **Concept overview** — `Doc/MediaFramework-concepts.md`.
- [x] **Pixel format × GL mapping** — `Doc/PixelFormats-OpenGL.md`.

---

## Suggested backlog (remaining)

1. **`IHardwareVideoInterop` implementations** (DXGI / Metal / Vulkan, …) mapping **`HardwareVideoSurfaceDescriptor`** — Linux libav may already populate **`VideoDmabufNv12Backing`** (`RetainDmabufForGl`) without going through `IHardwareVideoInterop`.
2. SIMD for remaining hot **`ChannelMap` shapes** (`MonoToN` with `N > 2`, asymmetric silence maps, …) after profiling.
3. Dedicated fuzz / property-test harness (many random route graphs + gain ramps + decoder soak).

---

## Audit findings (2026-05-12)

Original morning entries reproduced below with the working-tree status applied: every "Likely bug" and "Robustness / contract gap" is now `[x]` (resolved in the working tree) except `AudioRouter.Pause` which is `[~]` (documented but race not closed). The optimization and cleanup lists are mostly resolved; the unresolved ones are kept as `[ ]` so they remain on the radar.

### Likely bugs

- [x] **P010 `bitScale` over-amplifies** — fixed in `GlVideoFormatSupport.cs`: `P010` recipe now uses `bitScale = 1f` with a comment explaining the 10-bit-in-high-bits storage layout.

- [x] **`Nv12DmabufGpuUploader.TryUpload` ignores GL errors after `glEGLImageTargetTexStorageEXT`** — fixed: `_gl.GetError()` is checked after each plane upload; failure short-circuits the second `glEGLImageTargetTexStorageEXT` and propagates `false` to the caller. The two `EGLImage` handles are destroyed inline regardless of success (no leak on the failure path).

- [x] **EGLImages are leaked between frames** — fixed: `_yImage` / `_uvImage` fields are gone. The two images are destroyed immediately after the `glEGLImageTargetTexStorageEXT` calls in the same `TryUpload`.

- [x] **Dead inner guard in `Nv12DmabufGpuUploader.AppendPlaneAttribs`** — removed (the modifier-availability check now lives only in `TryUpload`).

### Robustness / contract gaps

- [x] **`AvDrmFrameDescriptorInterop` size hard-coded to 528** — fixed: `WarnIfInteropSizeMismatchLp64LoggedOnce` runs once per process from `DrmPrimeNv12BackingFactory` and logs via `MediaDiagnostics.LogWarning` if `Marshal.SizeOf<AvDrmFrameDescriptorInterop>()` disagrees with `ExpectedSizeBytes`.

- [x] **`DrmPrimeNv12BackingFactory` modifier preference is surprising** — fixed: `VideoDmabufNv12Backing` now carries `YPlaneDrmFormatModifier` + `UvPlaneDrmFormatModifier` independently (with a `UsesDistinctDmaBufObjects` convenience). `AppendPlaneAttribs` is invoked per-plane, so `DRM_FORMAT_MOD_LINEAR == 0` no longer collides with "missing modifier."

- [x] **`VideoFileDecoder.NativePixelFormats` is empty when HW decode runs without DRM PRIME** — fixed: the hw-software fallback now advertises `PixelFormat.Nv12` (matching VAAPI/D3D11VA layouts after `av_hwframe_transfer_data`) plus a docstring note. Sinks that accept NV12 can now negotiate around the BGRA32 sws path.

- [~] **`AudioRouter.Pause` re-enters the lock to enumerate sinks for `Flush`** — partially addressed: added a `<remarks>` paragraph documenting the assumption ("callers invoke `Pause` from the same synchronization domain that owns routing"). The race is documented but not closed by combining the two lock blocks — leave open until profile shows it matters.

### Optimization candidates

- [x] **`VideoFileDecoder.BuildConvertedFrame` allocates a fresh `byte[]` per frame** — fixed: rents from `ArrayPool<byte>.Shared`, releases via the existing `VideoFrame.release` callback. Source/dest scratch arrays are now per-decoder fields (`_swScaleSrcLines` / `_swScaleSrcStride` / `_swScaleDstLines` / `_swScaleDstStride`) — single-threaded use is annotated in a remark.

- [x] **`VideoFileDecoder.BuildPassThroughFrame` … per-frame arrays** — addressed: pooled `ReadOnlyMemory<byte>[]` / `int[]` (keyed by `planeCount`, cap 32 each), returned from the AVFrame-backed frame’s `release` callback (dispose may run off the decode thread).

- [x] **`ChannelMap.StereoSwapAdjacentChannels` … `WithElement` chain** — addressed: stack buffer + pairwise swap + `MemoryMarshal.Cast` reconstructs the vector.

- [x] **`ChannelMap.TryAccumulateMonoDupStereoInterleaved` and `TryAccumulateStereoDuplexWideInterleaved` fan out …** — addressed with SSE/AVX fast paths (`Sse`/`Avx` unpack and `0x44`/`0xEE` shuffles) plus generic `Vector<float>` scratch fallback when lane widths differ.

- [x] **`NDIVideoSender.PaceBeforePack` rounds wait to whole milliseconds** — fixed: coarse `Thread.Sleep` (minus 1 ms) plus a `Stopwatch`-deadline `SpinWait(32)` busy-wait for the sub-millisecond remainder. Pacing is now precise to sub-ms.

### Simplifications / cleanup

- [x] **`VideoHardwareDecodeContext.IsActive` is unreferenced** — deleted.
- [x] **`AudioRouter.CooperativeJoin` and `VideoPlayer.TryJoinDecodeThread` are 1-line proxies** — inlined. The helper indirection is gone.
- [x] **`YuvVideoRenderer.DispatchUploadFromFrame` 17-arm switch** — addressed: ctor-bound `Action<VideoFrame>` from a `switch` expression (`CreateUploadFromFrameDelegate`); per-frame dispatch is a single delegate invoke.
- [x] **`MIDIInputDevice.Close` and `OSCServer.Dispose` reimplement `CooperativePlaybackJoin`** — `<remarks>` blocks now document the intentional duplication. ("PMLib/OSCLib stay free of any S.Media.Core dependency for thread joins.")
- [x] **`SDL3GLVideoSink.Submit` swallows `_disposed` silently** — fixed: now throws `ObjectDisposedException`. `SDL3VideoSink.Submit` standardized the same way. `VideoPlayer.OnVideoTick`'s try/catch already disposes the frame on rethrow, so no frame leak.
- [x] **`try { … } catch { /* best effort */ }` blocks** — `VideoPlayer.OnVideoTick` Submit catch, `SDL3*VideoSink.Pump` + `RenderLoop` PresentFrame catches, and the `SafeRaise*` handlers now log via `MediaDiagnostics.LogError`. Silent glitches are now diagnosable.

---

## VideoPlaybackSmoke audit (2026-05-12 PM)

`MediaFramework/Tools/VideoPlaybackSmoke/` is a new smoke test that opens a media file, plays its video through SDL3 (GL or CPU) optionally mirrored over NDI, and slaves the video clock to PortAudio's playback clock. Below is a focused audit of (1) the tool itself, (2) likely root causes of the audio dropouts the user heard with 720p24 NV12 content, and (3) a roadmap to combining the audio and video paths under a single clock-aware facade.

### Tool-level cleanup

- [~] **Unused `using System.Threading;` in `VideoDmabufNv12Backing.cs`** — **not removed**: `Interlocked` in `Dispose` still lives in `System.Threading` (stale audit line).

- [x] **`TwinCpuVideoSink.Dispose` / ownership** — class `<remarks>` document primary vs secondary; `Dispose` only closes the window sink.

- [x] **`TwinCpuVideoSink.DuplicateCpuBackedFrame` heap `byte[]` per plane** — `ArrayPool<byte>.Shared` + `VideoFrame.release` return (failure path returns too).

- [x] **`PlaybackCli` usage vs `--ndi` + `--drm-gl`** — help text states they are mutually exclusive up front.

- [x] **Probe `PortAudioOutput` in `TryCreate`** — removed (device validation happens on the real output ctor).

- [x] **Status-line dropout diagnostics** — prints `vLate`, `paUnd`, `paDr`, `pumpDr`, show/decoded counts.

### Audio dropouts — likely root causes (720p24 NV12)

A few independent suspects, ordered from most to least likely:

- [x] **Startup ordering / prebuffer** — `VideoPlaybackSmoke`: **decoder-direct** `PrefillMainOutputDirectFromDecoder` (into PortAudio only) → `StartHardwareOutput()` → `AudioPlayer.Play()`. Running the router before the device is open made `WaitForCapacity` a no-op and filled/dropped the ring while `AudioFileDecoder.Position` raced ahead.

- [~] **Dedicated `Prefill` on `PortAudioOutput` / `AudioPlayer`** — smoke tool has `PrefillHardwareRing` locally; moving it into the library is optional.

- [x] **`SinkPump` thread priority** — drainer thread is now `AboveNormal` (matches router producer).

- [x] **Default `chunkSamples`** — `VideoPlaybackSmoke` default **960** (20 ms @ 48 kHz).

- [x] **First-chunk silence-padding on resume** — documented on `AudioRouter.Resume` (`<remarks>`: partial-read silence pad on first chunk after `Pause`).

### Audio + video clock unification — proposal

Today's situation:
- `MediaClock` is the visible playhead, optionally mastered to an `IPlaybackClock`.
- `PortAudioOutput` implements both `IClockedSink` (paces the audio router) and `IPlaybackClock` (exposes `PlayedSamples / SampleRate` as elapsed-since-start). `AudioPlayer.AddOutput` auto-wires both when present.
- `VideoPlayer` subscribes to `IMediaClock.VideoTick` and uses `IMediaClock.CurrentPosition` to pick the most recent in-window frame.
- `VideoFileDecoder` provides each frame's libav PTS as `VideoFrame.PresentationTime` (the "PTS clock" the user mentioned, indirectly).

So **A/V sync to PortAudio's hardware clock already works** when the master is wired. What's *not* unified:

- [x] **No single facade that owns Play / Pause / Stop / Seek for both** — `AvPlaybackCoordinator` (`S.Media.Core.Playback`) centralizes ordered Play / Pause / Stop / Seek for `AudioPlayer` + `VideoPlayer`; `VideoPlaybackSmoke` uses it after prefill/`StartHardwareOutput`.
- [x] **No `PtsClock` for video-only / live playback** — `VideoPtsClock` implements `IPlaybackClock` (PTS anchor + wall delta); wire via `VideoPlayer.FramePresentationTimePresented` or call `NotifyFramePts` yourself.
- [ ] **Two `AVFormatContext`s per AV file** — `VideoFileDecoder.Open(path)` and `AudioFileDecoder.Open(path)` each parse and demux the container independently. For typical mp4/mkv playback this means twice the file IO, two read pointers that drift across long files, and seek must hit both. A future `MediaContainerDecoder` that demuxes once and routes packets to per-stream decoders would: (a) halve IO, (b) keep audio/video PTS aligned at the demuxer, (c) enable a single seek that atomically repositions both. Larger lift — flag as backlog only.
- [~] **`SinkSlavedRouterClock` falls back to `WallClockRouterClock` if the slaved sink is removed** — fine for graceful degradation, but the fallback wall-clock instance is created once with the original sample rate / chunk size; a later sample-rate change wouldn't propagate. Today the router throws if sample rates disagree on `AddSource`, so this is dormant — `SinkSlavedRouterClock` `<remarks>` now call this out for future dynamic resampling work.

### Verification once changes land

After fixing the dropout suspects, the smoke tool should print, after a 30-second playback of a 720p24 NV12 file with audio:

- `mainOutput.UnderrunSamples` near 0 (a few hundred at start is tolerable; thousands means prebuffer is still wrong).
- `routing.Player.Router.GetPumpStats(<sinkId>).Dropped == 0` (any non-zero means the pump is being preempted — recheck thread priorities / pumpCapacityChunks).
- `videoPlayer.DroppedLate < 0.5%` of `videoPlayer.DecodedCount`.
- Clock and `vPTS` agree within ±1 frame (≈ 41 ms at 24 fps).

---

## Audit findings (2026-05-12 evening)

Re-audit after the late-afternoon set of fixes (SIMD/AVX2 in `ChannelMap`, ctor-bound renderer dispatch, pooled passthrough arena, `SinkPump` `AboveNormal`, new `VideoPtsClock` + `AvPlaybackCoordinator`, decoder-direct prefill + 20 ms chunks + HUD in the smoke tool). The big-ticket items are landed; the notes below cover (i) the user's report of "audio jumps slightly at times" on local playback, (ii) the "NDI audio doesn't look healthy" observation in NDI Monitor (video stays green and centered), and (iii) gaps and minor regressions in the new code.

### Local audio "jumps" — likely root causes

The new `PrefillMainOutputDirectFromDecoder` + 20 ms chunks + `SinkPump` `AboveNormal` removes most of the prior suspect chain. The remaining suspects:

- [ ] **`TargetQueueSamples` cushion is tight** — `VideoPlaybackSmoke` sets it via `Math.Clamp(chunkSamples * 8, chunkSamples * 4, output.CapacitySamples / 8)` (`Program.cs:212-215`). At default 960-sample chunks the value lands on **7680 samples ≈ 160 ms**. The ring itself is 1.37 s deep, but the producer is *capped* to that 160 ms target — meaning the run loop has ~160 ms of headroom before PortAudio starts seeing underruns if anything stalls the producer (decoder cluster I/O, full Gen-1 GC, transient thread preemption). Bump to ~300–500 ms (e.g. `chunkSamples * 16` lower bound and `CapacitySamples / 3` upper) for pre-recorded media; keep 160 ms only when latency really matters. The HUD's `paUnd` / `paDr` counters will tell you which side is wrong.
- [ ] **`PortAudioOutput.TargetQueueSamples` clamp ignores `CapacitySamples` lower bound** — when `output.CapacitySamples / 8 < chunkSamples * 4`, the clamp produces a value *below* the requested 4-chunk floor (the clamp's `max` is < its `min`, which `Math.Clamp` actually throws on in BCL but here both bounds depend on `chunkSamples`; for the smoke tool's defaults it works out, but a smaller ring or larger chunk would explode at runtime). Defensive fix: clamp `Math.Max(chunkSamples*4, Math.Min(chunkSamples*8, output.CapacitySamples / 8))` (or just guard with `Math.Min` first).
- [ ] **PortAudio's default suggested latency is `defaultHighOutputLatency`** — fine for PulseAudio (≈ 50 ms), but on JACK or ALSA-hw it can be longer; the 160 ms producer target may be smaller than the device's own buffer fill rate. Worth exposing a `--device-latency-ms` knob (or auto-derive `TargetQueueSamples = max(8 * chunkSamples, 4 * device.suggestedLatency * sampleRate)`).
- [ ] **No backpressure-aware decoder catch-up on Pause→Resume** — `AudioRouter.Resume` doc notes "first chunk may be silence-padded" (per `<remarks>`), which on a fresh `swr` queue at resume produces an audible micro-gap. For seek/resume-heavy workflows, call `Decoder.ReadInto(scratch)` once before `Player.Play()` to prime the `swr` queue, or expose a `Prefill` on the router itself.
- [ ] **`PortAudioOutput.Submit` silently drops on full ring** (`PortAudioOutput.cs:243-248`). When `WaitForCapacity` does its job this never fires, but during the brief window before the device stream is started or right after a Pause→Resume the ring can fill faster than callbacks drain. The `DroppedSamples` counter now surfaces in the HUD — verify it stays zero. If it ticks up, the prefill is overshooting.
- [ ] **`AudioRouter.RunLoop` allocates a new `float[]` per pumped chunk when the free-pool is exhausted** (`Commit`, `AudioRouter.cs:807`). Under sustained backpressure (e.g. NDI sink slow + 8-chunk capacity exceeded), the producer thread allocates a fresh buffer per chunk — that's Gen-0 traffic on the audio thread, which can cascade into latency spikes. Already counted as a drop via `RecordDrop`, but the allocation itself is silent. Switch to "drop without allocating" (don't rotate buffers when out of pool — reuse the current `_working` directly) and audit whether the silent re-allocation path is even desirable.

### NDI audio "doesn't look healthy" — root-cause shortlist

`VideoPlaybackSmoke` plumbs NDI audio as `routing.Player.AddOutput(ndSink)` (`Program.cs:85`). The NDI sink shares the same audio router as PortAudio, but with several mismatches:

- [ ] **NDI audio inherits 20 ms chunk cadence (50 Hz frame rate)** — NDIAudioSink sends one NDI audio frame per router chunk via `NDIAudioUtils.SendInterleaved32f`. NDI Monitor's audio meter expects a steady cadence; the router's `WaitForCapacity` sleeps in millisecond increments (`Math.Ceiling` rounds up), so the audio frame inter-arrival oscillates ±1 ms around 20 ms — visible as a wobbling buffer level in the NDI Monitor display. Fixes:
  - **Aggregate router chunks into larger NDI audio frames** (e.g. 4–5 chunks ≈ 80–100 ms): wrap `NDIAudioSink` in a per-sink accumulator that flushes when ≥ N samples are buffered; submit one `NDIlib_util_send_send_audio_interleaved_32f` per video-frame period instead of 50 Hz.
  - **Stamp real timecodes** instead of the `0x7FFFFFFFFFFFFFFF` "synthesise" sentinel (`NDIAudioSink.cs:65`). Maintain a sample counter and emit `Timecode = (long)(_samplesSent * 10_000_000 / sampleRate)` (NDI uses 100-ns ticks). With a real timecode the receiver can absorb cadence jitter against the stamped time, not against arrival time.
- [ ] **NDI audio starts ~prefill-duration *after* PortAudio audio** — `PrefillMainOutputDirectFromDecoder` consumes the decoder and writes directly into PortAudio's ring. The router (and therefore NDIAudioSink) only starts producing *after* the prefill consumed those samples. Result: NDI receivers see the audio stream begin ~100 ms after they would have seen video, which throws their A/V sync heuristic. Mitigations:
  - Route prefill through `AudioRouter` (e.g. add a "prebuffer mode" where the router runs `WaitForCapacity` checks while sinks are silent, then enables sink output once the ring hits target) so every sink sees the same starting sample.
  - Or send the prefill content as NDI audio too: emit one fat NDI audio frame containing the prefill block from the decoder before starting the router.
- [ ] **NDI audio sink shares `pumpCapacityChunks=8`** with PortAudio — at 20 ms chunks that's 160 ms of audio backlog before chunks are dropped. NDI's SDK serializes sends through a single sender; if a video frame is mid-send (`SendVideoAsync` returned but the buffer is still in flight on the NIC), the audio frame queues behind it. With 720p24 video frames the burst pressure isn't huge, but on a busy network the NDI audio pump *can* hit its cap and start `RecordDrop`-ing. The HUD only shows the *primary* sink's `pumpDr`; add an NDI-specific `pumpDr` line, or expose per-sink stats and log when any non-primary sink starts dropping.
- [ ] **NDI sender is created with `clockAudio: false`** in `VideoPlaybackSmoke` (`Program.cs:48-49`) yet there's no internal MFPlayer-side pacer for NDI audio. With `clockAudio: true` the SDK would smooth out the cadence on its side; with `false` and no manual pacer, jitter goes straight to the receiver. Either flip `clockAudio` back on for the smoke tool (NDI throttles on the SDK side, which is what NDI Monitor expects to see), or add a `minimumAudioSubmitSpacing` to `NDIAudioSink` mirroring `NDIVideoSender.PaceBeforePack` (with the same sub-millisecond `SpinWait` trick).
- [ ] **`NDIAudioSink.Submit` allocates / frees a native buffer only when capacity grows** (`EnsurePackedCapacity`) — fine for steady chunk sizes. But the first ~1 second of playback may bump the capacity 1–2 times if upstream chunk sizes shift; not a dropout cause, but worth noting that the first reallocation happens on the pump thread under the audio time budget.
- [ ] **NDI receivers handle audio at NDI video frame cadence by default** — even if you fix the timecode and chunk size, NDI's documented best practice is to send audio in blocks that match the video frame period (1/24 s = 41.67 ms ≈ 2000 samples @ 48 kHz). Aim there once the aggregator above lands.

### `VideoPtsClock` integration — wired but not used

`VideoPtsClock` (`S.Media.Core/Clock/VideoPtsClock.cs`) ships with unit tests but **nothing in the framework subscribes `FramePresentationTimePresented` to drive it**. `VideoPlaybackSmoke` always uses `routing?.Player.Clock ?? freerunClock` — when there's no audio, `freerunClock` is the plain `MediaClock` stopwatch fallback, not a `VideoPtsClock`. Two things to address:

- [ ] **Wire `VideoPtsClock` in audio-less playback** — when `routing` is null, construct a `VideoPtsClock`, hook `videoPlayer.FramePresentationTimePresented += clock.NotifyFramePts`, and use it as the `MediaClock` master via `mediaClock.SetMaster(videoPtsClock)`. `BeginSession` needs the first frame's PTS — easiest to call it from inside the first `FramePresentationTimePresented` invocation (guarded by a flag).
- [ ] **`AvPlaybackCoordinator` doesn't help with this either** — its `Play(video, audio: null, ...)` takes no clock parameter, so consumers can't ask it to use `VideoPtsClock` for the video-only path. Either add a `IPlaybackClock? videoOnlyMaster` parameter, or document that wiring `VideoPtsClock` is the caller's job.

### New code — minor regressions / cleanups

- [ ] **Unused `using System.Collections.Generic;` in `VideoFileDecoder.cs:2`** — `Stack<>` and `Dictionary<>` live in `System.Collections.Generic`, but the file already has `ImplicitUsings = enable` at the project level. The `using` line is redundant; either remove or keep with a comment.
- [ ] **`VideoFileDecoder._passThroughArena` lock spans `Stack<>.Push/Pop` and `Dictionary<>.TryGetValue`** — adding a contention point on the audio/video time budget. With single-threaded `TryReadNextFrame` the lock is uncontended on the producer side; the contention is the *frame release callback* (which can run on the sink's render thread). At 24 fps the contention is rare, but worth noting that disposing a backlog of late frames at once will serialize on this lock. `ConcurrentStack<>` per plane-count plus `ConcurrentDictionary` would remove the lock; the cap-32 enforcement gets `Interlocked.Increment` / `Decrement` instead.
- [ ] **`VideoFileDecoder.Close` clears the passthrough stacks but doesn't null out the AVFrame metadata held in the cached `ReadOnlyMemory<byte>[]`** — the elements are reference-type assignments (`planes[i] = …`) overwritten on every rent; cached arrays carry stale `ReadOnlyMemory<byte>` instances pointing to freed `AVFrame.data` pointers between rents. The bug is dormant because `RentPassThroughDescriptors` overwrites every slot before returning, but a defensive `Array.Clear` on push (or on pop) would prevent a future caller from observing stale memory if the cap-32 ever gets bypassed.
- [ ] **`AvPlaybackCoordinator.Play` order** — `prefillBeforeHardware → startHardware → audio?.Play → video.Play` matches what the smoke tool wants, but the `AudioPlayer.Play` step doesn't internally prebuffer — that's the caller's job via `prefillBeforeHardware`. Two minor improvements: (a) accept a `Func<TimeSpan, bool>? waitForPrebuffer` callback so the coordinator can verify the prebuffer succeeded before opening the device; (b) reverse the Pause order on cancel: today it does `video.Pause` then `audio?.Pause` — if `video.Pause` throws, audio is still running. Wrap in `try { video.Pause } finally { audio?.Pause }`.
- [ ] **`AvPlaybackCoordinator.Seek` order** — `audio?.Seek(position); video.Seek(position);` is correct (audio clock first), but neither pauses first. `AudioPlayer.Seek` does its own pause/resume internally; `VideoPlayer.Seek` is a self-contained pause/seek/resume; but during the brief window where audio is paused and video isn't, the video sink keeps presenting frames from the old position. Document the inter-leaving, or wrap both in a single Pause/Resume bracket inside the coordinator.
- [ ] **`SinkSlavedRouterClock` fallback creation eagerness** — fixed in this pass (`<remarks>` documents the assumption). But the underlying construct is still: a fallback `WallClockRouterClock` created once at `SlaveTo` time. If the slaved sink is later removed and the router replaces it with a different-sample-rate sink, the fallback wall clock would tick at the old rate during the brief unhinged window. The fix is to construct the fallback lazily on miss (or pass a `Func<IRouterClock>` factory). Track as backlog under "dynamic resampling support."
- [ ] **`ChannelMap.TryAccumulateMonoDupStereoInterleaved` / `TryAccumulateStereoDuplexWideInterleaved` AVX paths assume `Vector<float>.Count == 8`** — true on AVX2 (which is what `Avx.IsSupported` implies), but `Vector<float>.Count` can be 16 on AVX-512 hardware where `Avx512F` is active. On such a CPU, `vn == 8` would be false and we'd fall through to the SSE branch — fine functionally, but suboptimal. Either gate on `Vector<float>.Count` directly (`vn == 8 || vn == 16` with the AVX-512 variant) or accept the AVX-fallback on AVX-512 hardware (documented as such).
- [ ] **`VideoPlayer.FramePresentationTimePresented`** runs **inside the MediaClock driver thread's `OnVideoTick`** — same thread that fires audio ticks and position-changed events. Subscribers that do heavy work (e.g. recalc a UI overlay) will delay subsequent ticks. Worth a remark on the event noting "fires on the clock-driver thread; marshal to your own context if you need to do non-trivial work."

### Quick verification when investigating the user's audio reports

For local "audio jumps":
1. Run `VideoPlaybackSmoke <file> --hw` (no `--ndi`) for 60 s.
2. Read the HUD's final `paUnd` / `paDr` / `pumpDr`. If `paUnd > 0` mid-stream → producer stall (raise `TargetQueueSamples`). If `paDr > 0` → router writing faster than PA drains (shouldn't happen after prefill). If `pumpDr > 0` → pump preempted (shouldn't happen with `AboveNormal`).
3. If all three are 0 but the user still hears jumps, the source is likely PortAudio's audio thread itself (Pulse server jitter, ALSA period underrun). Try `defaultLowOutputLatency` or pass an explicit `framesPerBuffer` matching the ALSA period.

For NDI Monitor audio health:
1. Run `VideoPlaybackSmoke <file> --ndi MFPlayer` (without `--drm-gl`).
2. On the receiver side, run `mediainfo` against the NDI source or watch NDI Monitor's audio waveform meter for cadence wobble.
3. If video stays centered green and audio shows uneven bars: the audio frame cadence is the issue. Try flipping `clockAudio: true` in `NDIOutput` ctor (smoke tool currently passes `false`).
4. If `clockAudio: true` smooths it, that confirms the audio-side pacing gap — apply the aggregator + real timecode fix in the §"NDI audio" list above to get the same effect with `clockAudio: false`.
