# Media Framework — Review & Improvement Checklist

A consolidated list of bugs, design issues, and improvement opportunities
discovered during a deep review of the framework. Each item is independently
actionable; ordering inside a section is roughly highest impact first.

Severity legend:
- 🐞 Bug / correctness issue
- ⚠️  Risk / latent issue (works today, will bite later)
- ⚡ Performance
- 🧩 API / design
- 📐 Coverage gap (functionality not yet implemented)
- 📝 Docs / hygiene

---

## 1. OpenGL renderer (`S.Media.OpenGL`)

### 1.1 Correctness bugs

- [ ] 🐞 **Odd dimensions truncate.** `PlaneTextureSize` in `YuvVideoRenderer`
  uses integer `/2` for chroma sizing. For a 1281×721 I420 source this
  under-sizes chroma textures by one row/column. Use `(w + 1) / 2`,
  `(h + 1) / 2`. Apply identical fix in `PixelFormatInfo.PlaneHeight` and
  `PixelFormatInfo.PlaneByteWidth`.
- [ ] 🐞 **GL pixel-store state leaks.** `glPixelStorei(UNPACK_ALIGNMENT,
      UNPACK_ROW_LENGTH)` is mutated on every upload and never reset. If the
  surrounding application uses GL elsewhere (Avalonia, ImGui, custom),
  it will read stale values. Either snapshot/restore around the upload,
  or document that the renderer owns these state bits.
- [ ] 🐞 **`Render()` does a redundant `glClear`.** The full-screen triangle
  overwrites every pixel; the clear is dead work and hurts tile-based
  GPUs in particular. Drop it (or expose as opt-in).
- [ ] ⚠️ **BGRA upload assumes little-endian.** `GL_BGRA + GL_UNSIGNED_BYTE`
  into `RGBA8` only matches B,G,R,A byte order on LE machines. All
  current targets are LE — add a comment to prevent a future BE port
  from silently shipping swapped colours.
- [ ] ⚠️ **Y-flip is baked into the vertex shader.** Works for FFmpeg/NDI
  sources (row 0 on top). Will break for sources that need bottom-up
  orientation (FBO color attachments). Add a `bool yFlip` uniform or a
  tiny 2×2 NDC transform.
- [ ] ⚠️ **`SetUnpackAlignment` ignores `visibleBytes`.** Parameter is dead;
  remove it or actually act on it. Cap derived alignment at 8 (GL maximum).
- [ ] ⚠️ **R16 path assumes even stride.** 10-bit `UploadPlanarR16` divides
  stride by 2; assert `strideBytes % 2 == 0` to catch malformed sources
  early.

### 1.2 Pixel-format coverage (the main ask)

- [ ] 📐 Add **`Yv12`** — same shader as I420 with U/V samplers swapped.
- [ ] 📐 Add **`Nv21`** — same shader as NV12 with `uv.r/uv.g` swapped.
- [ ] 📐 Add **`Rgba32`** — `RGBA8`, `RGBA, UNSIGNED_BYTE`.
- [ ] 📐 Add **`Rgb24` / `Bgr24`** — `RGB8`, `RGB`/`BGR, UNSIGNED_BYTE`,
  alignment 1.
- [ ] 📐 Add **packed `Uyvy` / `Yuyv`** — upload as `RG8` (or `RGBA8` at
  half-width) and decode in a small dedicated fragment shader.
- [ ] 📐 Add **`Yuv422P` (8-bit planar 4:2:2)** — reuses `yuv_planar` with
  `bitScale = 1.0`, just new plane sizing.
- [ ] 📐 Add **`Yuv444P` / `Yuv444P10Le`** (ProRes 4444, J2K) — chroma at
  full width and height.
- [ ] 📐 Add **`P010` / `P016`** (HEVC/AV1 hardware decode output) — NV12
  layout with `R16`/`RG16` textures and `bitScale = 65535/1023` (or
  `1.0` for P016).
- [ ] 📐 Add **`Yuv420P10Le`, `Yuv420P12Le`, `Yuv422P12Le`** — variants of
  the existing planar shader with the right bit-scale.
- [ ] 📐 Add **YUV with alpha plane** (e.g. `Yuva420P`, ProRes 4444 alpha).
  Requires a fourth sampler and shader path; output uses `vec4` for the
  alpha channel.
- [ ] 📐 Add **`Gray8` / `Gray16`** (mono cameras, depth) — `R8`/`R16` and
  a trivial luminance shader.

### 1.3 Renderer architecture / API

- [ ] 🧩 **Replace 5 switch expressions with one `GlFormatRecipe` record.**
  Today adding a format means editing `ShaderNames`,
  `SamplerUniformNames`, `PlanesNeeded`, `PlaneTextureSize`,
  `PlaneTextureFormat` in lockstep. Collapse into one table:
  ```csharp
  private readonly record struct GlFormatRecipe(
      string Vert, string Frag, string[] Samplers,
      int Planes,
      Func<VideoFormat, int, (int w, int h)> PlaneSize,
      Func<int, (GlInternalFormat, GlPixelFormat, GlPixelType)> PlaneTex,
      float BitScale, bool HasAlphaPlane, bool ChromaIsInterleaved);
  ```
- [ ] 🧩 **Expose `IsFormatSupported(PixelFormat)`** so
  `VideoFormatNegotiator` doesn't maintain a duplicate hardcoded list.
- [ ] 🧩 **`Render(int x, int y, int w, int h)` overload** for letter/pillar
  boxing (the existing doc-comment promises it).
- [ ] 🧩 **`ColorSpace` setter.** NDI sources can change colour metadata
  mid-stream; the renderer should re-upload the offset/matrix uniforms.
- [ ] 🧩 **Transfer-function uniform** (`int transfer`: sRGB, BT.709, PQ,
  HLG) and inverse-OETF after matrix multiply — required to render HDR
  sources correctly. Without it, HEVC/AV1 HDR shows as dim or wrong.
- [ ] 🧩 **Use sampler objects (`glGenSamplers`)** rather than mutating
  per-texture filter/wrap state.
- [ ] 🧩 **Optional mipmaps** on the Y plane when displaying a 4K source in
  a small preview (reduces aliasing).
- [ ] 🧩 **Aspect-ratio / fit policy.** Right now `Render` always fills.
  Add `Stretch`/`Contain`/`Cover` so callers don't have to compute a
  viewport.

### 1.4 Performance

- [ ] ⚡ **Cache shader source** in a `static ConcurrentDictionary<string,
      string>` so `LoadShader` doesn't enumerate manifest resources on every
  renderer instance.
- [ ] ⚡ **Share linked GL programs** keyed by `(vert, frag)` across renderer
  instances (a multi-viewer UI creates many).
- [ ] ⚡ **Skip writing `UNPACK_ROW_LENGTH`** when value already equals the
  cached last value.
- [ ] ⚡ **Direct pointer upload path** — replace `Memory<byte>.Pin()` per
  plane with a `ReadOnlySpan<byte>`-based API for callers that already
  own pinned memory.

---

## 2. Core video types (`S.Media.Core/Video`)

- [ ] 🐞 **Same odd-dimension truncation** in `PixelFormatInfo.PlaneHeight`
  and `PixelFormatInfo.PlaneByteWidth`. Fix to round-up.
- [ ] 🧩 **`BytesPerSample(format, plane)` helper.** Multiple callers
  hard-code `/2` for 16-bit formats; centralise.
- [ ] 🧩 **`Argb32` / `Abgr32` formats** plus their `PlaneByteWidth` entries.
- [ ] 🧩 **`IsAlphaCarrying(format)` helper** — sinks/renderers need to
  know whether the source might supply a meaningful alpha channel.
- [ ] 📝 **Document component-order convention** more thoroughly on each
  enum value, particularly for endian-sensitive packed formats.

---

## 3. `MediaClock`

- [ ] 🐞 **Silent `catch { }` in `SafeInvoke` / `RaisePositionChanged`.**
  Misbehaving subscribers are intentionally tolerated, but swallowed
  silently — switch to `Debug.WriteLine` or an `ILogger` so debugging
  isn't blind.
- [ ] ⚠️ **Pause with attached master can lose elapsed audio.** When the
  master keeps advancing during `Pause()` (PortAudio is still draining
  its OS buffer), subsequent `Start()` re-anchors and ignores the
  played samples. Snapshot the master's elapsed at pause and adjust
  `_basePosition` on `Start` if the master moved, OR document that
  callers must pause the audio sink before pausing the clock (the
  remark mentions this; an explicit helper would be friendlier).
- [ ] ⚠️ **Rapid Start/Stop overlap.** `DetachDriver` cancels but the new
  `Start` doesn't wait for the old thread to exit, so two driver
  threads can briefly co-exist and double-fire ticks. Either join
  under the lock (block briefly) or guard with `_driverGeneration`.
- [ ] ⚠️ **`Reset()` raises `PositionChanged` from the caller thread**,
  whereas regular ticks raise from the driver thread. Inconsistency
  that may surprise UI subscribers. Pick one threading contract and
  document it.
- [ ] ⚡ **Cache `token.WaitHandle`** outside the `DriverLoop` body — the
  property allocates lazily on first access.
- [ ] 🧩 **Allow user-supplied `ILogger`** for the catch sites.

---

## 4. `AudioRouter`

The router is the strongest part of the codebase; remaining items are
refinements, not rescue work.

### 4.1 Correctness / robustness

- [ ] 🐞 **Silent `catch { }` in sink Flush / Submit / pump drain.** Same
  logging fix as `MediaClock`. At minimum, surface a `SinkErrored`
  event so the host can react (mute, remove, restart).
- [ ] ⚠️ **Auto-generated IDs can collide with caller-supplied IDs.**
  `src_3` typed manually before three auto-IDs roll the counter will
  throw on the next AddSource. Either prefix auto IDs with something
  callers wouldn't use (`__src_3__`), or expose `nextId()` only as a
  Guid.
- [ ] ⚠️ **No "sink fell behind" notification.** `Dropped` counter rises
  silently. Expose an event so a UI / monitor can react.
- [ ] ⚠️ **`SetClock` / `SlaveTo` require stopped router.** OK as a v1,
  but breaks hot-swapping outputs (e.g. switching default audio
  device). Consider supporting a live clock swap that pauses one
  chunk.
- [ ] 📝 **`SeekSource` side-effect on other sources.** Pausing the whole
  router during a single-source seek introduces a gap in every other
  source's output. Document; consider a no-pause seek for sources
  whose `Seek` is lock-free.

### 4.2 Performance / quality

- [ ] ⚡ **`SetRouteGain` rebuilds the whole `RouterState`** on every call.
  For a UI volume scrubber dragging at 60 Hz this allocates an
  `ImmutableArray<Route>` 60×/s per route. Keep gains in a flat
  `float[]` (or `ConcurrentDictionary`) for hot updates; reserve the
  immutable state replacement for structural changes.
- [ ] ⚡ **`ApplyRoute` ramp/steady inner loops** can be vectorised with
  `System.Numerics.Vector<float>` for the common case of identity
  channel maps (e.g. stereo→stereo). At 48 kHz this is sub-1% CPU
  today, so optional.
- [ ] ⚡ **`_currentGains` is `ConcurrentDictionary<(string, string), …>`**
  and is hit twice per route per chunk. Allocates a tuple key on read.
  Move to a `Dictionary<int, float>` keyed by the route index in the
  immutable array — read-side becomes index lookup.
- [ ] 📐 **Per-sink rate adaptation / resampling.** Multi-output drift is
  documented; flag as a roadmap item with the implementation strategy
  (small SOXR/swresample instance per non-primary sink, fed from the
  router's chunk).
- [ ] 📐 **Sample-rate conversion at source boundary.** Currently sources
  that disagree with the router throw. Add a `ResamplingSourceWrapper`
  that wraps `IAudioSource` with `SwrContext`/SOXR so callers can mix
  sources of arbitrary sample rates.

### 4.3 API

- [ ] 🧩 **Per-route metering output** — peak / RMS on each route would
  let UIs draw VU meters without a separate analysis sink.
- [ ] 🧩 **Sink groups / buses** — even without a true mixer bus, a
  named-group filter on `AddRoute` would simplify multi-output
  common cases ("route this source to all `monitor` sinks").

---

## 5. `AudioPlayer` (high-level facade)

- [ ] ⚠️ **`AddOwnedSource` mutates `_ownedDisposables` without `_gate`.**
  Today single-threaded by convention, but the rest of the player is
  lock-protected — make it consistent.
- [ ] 🧩 **No `AddOutput` for clock-only sinks.** A sink that is
  `IClockedSink` but not `IPlaybackClock` (e.g. a record-to-file sink
  that paces but doesn't provide playback time) can't currently be
  auto-promoted. Tweak the auto-wire condition.
- [ ] 🧩 **`Seek` requires source ID.** For a typical single-source player
  the caller already knows there's only one; add an overload that
  seeks the first/only source.
- [ ] 🧩 **`LoadFile` mentioned in XML docs but does not exist** in the
  type. Either add it (creates an `AudioFileDecoder`, wires to the
  first output) or fix the docs.

---

## 6. FFmpeg decoders

### 6.1 `VideoFileDecoder`

- [ ] 🐞 **`av_seek_frame` doesn't ensure a key-frame-aligned restart.**
  When the source has long GOPs the next decoded frame can be far
  after the requested timestamp. After seeking, decode and discard
  frames until PTS ≥ requested.
- [ ] 🐞 **Pass-through `BuildPassThroughFrame` plane sizing uses
  `stride * height`** but height comes from the original `Format`, not
  `PlaneHeight`. For NV12 / I420 the chroma plane uses the full Y
  height in the calculation → over-counts memory. Use
  `PixelFormatInfo.PlaneHeight(format, frame->height, i)`.
- [ ] 🐞 **`AVFrame.linesize[]` can be negative** (FFmpeg uses negative
  strides to signal flipped frames). The current uploader would index
  into negative addresses. Either reject negative strides at decode
  time and convert via sws, or pass the absolute value with a
  `bottom-up = true` flag to the renderer.
- [ ] ⚠️ **`SwsContext` is reused across format/dim changes**
  via `sws_getCachedContext`, which is correct, but the input pixel
  format change (e.g. a clip that switches profile mid-stream) is not
  detected. Listen to `_codecCtx->pix_fmt` after each `receive_frame`
  and rebuild on change.
- [ ] ⚠️ **No HW acceleration path.** All decoding is software. Long-term
  goal: support VAAPI/DXVA2/D3D11VA/VideoToolbox with the resulting
  `AVFrame` carrying an `AV_PIX_FMT_*_*` hw format; integrate
  with the renderer via interop where possible.
- [ ] 🧩 **`SelectOutputFormat` lets the caller request multi-plane targets
  but `BuildConvertedFrame` only supports packed formats.** Either
  implement multi-plane sws output (allocate per-plane byte arrays)
  or reject multi-plane targets at `SelectOutputFormat` time.
- [ ] 🧩 **Map `AV_PIX_FMT_P010LE` / `AV_PIX_FMT_YUV420P10LE` /
  `AV_PIX_FMT_YUV444P*`** in `MapNativePixelFormat` once those
  `PixelFormat` enum entries exist.
- [ ] 🧩 **Expose colour-space / range / transfer-function metadata** from
  `AVCodecContext` (`colorspace`, `color_range`, `color_trc`) so the
  renderer can pick the matrix automatically rather than guessing
  from height.

### 6.2 `AudioFileDecoder`

- [ ] 🐞 **`Seek` re-inits swr** but keeps `_drainPacketSent = false` only,
  not `_drainedTail`. Replay-after-seek currently works only because
  `IsExhausted` happens to consult `_eofReached`; document or simplify
  the state machine.
- [ ] ⚠️ **`Position` is updated only when samples flow out**, so during a
  long underrun your reported position freezes. Acceptable for an
  audio source, but worth a note.
- [ ] 🧩 **Channel-layout downmix on the source side**. Currently the
  decoder produces whatever channel count the file has; the
  `ChannelMap` on the route does any remapping. For real surround
  content a proper downmix matrix (Dolby coefficients) is more
  correct than zero-pad/zero-drop. Worth a `DownmixingSourceWrapper`.

---

## 7. NDI sender (`NDIVideoSender`)

- [ ] 🐞 **Even-dimension check on `Configure` is good, but odd-width
  packed BGRA frames slip through** even though the staging copy
  assumes `visibleStride = width * 4`. Add the even-width check for
  packed formats too (or simply always require even dimensions for
  4:2:0 / 4:2:2 formats and pad otherwise).
- [ ] 🐞 **`PackI420` writes `uDstBase + halfW * halfH`** for the V plane,
  which is correct only when V follows U *contiguously without
  stride*. NDI requires the V plane right after the U plane at half
  stride — your math matches today but a different `LineStrideInBytes`
  policy in the future will silently corrupt. Compute V offset as
  `uDstBase + uPlaneByteCount` based on the same `LineStrideForFormat`
  helper.
- [ ] ⚠️ **Submission isn't paced.** `Submit` blasts every frame to NDI's
  async send without honouring frame rate; downstream NDI receivers
  will receive frames as fast as the producer dispatches them. Either
  gate `Submit` by a wall-clock timer matched to `Format.FrameRate`,
  or accept a `presentationTime` and use `Timecode` plus a tiny
  pacing loop.
- [ ] 🧩 **Add UYVY / NV12 / I420 alpha variants (`BGRA + A` via dedicated
  FourCC) and 16-bit P216 / P416** if you want to align with NDI's
  higher-quality formats.
- [ ] ⚡ **Memory copy on every frame is unavoidable for staging, but
  `PackPacked` row-loop can be one `CopyTo` when stride equals visible
  stride.** Branch on `srcStride == visibleStride`.

---

## 8. PortAudio output (`PortAudioOutput`)

- [ ] 🐞 **`Stop()` doesn't free `_selfHandle` if `Pa_StopStream` throws.**
  A failed stop leaves the GCHandle pinned forever. Wrap in try/finally.
- [ ] 🐞 **Ring-buffer accounting can deadlock on a stuck audio thread.**
  If the PA callback hangs (driver bug), `WaitForCapacity` will spin
  forever as `QueuedSamples` stays > target. Bound the wait
  (return `false` after N seconds of no consumption) and surface as
  an event.
- [ ] ⚠️ **`Flush()` discards anything currently in the OS buffer with
  `Pa_AbortStream`**, then restarts. There's a brief moment where the
  stream isn't active and a concurrent `Submit` will write into the
  ring without observers — those samples will play on resume. Either
  Submit-side check `_isRunning` (it doesn't today) or document that
  Flush must be paired with producer-side quiescence.
- [ ] ⚠️ **`IsRunning` and `_isRunning` are read without `Volatile`** in
  `Submit` and `WaitForCapacity`. Update to `Volatile.Read`.
- [ ] ⚠️ **`Pa_GetStreamTime` returns 0 after Stop**, but
  `ElapsedSinceStart` reports based on `PlayedSamples` (preserved).
  Two different clocks expose two different post-stop behaviours.
  Pick one canonical "elapsed" and document.
- [ ] 🧩 **`Native.Pa_GetDeviceInfo` is called once at construction.** If
  the user replugs a USB device PA's index might be stale on next
  `Start`. Re-resolve on `Start`.
- [ ] 🧩 **No support for `IClockedSink.WaitForCapacity` overshoot
  reporting.** When the router consistently overshoots target, the
  buffer fills and `DroppedSamples` grows — expose a warning event.

---

## 9. SDL3 video sinks

(Not deeply reviewed — file the following as a section to walk through.)

- [ ] 📝 Verify `SDL3GLVideoSink` correctly makes the GL context current
  before delegating to `YuvVideoRenderer`, and releases it on the
  same thread.
- [ ] 📝 Verify event-loop integration — SDL3 requires `SDL_PumpEvents` on
  the thread that owns the window; doc current contract.

---

## 10. Cross-cutting concerns

### 10.1 Logging & diagnostics

- [ ] 🧩 **Project-wide `Diagnostics` static class** routing to a single
  `ILogger` (or `Debug.WriteLine` fallback). Replace every `catch
      { /* ignore */ }` with a logged catch.
- [ ] 🧩 **Per-component event surface**: `SinkErrored`, `ClockOverrun`,
  `PumpOverflow`, etc. Lets a host UI distinguish a stalled
  output from a graceful EOF.

### 10.2 Cancellation

- [ ] 🧩 **Accept `CancellationToken` on blocking public APIs**:
  `WaitForIdle`, `Stop`, `Pause`, `Flush`. Currently they all
  internally sleep with no opt-out.

### 10.3 Testing

- [ ] ✅ Add stress tests for the router that mutate routes/sources/sinks
  while running on a separate thread (verify no exceptions, no
  buffer leaks, no double-drops).
- [ ] ✅ Property test the ring buffer of `PortAudioOutput` with random
  submit/drain sizes (wraparound coverage).
- [ ] ✅ Pixel-format round-trip tests for the renderer: synthesise a
  frame, upload, read back via `glReadPixels`, compare against a
  software reference.
- [ ] ✅ A scripted "drift" test for `WallClockRouterClock` vs.
  `SinkSlavedRouterClock` to confirm the slave path stays within
  ±0.5 sample of the master over 10 minutes.

### 10.4 Documentation

- [ ] 📝 A "concepts" doc explaining the source → router → sink graph,
  clocks, channel maps, and the difference between Pause / Stop /
  Flush / Dispose — the inline XML is excellent but spread thin.
- [ ] 📝 Pixel-format quick-reference table: enum name, FFmpeg name, NDI
  FourCC, GL recipe, bit depth, plane layout.

---

## Suggested ordering for first pass

1. Fix the odd-dimension truncation bugs (renderer + `PixelFormatInfo`).
2. Drop the redundant `glClear` and snapshot/restore GL pixel-store state.
3. Refactor the renderer to a single `GlFormatRecipe` table.
4. Expand pixel-format coverage: `Yv12`, `Nv21`, `Rgba32`, `Rgb24`/`Bgr24`,
   `Uyvy`/`Yuyv`, `Yuv422P`, `Yuv444P`, `P010`.
5. Add the transfer-function uniform (HDR display).
6. Replace silent `catch { }` with logged catches across `MediaClock`,
   `AudioRouter`, `SinkPump`, `AudioPlayer`.
7. Wire `IsFormatSupported` on the renderer through `VideoFormatNegotiator`.
8. Fix the FFmpeg decoder seek/keyframe-skip and negative-stride handling.
9. Pace `NDIVideoSender` by frame rate.
10. Address remaining performance items (`SetRouteGain` hot path, shader
    caching) once correctness is solid.