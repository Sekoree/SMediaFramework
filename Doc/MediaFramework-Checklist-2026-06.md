# Media Framework Fix Checklist — 2026-06

Status (updated 2026-06-01): **Batches 1–3 done (15 items).** Batch 1: P0-2, P0-3, P1-4, P1-5, P2-10, P2-16, P2-21, P2-22. Batch 2: P0-5c, P1-2, P1-6, P1-7, P1-8. Batch 3: P0-5a, P0-5b. +4 regression tests. Build clean; Core 396 / FFmpeg 159 / NDI 50 / Encode 2 green. Remaining P0s (P0-1 demux interrupt, P0-4 source-drain semantics, P0-6 frame lifetime) are larger/behavior-changing — left open for a directed pass. Derived from `Doc/MediaFramework-Deep-Review-Findings.md` (verification pass 2026-06-01). Items use `[ ]` open, `[~]` partial, `[x]` closed. Each closed/partial item gets a one-line **Closed via …** / **Partial — …** / **Not applied — …** note. Withdrawn review findings (NDI `Video` getter, NDI realloc) are intentionally absent.

Convention: code change + checklist note land in the same pass. Verification report (build warnings/errors, test pass/fail, new tests) follows each batch.

---

## Phase P0 — Critical correctness & stability

- [ ] **P0-1 FFmpeg demux stop race + interrupt callback.** Install an `AVIOInterruptCB` on `_fmt` driven by `_demuxerStopRequest`; in `StopDemuxerAndDrainQueues` do not clear `_demuxerThread`/`_demuxerStopRequest` or free demux state until the thread has actually exited (`MediaContainerSharedDemux.cs:537-554`, `587-616`).
- [x] **P0-2 VideoPlayer post-submit double-dispose (3 sites).** **Closed via** capturing the PTS before `Submit`, gating counters/notification on a `submitted` flag, and routing the event through a new `RaisePresented` helper that wraps the invocation in its own try/catch (never disposes the frame, never lets a subscriber exception reach the clock-driver thread) — applied in `OnVideoTick`, `PresentLatestQueuedFrame`, `TrySubmitHeldFrame`. Note: `VideoFrame.Dispose` is idempotent (`Interlocked.Exchange`), so the real symptom was a *premature* release of a frame an async output still held queued, not a literal double-free. Regression test `VideoPlayerTests.Submit_Success_With_Throwing_PresentationEvent_Does_Not_Release_Frame`.
- [x] **P0-3 VideoRouter.RemoveOutput leaks owned outputs.** **Closed via** capturing the registration under `_gate`, then disposing it (when `DisposeOutputOnRouterDispose`) *outside* the lock — `VideoOutputPump.Dispose` joins its drainer thread, so holding `_gate` during teardown is unsafe.
- [ ] **P0-4 AudioRouter drains unrouted/unstarted sources.** Only read a source when at least one active (non-muted) route targets it (`AudioRouter.cs:1153-1159`). Behavior change — verify against existing tests.
- [x] **P0-5a AudioRouter background rethrow → fault.** **Closed via** a `Faulted` event + `Fault` property (new `AudioRouterFaultedEventArgs`); `RunLoop`'s catch now records the fault and raises the event instead of rethrowing (the `finally` still tears down thread/cts); `Start` clears the prior fault. Regression test `AudioRouterFaultTests.RunLoop_SourceThrows_RouterFaultsInsteadOfCrashing`.
- [x] **P0-5b FFmpeg demux thread exception boundary.** **Closed via** wrapping `DemuxerThreadProc` in a catch that records `_demuxFault` (exposed as `DemuxFault`), signals EOF (`_fileReadCompleted = true`) and pulses `_queueGate` so blocked audio/video tracks wake and exhaust gracefully; `StartDemuxerThread` clears the fault.
- [x] **P0-5c NDI receiver fault boundaries.** **Closed via** a `_faultEx` field + `Fault` property + `Faulted` event on both receivers; `IsExhausted` now terminal on fault; `CaptureLoop` wrapped in try/catch (video uses a `CaptureLoopCore` split to avoid re-indenting) that records the fault, wakes the blocked video reader, and stops the router pulling audio.
- [ ] **P0-6 Static/text/image frame lifetime.** Make emitted frames safe against source disposal/mutation. Minimum: `TextLayerSource` must publish a new immutable buffer generation per rasterize instead of rewriting the live `_pixelBuffer`, and must not return a still-referenced buffer to the pool (`TextLayerSource.cs:176-221`, `223-229`); `StaticFrameSource`/`ImageFileSource` refcount or duplicate per emitted frame.

## Phase P1 — Transport, sync, decode/encode

- [ ] **P1-1 Coordinated seek as default.** Route `MediaPlayer.Seek` through the coordinated (pause-first) path; keep the uncoordinated one internal/opt-out (`AvPlaybackCoordinator.cs:92-127`, `MediaPlayer.cs:250-265`).
- [x] **P1-2 Pause order.** **Closed via** pausing audio (router + clock, or the video clock when audio-less) first, then doing the possibly-blocking `video.Pause` in a `finally`, with the shared-mux flush still last.
- [ ] **P1-3 Shared-demux seek audio lock.** Take `_audioDecodeLock` around the audio flush in `SeekPresentation` (`MediaContainerSharedDemux.cs:683-701`).
- [x] **P1-4 Reset fallback PTS counters on seek.** **Closed via** re-anchoring `_vFramesEmitted` (shared demux `SeekPresentation`) and `_framesEmitted` (`VideoFileDecoder.Seek`) to `round(position * fps)` so no-PTS streams resume at ~seek-target instead of a stale future index.
- [x] **P1-5 AudioClipVoice EOF release hang.** **Closed via** finishing the release ramp immediately (`_gain = 0; _stopped = true`) when the source is exhausted mid-release, so `IsExhausted` becomes true instead of lingering. Regression test `AudioClipTests.Voice_StopAtEndOfClip_BecomesExhausted`.
- [x] **P1-6 FFmpeg video encoder allocate-once.** **Closed via** allocating `_frame`'s plane buffers once in `OpenCodecLocked` (format/dims set there), checking the `av_frame_make_writable` return in `Submit`, and removing the per-frame `av_frame_get_buffer` from `FfmpegAvFrameFill.CopyVideoFrame` (now a pure copy with a dimension guard).
- [x] **P1-7 FFmpeg audio encoder non-FLT/FLTP.** **Closed via** a per-encoder `SwrContext` (FLT-in → codec-sample-fmt-out) used in `WriteFrameLocked`, replacing the FLTP-only / raw-float-copy branches. Now correct for FLAC (s16/s32) and any integer/planar codec. Regression test `FFmpegMuxRoundTripTests.Audio_flac_round_trip_preserves_signal` (lossless RMS check).
- [x] **P1-8 Router clock catch-up cap.** **Closed via** re-anchoring the deadline to "now" once the backlog exceeds `MaxCatchupChunks` (64, matching `MediaClock`) in both `WallClockRouterClock` and `PlaybackSlavedRouterClock`.
- [ ] **P1-9 Frame-mode audio tail drain.** Share the tail-drain path between `ReadInto` and `TryReadNextFrame` so frame-mode consumers drain swr and reach `IsExhausted` (`MediaContainerSharedDemux.cs:1447-1452`/`1486-1490`/`1418`; `AudioFileDecoder`).
- [ ] **P1-10 ResamplingAudioSource seekability.** Forward `ISeekableSource`/`Position`/`Duration` through the wrapper (`ResamplingAudioSource.cs:25`).
- [ ] **P1-11 NDI ingest timing.** Use NDI timecode/timestamp when present; fixed cadence only as fallback (`NDIVideoReceiver.cs:176-181`, `218-220`).
- [ ] **P1-12 VideoPlayer restart guard.** If the decode thread fails to stop within the join cap, keep the player blocked and reject `Play()` restart (`VideoPlayer.cs:248-295`).

## Phase P2 — Allocation, contracts, API hardening

- [ ] **P2-1 Lazy-start / pool output-pump threads.** Don't start an `OutputPump` thread until the router starts; or share a bounded pool (`AudioRouter.cs:1502-1521`). Addresses the intermittent suite OOM.
- [ ] **P2-2 VideoRouter conversion outside lock.** Snapshot routes/outputs under `_gate`, then convert/readback/submit outside it (`VideoRouter.cs:547-687`, `746-755`).
- [ ] **P2-3 VideoOutputPump reconfigure drain.** Drain/drop queued frames on a real format change; version queues by format (`VideoOutputPump.cs:105-124`).
- [ ] **P2-4 VideoOutputPump dispose vs blocked Submit.** Don't dispose the inner output until the drainer exits (`VideoOutputPump.cs:~252-283`).
- [ ] **P2-5 Compositor hot-path allocations.** Reuse arrays/leases in `VideoCompositorSource.TryReadNextFrame`; stop `Slots` from allocating per access (`VideoCompositorSource.cs:80`, `162-187`).
- [ ] **P2-6 FFmpeg planar pin/alloc per frame.** `stackalloc`/`fixed`/pooled pinned buffers in the swscale paths (`VideoFileDecoder.cs:745-829`; shared demux `~1280-1363`). 🔎 re-verify first.
- [ ] **P2-7 FFmpeg pass-through per-plane memory-manager alloc.** Pool the `UnmanagedMemoryManager<byte>` wrappers if profiling warrants (`MediaContainerSharedDemux.cs:~1188-1200`). 🔎 re-verify first.
- [ ] **P2-8 FFmpeg audio encoder O(n) pending shift.** Replace `_pending` `List<float>` + `RemoveRange(0,…)` with a ring/offset buffer (`FfmpegAudioEncoder.cs:55-66`).
- [ ] **P2-9 CPU compositor straight-alpha.** Encode alpha mode in metadata/pixel format or normalize at source boundaries (`CpuVideoCompositor.cs:16-22`, `172-204`).
- [x] **P2-10 GL compositor pack pixel-store restore.** **Closed via** saving `GL_PACK_ALIGNMENT`/`GL_PACK_ROW_LENGTH` alongside the existing unpack save and restoring both in the `finally`.
- [ ] **P2-11 LayerConfig.ScaleAnchor.** Implement anchor-aware scale/rotation composition or remove the property (`LayerConfig.cs`, `LayerConfigResolver.cs`, `LayerTransform2D.cs`).
- [ ] **P2-12 LayerHandle thread safety.** Swap immutable config/transition snapshots atomically, or lock mutation+reads (`LayerHandle.cs:36-45`, `54-65`).
- [ ] **P2-13 LayerHandle configure-throw leak.** Wrap `Configure`/`Submit` and dispose the frame unless ownership moved (`LayerHandle.cs:87-89`).
- [ ] **P2-14 StaticFrameSource.FromFrame(copyBacking:false).** Remove the non-copying public mode or require a shared/refcounted backing (`StaticFrameSource.cs:108-131`).
- [ ] **P2-15 NDI live source format probe.** Async `Connect`/`Probe` or `TryGetFormat`/format-changed pattern (`NDIVideoReceiver.cs:210-222`, `NDIAudioReceiver`).
- [x] **P2-16 PortAudio wiring leak.** **Closed via** hoisting `output`/`sinkMain` out of the `try` and, on failure, rolling back router registration (`RemoveOutput`) and disposing the native output. Verified the audio `OutputPump.Dispose` does not dispose the inner output (the host owns it on success), so no double-dispose.
- [ ] **P2-17 PortAudio prefill allocation.** `ArrayPool<float>`/reused scratch in `PrefillFrom` (`PortAudioOutput.cs:~372`).
- [ ] **P2-18 PortAudio companion ownership.** Session/player owns the companion host by default (`MediaPlayerOpenBuilderPortAudioExtensions.cs`).
- [ ] **P2-19 SDL output dispose vs render thread.** Don't dispose wait handles/inner resources until the render thread exits (`SDL3VideoOutput.cs`, `SDL3GLVideoOutput.cs`). 🔎 re-verify first.
- [ ] **P2-20 SDL reconfigure contract parity.** Align `SDL3VideoOutput.Configure` with `SDL3GLVideoOutput.Configure` (`SDL3VideoOutput.cs:122-128`, `SDL3GLVideoOutput.cs:259-320`).
- [x] **P2-21 AudioRouter validate read counts.** **Closed via** an `(uint)read > (uint)scratch.Length` guard in `RunLoop` that logs and clears the chunk (catches negative and over-count) instead of letting `AsSpan(read)` throw on the router thread. Full per-source fault state deferred to the fault-handling work (P0-5a).
- [x] **P2-22 AudioRouter.WaitForIdle vs abandoned chunks.** **Closed via** incrementing `_processed` for each chunk `AbandonQueue` removes, so `WaitForIdle` stops blocking once the queue is genuinely drained.
- [ ] **P2-23 AdaptiveRate fake primary clock.** Only expose `IClockedOutput` when the inner output is clocked, or only wrap non-primary outputs (`AdaptiveRateAudioOutput.cs:38`, `141-144`).
- [ ] **P2-24 AdaptiveRate wrapper disposal.** Own + dispose adaptive wrappers on output remove/router dispose (`AudioRouter.Playback.cs`).
- [ ] **P2-25 VideoFormat/VideoFrame validation.** Add `VideoFormat.Validate`; validate CPU plane count/stride/length at frame creation (`VideoFormat.cs:7-11`, `VideoFrame.Validation.cs`).
- [ ] **P2-26 RouteGainSlot encapsulation.** Expose immutable route snapshots; keep the mutable gain slot internal (`RouteGainSlot.cs:7-16`, `AudioRouter.Routes`).
- [ ] **P2-27 URI/path API strictness.** `OpenFile`/`OpenUri(Uri absolute)`/`OpenStream` with early validation; stop accepting `RelativeOrAbsolute` (`MediaPlayerOpenBuilder.cs:360`).

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
