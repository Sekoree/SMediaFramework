# Media Framework Fix Checklist — 2026-06

Status (updated 2026-06-01): **Batches 1–7 done (32 items + 2 partial).** Batch 1: P0-2, P0-3, P1-4, P1-5, P2-10, P2-16, P2-21, P2-22. Batch 2: P0-5c, P1-2, P1-6, P1-7, P1-8. Batch 3: P0-5a, P0-5b. Batch 4: P0-4, P1-3, P0-6~. Batch 5: P2-8, P2-12, P2-13, P2-17. Batch 6: P1-10, P1-12, P2-25~. Batch 7: P2-3, P2-4, P2-11, P2-14, P2-23, P2-24, P2-26, P2-27. +10 regression tests. Build clean; all suites green (Core 397 / FFmpeg 162 / Encode 2 / NDI 50 / Playback 25 / PortAudio 22 / OpenGL 105 / Skia 10). **Deferred with rationale:** P0-1 (native `AVIOInterruptCB` — can't verify/test the interop here), P1-1 (coordinated-seek-default needs a session-level single-seek + resume-after-seek refactor with UX implications). Derived from `Doc/MediaFramework-Deep-Review-Findings.md` (verification pass 2026-06-01). Items use `[ ]` open, `[~]` partial, `[x]` closed. Each closed/partial item gets a one-line **Closed via …** / **Partial — …** / **Not applied — …** note. Withdrawn review findings (NDI `Video` getter, NDI realloc) are intentionally absent.

Convention: code change + checklist note land in the same pass. Verification report (build warnings/errors, test pass/fail, new tests) follows each batch.

---

## Phase P0 — Critical correctness & stability

- [ ] **P0-1 FFmpeg demux stop race + interrupt callback.** **Deferred (needs a focused native pass).** Requires installing an `AVIOInterruptCB` on `_fmt` driven by `_demuxerStopRequest` (so `av_read_frame` aborts promptly and the 4 s join can't time out into a free/seek-while-blocked race), plus not clearing `_demuxerThread`/`_demuxerStopRequest` until the thread exits. Not done this round: hand-writing the native function-pointer interop against FFmpeg.AutoGen 8's binding without source to verify the callback type, and without a slow/hanging-I/O harness to test it, is too risky to ship blind. Partial mitigation already in place: P0-5b's demux fault boundary means a faulting/aborted read no longer crashes the host.
- [x] **P0-2 VideoPlayer post-submit double-dispose (3 sites).** **Closed via** capturing the PTS before `Submit`, gating counters/notification on a `submitted` flag, and routing the event through a new `RaisePresented` helper that wraps the invocation in its own try/catch (never disposes the frame, never lets a subscriber exception reach the clock-driver thread) — applied in `OnVideoTick`, `PresentLatestQueuedFrame`, `TrySubmitHeldFrame`. Note: `VideoFrame.Dispose` is idempotent (`Interlocked.Exchange`), so the real symptom was a *premature* release of a frame an async output still held queued, not a literal double-free. Regression test `VideoPlayerTests.Submit_Success_With_Throwing_PresentationEvent_Does_Not_Release_Frame`.
- [x] **P0-3 VideoRouter.RemoveOutput leaks owned outputs.** **Closed via** capturing the registration under `_gate`, then disposing it (when `DisposeOutputOnRouterDispose`) *outside* the lock — `VideoOutputPump.Dispose` joins its drainer thread, so holding `_gate` during teardown is unsafe.
- [x] **P0-4 AudioRouter drains unrouted/unstarted sources.** **Closed via** gating the run-loop read on routing: only sources targeted by a route this chunk are read (reused per-chunk `HashSet` so a source feeding many routes is read once; muted-but-routed sources still advance — mixer mute semantics). `keepRunning` only auto-stops once routes exist AND every routed source is exhausted, so dynamic "route-last" graphs aren't killed and an empty/route-less router idles instead of stopping. MediaPlayer now routes its audio source to a `DiscardingAudioOutput` so "play to completion" / master-clock advance still works with no real output attached (a later clocked PortAudio output still becomes the pacing primary). Regression test `AudioRouterSourceGatingTests.RunLoop_UnroutedSource_IsNotConsumedUntilRouted`.
- [x] **P0-5a AudioRouter background rethrow → fault.** **Closed via** a `Faulted` event + `Fault` property (new `AudioRouterFaultedEventArgs`); `RunLoop`'s catch now records the fault and raises the event instead of rethrowing (the `finally` still tears down thread/cts); `Start` clears the prior fault. Regression test `AudioRouterFaultTests.RunLoop_SourceThrows_RouterFaultsInsteadOfCrashing`.
- [x] **P0-5b FFmpeg demux thread exception boundary.** **Closed via** wrapping `DemuxerThreadProc` in a catch that records `_demuxFault` (exposed as `DemuxFault`), signals EOF (`_fileReadCompleted = true`) and pulses `_queueGate` so blocked audio/video tracks wake and exhaust gracefully; `StartDemuxerThread` clears the fault.
- [x] **P0-5c NDI receiver fault boundaries.** **Closed via** a `_faultEx` field + `Fault` property + `Faulted` event on both receivers; `IsExhausted` now terminal on fault; `CaptureLoop` wrapped in try/catch (video uses a `CaptureLoopCore` split to avoid re-indenting) that records the fault, wakes the blocked video reader, and stops the router pulling audio.
- [~] **P0-6 Static/text/image frame lifetime.** **Partial — `TextLayerSource` fixed** via a refcounted generation buffer (`FrameBuffer : IDisposable`): each rasterise produces an immutable pooled buffer; emitted frames share it and hold a ref (no per-frame copy while static); a property change rasterises a new generation, and an old one returns to the pool only when the source and all referencing frames are disposed — so mutation/disposal can't change pixels or free the buffer under in-flight frames. Regression tests `TextLayerSourceTests.HeldFrame_NotMutated_ByLaterTextChange` / `HeldFrame_RemainsReadable_AfterSourceDispose`. **Not done:** `StaticFrameSource`/`ImageFileSource` — lower severity (immutable buffers, so no mid-flight content change; the only hazard is disposing the source while frames are queued, and managed-array backings are GC-safe). The `FromFrame(copyBacking:true)` disposal hazard is tracked under P2-14.

## Phase P1 — Transport, sync, decode/encode

- [ ] **P1-1 Coordinated seek as default.** **Deferred (UX/architecture decision).** The coordinator's `SeekCoordinated` *pauses and stays paused*, so making it the default `MediaPlayer.Seek` would turn seek-during-playback into stop-at-position (a UX change). The correct fix — for shared demux, `SeekPresentation` once then reset both tracks/clocks, and pause→seek→resume-if-was-playing — lives in the session layer and should be designed deliberately, not a one-line default swap. The race half is already addressed by P1-3.
- [x] **P1-2 Pause order.** **Closed via** pausing audio (router + clock, or the video clock when audio-less) first, then doing the possibly-blocking `video.Pause` in a `finally`, with the shared-mux flush still last.
- [x] **P1-3 Shared-demux seek audio lock.** **Closed via** wrapping the audio decode-state flush/reset in `SeekPresentation` (`_aCtx` flush, `swr_close`/`swr_init`, counters) in `lock (_audioDecodeLock)` — the same lock `AudioTrack.ReadInto` holds. The video consume below takes `_videoDecodeLock` sequentially, so no nested-lock-ordering hazard.
- [x] **P1-4 Reset fallback PTS counters on seek.** **Closed via** re-anchoring `_vFramesEmitted` (shared demux `SeekPresentation`) and `_framesEmitted` (`VideoFileDecoder.Seek`) to `round(position * fps)` so no-PTS streams resume at ~seek-target instead of a stale future index.
- [x] **P1-5 AudioClipVoice EOF release hang.** **Closed via** finishing the release ramp immediately (`_gain = 0; _stopped = true`) when the source is exhausted mid-release, so `IsExhausted` becomes true instead of lingering. Regression test `AudioClipTests.Voice_StopAtEndOfClip_BecomesExhausted`.
- [x] **P1-6 FFmpeg video encoder allocate-once.** **Closed via** allocating `_frame`'s plane buffers once in `OpenCodecLocked` (format/dims set there), checking the `av_frame_make_writable` return in `Submit`, and removing the per-frame `av_frame_get_buffer` from `FfmpegAvFrameFill.CopyVideoFrame` (now a pure copy with a dimension guard).
- [x] **P1-7 FFmpeg audio encoder non-FLT/FLTP.** **Closed via** a per-encoder `SwrContext` (FLT-in → codec-sample-fmt-out) used in `WriteFrameLocked`, replacing the FLTP-only / raw-float-copy branches. Now correct for FLAC (s16/s32) and any integer/planar codec. Regression test `FFmpegMuxRoundTripTests.Audio_flac_round_trip_preserves_signal` (lossless RMS check).
- [x] **P1-8 Router clock catch-up cap.** **Closed via** re-anchoring the deadline to "now" once the backlog exceeds `MaxCatchupChunks` (64, matching `MediaClock`) in both `WallClockRouterClock` and `PlaybackSlavedRouterClock`.
- [ ] **P1-9 Frame-mode audio tail drain.** **Deferred — no current consumers.** `IAudioSource.TryReadNextFrame(out AudioFrame)` is a default interface method returning false; nothing in the framework or tests consumes audio in frame mode (the router uses `ReadInto`, which *does* drain the swr tail correctly). Fixing dead code now is low value; revisit if/when a frame-mode audio consumer (e.g. a future encode pull path) lands.
- [x] **P1-10 ResamplingAudioSource seekability.** **Closed via** a `ResamplingAudioSource.Create` factory that returns a `SeekableResamplingAudioSource : ResamplingAudioSource, ISeekableSource` when the inner is seekable (forwards Seek/Position/Duration and flushes the resampler on seek via `ResetAfterInnerSeek`); the FFmpeg auto-resample factory now uses `Create`. Regression tests in `ResamplingAudioSourceTests` (incl. `Router_AutoResample_PreservesSeekThroughWrapper`).
- [ ] **P1-11 NDI ingest timing.** Use NDI timecode/timestamp when present; fixed cadence only as fallback (`NDIVideoReceiver.cs:176-181`, `218-220`).
- [x] **P1-12 VideoPlayer restart guard.** **Closed via** a `_decodeThreadStuck` flag set when `StopInternal`'s bounded join leaves the decode thread `IsAlive` (and it wasn't an early cooperative cancel); `Play()` then throws instead of starting a second decode thread over the same source/queue.

## Phase P2 — Allocation, contracts, API hardening

- [ ] **P2-1 Lazy-start / pool output-pump threads.** Don't start an `OutputPump` thread until the router starts; or share a bounded pool (`AudioRouter.cs:1502-1521`). Addresses the intermittent suite OOM.
- [ ] **P2-2 VideoRouter conversion outside lock.** Snapshot routes/outputs under `_gate`, then convert/readback/submit outside it (`VideoRouter.cs:547-687`, `746-755`).
- [x] **P2-3 VideoOutputPump reconfigure drain.** **Closed via** dropping queued frames in `Configure` when the new format differs from the inner's current format (the unchanged-format branch-route re-Configure stays a cheap pass-through). Residual: a frame already in-flight on the drainer can still race — noted; full format-versioned queues left as a follow-up.
- [x] **P2-4 VideoOutputPump dispose vs blocked Submit.** **Closed via** checking `Thread.IsAlive` after the join; if the drainer is still blocked in a slow inner `Submit`, `Dispose` now leaks `_pending`/`_cts`/the inner deliberately (logs) instead of disposing them under the running thread (which would throw `ObjectDisposedException` on the drainer or use-after-dispose the inner).
- [ ] **P2-5 Compositor hot-path allocations.** Reuse arrays/leases in `VideoCompositorSource.TryReadNextFrame`; stop `Slots` from allocating per access (`VideoCompositorSource.cs:80`, `162-187`).
- [ ] **P2-6 FFmpeg planar pin/alloc per frame.** `stackalloc`/`fixed`/pooled pinned buffers in the swscale paths (`VideoFileDecoder.cs:745-829`; shared demux `~1280-1363`). 🔎 re-verify first.
- [ ] **P2-7 FFmpeg pass-through per-plane memory-manager alloc.** Pool the `UnmanagedMemoryManager<byte>` wrappers if profiling warrants (`MediaContainerSharedDemux.cs:~1188-1200`). 🔎 re-verify first.
- [x] **P2-8 FFmpeg audio encoder O(n) pending shift.** **Closed via** replacing the `List<float>` + `RemoveRange(0,…)` with a `float[]` + read/write offsets (`AppendPending` compacts/grows only when the tail is full); draining an encoded frame is now O(1). Validated by the P1-7 FLAC round-trip (chunked Submit).
- [ ] **P2-9 CPU compositor straight-alpha.** Encode alpha mode in metadata/pixel format or normalize at source boundaries (`CpuVideoCompositor.cs:16-22`, `172-204`).
- [x] **P2-10 GL compositor pack pixel-store restore.** **Closed via** saving `GL_PACK_ALIGNMENT`/`GL_PACK_ROW_LENGTH` alongside the existing unpack save and restoring both in the `finally`.
- [x] **P2-11 LayerConfig.ScaleAnchor.** **Closed via removal** — the field was read nowhere (only declared); removed it rather than leave an API control that does nothing. Anchor-aware composition can be reintroduced when actually wired (noted in the `LayerConfig` remarks).
- [x] **P2-12 LayerHandle thread safety.** **Closed via** a `_gate` lock guarding `_config`/`_transitions` in `SetConfig`/`AddTransition`/`ClearTransitions`/`CurrentConfig`, and snapshotting both under the lock at the top of `TryPullFrame` so the compositor path iterates immutable copies.
- [x] **P2-13 LayerHandle configure-throw leak.** **Closed via** wrapping `Slot.Output.Configure`/`Submit` in `TryPullFrame` in a try/catch that disposes the pending frame on throw (ownership hasn't moved on a Configure/Submit failure).
- [x] **P2-14 StaticFrameSource.FromFrame(copyBacking:false).** **Closed via removal** — the non-copying mode (aliased caller planes, no ownership) had no users; `FromFrame` now always duplicates CPU backing. Updated the 6 call sites (all passed `copyBacking: true`).
- [ ] **P2-15 NDI live source format probe.** Async `Connect`/`Probe` or `TryGetFormat`/format-changed pattern (`NDIVideoReceiver.cs:210-222`, `NDIAudioReceiver`).
- [x] **P2-16 PortAudio wiring leak.** **Closed via** hoisting `output`/`sinkMain` out of the `try` and, on failure, rolling back router registration (`RemoveOutput`) and disposing the native output. Verified the audio `OutputPump.Dispose` does not dispose the inner output (the host owns it on success), so no double-dispose.
- [x] **P2-17 PortAudio prefill allocation.** **Closed via** renting the prefill scratch from `ArrayPool<float>.Shared` (returned in a `finally`) instead of `new float[bufFloats]` per call.
- [ ] **P2-18 PortAudio companion ownership.** Session/player owns the companion host by default (`MediaPlayerOpenBuilderPortAudioExtensions.cs`).
- [ ] **P2-19 SDL output dispose vs render thread.** Don't dispose wait handles/inner resources until the render thread exits (`SDL3VideoOutput.cs`, `SDL3GLVideoOutput.cs`). 🔎 re-verify first.
- [ ] **P2-20 SDL reconfigure contract parity.** Align `SDL3VideoOutput.Configure` with `SDL3GLVideoOutput.Configure` (`SDL3VideoOutput.cs:122-128`, `SDL3GLVideoOutput.cs:259-320`).
- [x] **P2-21 AudioRouter validate read counts.** **Closed via** an `(uint)read > (uint)scratch.Length` guard in `RunLoop` that logs and clears the chunk (catches negative and over-count) instead of letting `AsSpan(read)` throw on the router thread. Full per-source fault state deferred to the fault-handling work (P0-5a).
- [x] **P2-22 AudioRouter.WaitForIdle vs abandoned chunks.** **Closed via** incrementing `_processed` for each chunk `AbandonQueue` removes, so `WaitForIdle` stops blocking once the queue is genuinely drained.
- [x] **P2-23 AdaptiveRate fake primary clock.** **Closed via** excluding `IAdaptiveRateWrappedOutput` from `AutoWirePrimaryOutputIfNeeded` — an adaptive wrapper (which reports `IClockedOutput` unconditionally but returns 'always ready' when its inner isn't clocked) can never be promoted to the router's pacing primary.
- [x] **P2-24 AdaptiveRate wrapper disposal.** **Closed via** disposing `entry.Output` in `RemoveOutput` and router `Dispose` when it's an `IAdaptiveRateWrappedOutput` (router-created) — frees its monitor subscription/resampler. Verified the wrapper's `Dispose` doesn't touch the caller's inner output.
- [~] **P2-25 VideoFormat/VideoFrame validation.** **Partial — `VideoFormat.Validate` added** (positive dimensions + frame rate, mirroring `AudioFormat.Validate`) and called at the `VideoPlayer` boundary after negotiation. **Not done:** per-frame CPU plane-count/stride/length validation in `VideoFrame` — that's the per-frame hot path and risks rejecting frames that currently work, so it needs profiling + a trusted-fast-path design before enabling.
- [x] **P2-26 RouteGainSlot encapsulation.** **Closed via** changing `Target`/`Current` from public fields to `{ get; internal set; }` — external callers keep read access but can no longer bypass `SetRouteGain`'s ramp invariant. (Full immutable-route-snapshot DTO left as a larger API change.)
- [x] **P2-27 URI/path API strictness.** **Closed via** making `MediaPlayerOpen.Uri(string)` require an absolute URI (clear error pointing to `File(path)` for filesystem paths) instead of `UriKind.RelativeOrAbsolute`. `File`/`Uri(Uri)`/`Stream` already existed.

## Phase P3 — Architecture (design-level; scope per item)

- [ ] **P3-1 Owned MediaSession/PlaybackSession.** Single object owning player, outputs, companion hosts, plugin resources; `Borrow`/`DoNotDispose` escape hatches; `IAsyncDisposable`.
- [ ] **P3-2 First-class clips/voices/cues.** `Soundboard`/`CueEngine` over the router; `Fire(...)` returns a voice handle with Stop/FadeTo/Seek/SetGain + completion/fault events.
- [ ] **P3-3 Per-session options vs process-wide statics.** Move `AudioRouter.DefaultAutoResample`, `MediaFrameworkPlugins`, `VideoCompositor.RegisterAutoBackend` behind per-session options/registry (keep statics as escape hatch).
- [ ] **P3-4 Composition time-driven by default.** Wire `SlotKeepPolicy.MasterAligned` + a real master time into the declarative `VideoCompositor`; decode layers into bounded PTS queues (`VideoCompositor.cs:100-106`, `VideoCompositorSource.cs:332-399`).

---

## Verification log

### Batch 1 — 2026-06-01 (P0-2, P0-3, P1-4, P1-5, P2-10, P2-16, P2-21, P2-22)
- **Build:** `dotnet build MFPlayer.sln` — succeeded, 0 errors. (Pre-existing `CS0618` obsolete-API warnings in `S.Media.Core` are unchanged, not introduced here.)
- **Tests:** `S.Media.Core.Tests` 395 passed / 0 failed (was 393; +2 new). `S.Media.FFmpeg.Tests` 159 passed / 0 failed.
- **New tests:** `AudioClipTests.Voice_StopAtEndOfClip_BecomesExhausted` (P1-5), `VideoPlayerTests.Submit_Success_With_Throwing_PresentationEvent_Does_Not_Release_Frame` (P0-2).
- The pre-existing flaky `NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize` is unrelated to this batch (NDI suite not affected here).

### Batch 2 — 2026-06-01 (P0-5c, P1-2, P1-6, P1-7, P1-8)
- **Build:** `dotnet build MFPlayer.sln` — succeeded, 0 errors.
- **Tests:** Core 395 / FFmpeg 159 / NDI 50 / FFmpeg.Encode 2 — all passed, 0 failed.
- **New tests:** `FFmpegMuxRoundTripTests.Audio_flac_round_trip_preserves_signal` (P1-7) — the audio encoder now has its first coverage (was 1 video-only test).
- Touched FFmpeg native paths (video frame alloc, audio swresample); the FLAC round-trip is the deliberate proof the swresample path is correct, not just compiling.

### Batch 3 — 2026-06-01 (P0-5a, P0-5b)
- **Build:** `dotnet build MFPlayer.sln` — succeeded, 0 errors.
- **Tests:** Core 396 / FFmpeg 159 — all passed, 0 failed.
- **New tests:** `AudioRouterFaultTests.RunLoop_SourceThrows_RouterFaultsInsteadOfCrashing` (P0-5a).
- No existing test depended on the old rethrow behaviour. Fault state is opt-in to observe (`Fault`/`Faulted`, `DemuxFault`); default behaviour is "stop quietly instead of crash", which is strictly safer for existing callers.

### Batch 4 — 2026-06-01 (P0-4, P1-3, P0-6 partial)
- **Build:** `dotnet build MFPlayer.sln` — succeeded, 0 errors.
- **Tests:** all suites green — Core 397 / FFmpeg 159 / Encode 2 / NDI 50 / Playback 25 / PortAudio 22 / OpenGL 105 / Skia 10.
- **New tests:** `AudioRouterSourceGatingTests.RunLoop_UnroutedSource_IsNotConsumedUntilRouted` (P0-4); `TextLayerSourceTests.HeldFrame_NotMutated_ByLaterTextChange` + `HeldFrame_RemainsReadable_AfterSourceDispose` (P0-6).
- **Caught + fixed a regression in this batch:** P0-4 broke `MediaPlayerSmokeTests.OpenAudioOnlyFile_play_until_source_exhausted` (a headless audio file no longer drained without a route). Fixed by routing MediaPlayer's audio source to a `DiscardingAudioOutput` (mirrors the existing `DiscardingVideoOutput` primary), so play-to-completion / master-clock advance still works; verified a later clocked PortAudio output still auto-promotes to primary (`AutoWirePrimaryOutputIfNeeded` only promotes the first `IClockedOutput`).
- **Deferred:** P0-1 (native interrupt callback — see item) and P1-1 (coordinated-seek-default — see item).

### Batch 5 — 2026-06-01 (P2-8, P2-12, P2-13, P2-17)
- **Build:** `dotnet build MFPlayer.sln` — succeeded, 0 errors.
- **Tests:** Core 397 / FFmpeg.Encode 2 / PortAudio 22 — all green (no new tests; P2-8 covered by the existing FLAC round-trip, the rest are mechanical hardening).
- Small, contained quality fixes: O(1) encoder pending buffer, LayerHandle config/transition locking + configure-throw frame disposal, pooled PortAudio prefill scratch.

### Batch 6 — 2026-06-01 (P1-10, P1-12, P2-25 partial; P1-9 deferred)
- **Build:** `dotnet build MFPlayer.sln` — succeeded, 0 errors.
- **Tests:** all green — Core 397 / FFmpeg 162 (+3 resampling) / Playback 25 / OpenGL 105 / Skia 10.
- **New tests:** `ResamplingAudioSourceTests` ×3 (incl. router autoResample seek passthrough — the exact finding).
- **P1-9 deferred:** frame-mode audio (`TryReadNextFrame(out AudioFrame)`) has no consumers anywhere; the live path (`ReadInto`) already drains the tail. Not worth fixing dead code now.
- **P2-25 scoped down:** `VideoFormat.Validate` (dimensions + frame rate) added and wired at the player boundary; per-frame `VideoFrame` plane validation left for a profiled, trusted-fast-path pass.

### Batch 7 — 2026-06-01 (P2-3, P2-4, P2-11, P2-14, P2-23, P2-24, P2-26, P2-27)
- **Build:** `dotnet build MFPlayer.sln` — succeeded, 0 errors.
- **Tests:** Core 397 / FFmpeg 162 / Playback 25 — all green (mechanical hardening + small breaking API cleanups; no new tests).
- **Breaking API changes (approved):** removed `LayerConfig.ScaleAnchor` and `StaticFrameSource.FromFrame`'s `copyBacking` param (both dead/footgun); `RouteGainSlot.Target/Current` now read-only externally; `MediaPlayerOpen.Uri(string)` rejects relative URIs.
