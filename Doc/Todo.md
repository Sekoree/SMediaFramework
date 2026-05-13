# Media Framework — Review & Improvement Checklist

**Last updated:** 2026-05-13 — **`AvPlaybackCoordinator.Pause`**: when **`AudioPlayer`** is **null**, **`VideoPlayer.Clock.Pause`** so **`MediaClock`** driver stops before shared-mux flush (**`VideoPlaybackSmoke`** + **`VideoPtsClock`**); interrupt path skips **`SeekPresentation`** flush; **`ConsumeDecoderUntilPts`** iteration guard; **`VideoSinkPump.Dispose`** join **30s**; **`MediaContainerAvRouter.Create`**. **Previous:** 2026-05-13 — **`VideoPlayer`**: **`DrainQueue`** before joining decode (full presentation queue + no **`VideoTick`** after **`IsRunning=false`** could deadlock decode on **`SemaphoreSlim.Wait`**); tests **`Pause_completes_when_decode_blocks_on_slot_semaphore_without_ticks`**. **Previous:** 2026-05-13 — **`MediaContainerSharedDemux.RequestVideoDecodeYield`** pulses **`_queueGate`** (faster cooperative pause); **`FeedVideoFromQueue`** re-checks decode yield each iteration; **`SDL3GLVideoSink`** render-thread join **45s** + **`SDL_GL_SetSwapInterval(0)`** before swap when **`_disposed`** (vsync teardown); **`VideoPlaybackSmoke`** stderr on Ctrl+C; **`VideoPlayer`** / **`MediaContainerDecoder`** remarks. **Previous:** 2026-05-14 — **`VideoRouter` fan-out introspection** — **`TryGetInputFanOutPixelFormats`** + **`VideoRouterFanOutPixelFormat`**; **`VideoPlaybackSmoke`** **`[video]`** line (negotiated vs **SDL**/**NDI** pixel paths, GL shader-direct hint, router **CPU swscale** fan-out); **`VideoSinkFanoutFormats`** prefers negotiated pixel format when the branch accepts it (avoids bogus **UYVY** branch on **NV12** dma-buf fan-out); **`VideoCpuFrameConverter.Convert`** pins **any** **`ReadOnlyMemory<byte>`** (unmanaged libav pass-through + **NDI**); **`AudioRouter`** natural-EOF **`RunLoop`** **`finally`** drains pumps + **`IFlushableSink.Flush`** + disposes CTS; **`VideoPlaybackSmoke`** cooperative shutdown (**`WaitOne`** on cancel token, **`Pause(CancellationToken.None)`**, EOF exit when router completes). **Previous:** 2026-05-13 — **`NDIPlayer`** (`Tools/NDIPlayer`): mux-ordered **single-thread** **`TryReadNextFrame`** (shared demux safe); **`MediaContainerSharedDemux.SelectVideoOutputFormatLocked`** clears **`_vPrimedAfterSeek`** when negotiated **pixel format** changes (fixes BGRA primed frame vs NV12 **`NDIVideoSender`**); wall pacing uses a **sub-millisecond tail** plus default **wall-anchor drift leak** (**`--no-wall-drift-correct`** disables). **NDI egress (smoke + libs):** **`NDIVideoTimecodeMode`** (**`PresentationRelativeTicks`** in **`VideoPlaybackSmoke`**; **`MuxerPresentationTicks`** in **`NDIPlayer`**), **`NDIOutput.ResetVideoPresentationTimecodeAnchor()`**, **`PaceBeforePack`** **`Thread.Sleep`** remainder (no **`SpinWait`**), NDI **`VideoSinkPump`** default **8** + **`--ndi-clock-video`**, **`--ndi-disable-wall-pace`**, **`--ndi-video-pump-frames=`**, **`--ndi-video-tc=pts|synth`**. **`dotnet test MFPlayer.sln -c Release`** green. **Previous:** NDI egress review notes (§**NDI egress A/V sync & Monitor video health**). **Previous:** 2026-05-12 — **Windows NV12 D3D11→GL**: **`Nv12Win32SharedHandleGpuUploader`** (`**`WGL_NV_DX_interop`** GPU path, staging **`Map`/`glTexSubImage2D`** fallback), **`SDL3GLVideoSink`** borrows a host **`ID3D11Device`** for NV12 + **`RetainD3D11SharedHandleForGl`**, **`VideoPlaybackSmoke --d3d11-gl`**; **`MediaDiagnostics.LogInformation`**; **`PortAudioOutput.CallbackFaultException`** (first PA callback fault). **Previous:** **`PortAudioOutput.ElapsedSinceStart`**: **`Pa_GetStreamTime`** + per-segment anchor against **`PlayedSamples`** so **`IPlaybackClock`** advances between output callbacks (fixes **~47 Hz** **`MediaClock`** / **`VideoPlayer`** gating when **`PlayedSamples`** only moved once per buffer). **Previous:** 2026-05-14 — **DMA-BUF NV12 multi-sink**: **`VideoDmabufNv12Backing`** refcount + **`VideoFrame.CreateNv12DmabufSharedReference`**; **`VideoRouter`** / **`VideoOutputRouter`** fan-out when all branches stay **NV12** (no **`VideoCpuFrameConverter`** on dma-buf). **Previous:** **`AudioRouter.AddSink`** / **`AudioPlayer.AddOutput`**: optional **per-sink `pumpCapacityChunks`** ( **`SinkPumpStats.PumpCapacityChunks`** ); **`VideoPlaybackSmoke`** NDI audio default **24** with **`--ndi-audio-pump-chunks=`** override. **Previous:** **`NDIAudioSink`**: native packed buffer grows with **≥2× headroom** + power-of-two sizing, **`NativeMemory.Realloc`** (fewer free/alloc cycles when chunk sizes shift). **`OSCServer.Dispose` / `DisposeAsync`**: **`#if DEBUG`** **`ILogger.LogDebug`** on best-effort shutdown catches (no **`S.Media.Core`** dependency). **Previous:** **`MediaClockExtensions.SetMasterChain`** (**`IMediaClock`** + **`CompositePlaybackClock`**) for priority-ordered **`IPlaybackClock`** masters; **`Doc/Todo.md`** suggested backlog #6 marked done. **Previous:** **`FFmpegRuntime.EnsureInitialized`**: first init wins; a later conflicting **`rootPath`** is ignored and logged once (**`MediaDiagnostics.LogWarning`**). **`AudioRouter.Resume`** `<remarks>` document pause→resume mitigations. **`SinkPump.Dispose`**: **`#if DEBUG`** log on **`Cancel`** failure. **`Doc/Todo.md`**: NDI ingest **`IPlaybackClock`** line synced to shipped **`NdiIngestPlaybackClock`**. **Previous:** **`AudioRouter.Pause`**: **`IFlushableSink`** snapshot + **`Flush`** in the same stop pass as the run-loop teardown (no second **`lock`**); **`Doc/Todo.md`** checkboxes synced to **`MediaContainerSharedDemux`** / **`AvPlaybackCoordinator`** seek guidance. **Previous (2026-05-13):** **`AudioPlayer.RemoveOutput`** auto-promotes the next **`IClockedSink`**; **`AudioRouter.RetargetSlaveClock`** + **`TryGetSink`**; **`SinkPump.Commit`** drops without allocating when pool + queue are empty; **`NDIAudioSink`** stamps **100 ns** timecodes; **`VideoPlaybackSmoke`** uses **`VideoPtsClock`** when audio is missing, larger **PortAudio** queue target, **`ndiDr`** HUD, **`clockAudio: true`** for NDI; **`AvPlaybackCoordinator.Play`** optional **`videoOnlyMaster`**, **`Seek`** updates the video clock when audio is null.

**Previous update:** 2026-05-13 — **Tier E/F + teardown + audio decode:** **`Doc/Todo.md`** — **§Tier F** policy + rows **27–36** (mirrors **E14–E20**, checklist **`[ ]`** tails); **Tier E** **17**/**19**/**20** **`[~]`** partials; **`AudioFileDecoderOpenOptions`**, **`NDIOutput`** / **`NDIVideoSender`** / **`SDL3GLVideoSink`** / **`YuvVideoRenderer`** / **`Nv12Win32SharedHandleGpuUploader`** DEBUG teardown logs; **`CompositePlaybackClock`** micro-stress test; counts **Core** **257**, **FFmpeg** **77**.

**Previous update:** 2026-05-13 — **Tier E + Tier F + deferred index:** **`Doc/Todo.md`** — **§Deferred work** now lists **§Tier F — Deferred registry** (rows **21–26**); **Tier E** rows **14–20** rewritten with **`[ ]`** + acceptance hints; **Tier D**/**B** cross-links; NDI audit item **6** fan-out text updated for **`TryCreateNv12CpuFanOutViews`**; **`ChannelMap`** / **`VideoFileDecoder`** / **`AudioFileDecoder`** `<remarks>`.

**Previous update:** 2026-05-13 — **Tier D (rows 11–13):** **`AudioRouter`** sample-rate docs (**`ReconfigureSampleRate`** vs **`IsRunning`**, **`IClockedSink`** in drift `<remarks>`); **`CompositePlaybackClock`** instant handoff `<remarks>` + test **`WhenBothAdvancing_UsesHigherPriorityElapsed_NotBlended`**; **`Doc/Todo.md`** Tier **D** **[x]** + backlog **#6**/**#12** lines; **Core.Tests** **256**.

**Previous update:** 2026-05-13 — **Tier C (CPU NV12 fan-out):** **`VideoFrame.TryCreateNv12CpuFanOutViews`** refcounted shared planes; **`VideoRouter`** / **`VideoOutputRouter`** wire-up; **`NDIVideoSender`** remarks; **`Doc/Todo.md`** Tier **C.9** + test counts (**Core** **255**, **FFmpeg** **76**).

**Previous update:** 2026-05-13 — **Tier C (continued):** **`RUN_NDI_MUX_SOAK`** mux playhead soak; **`NDIOutput.ClearConnectionMetadata`** / **`AddConnectionMetadata`**; **`NDIVideoSender.PackI420`** bulk paths; **`VideoPlaybackSmoke`** / **`NDIPlayer`** **`--ndi-wait-first-receiver-ms`**; **`Doc/Todo.md`** Tier **C** + **NDI.Tests** **15**.

**Previous update:** 2026-05-13 — **Tier C (NDI labs + egress):** **`RUN_NDI_EGRESS_SOAK_ROUNDS`** (clamp **1k–10M** with **`RUN_NDI_EGRESS_SOAK=1`**); **`NDIOutput.GetReceiverConnectionCount`** + **`ConnectionCount`** explicit zero-timeout; **`NDIVideoSender.PackNv12`** bulk contiguous copy; **`VideoPlaybackSmoke`** usage lab env line; **`Doc/Todo.md`** Tier **B** deferred bullets + Tier **C** rows **7–10** + **NDI.Tests** **14**.

**Previous update:** 2026-05-13 — **`VideoRouter`** / **`VideoSinkPump`**: **`VideoSinkPumpMetrics`** (**`MaxQueueDepth`**, **`CurrentQueuedDepth`**, drops, submitted); **`TryGetVideoSinkPumpMetrics`** overload; throttled **`MediaDiagnostics`** on queue-full drops; **`VideoPlaybackSmoke`** HUD **`ndiVidQ`**; tests.

**Previous update:** 2026-05-14 — **`Doc/Todo.md`** §**Implementation verification** test counts; **`D3D11InteropUtility`** + one-time DXGI adapter LUID log for **owned** **`D3D11GlInteropDeviceHost`** in **`SDL3GLVideoSink`**; **`MediaFramework-concepts.md`** negotiation + Win32 NV12 note.

**Previous update:** 2026-05-13 — **Backlog table rows 1–3:** **`D3d11TextureKeyedMutexScope`** + **`Nv12Win32SharedHandleGpuUploader`**; **`HardwareVideoWin32Nv12`** + **`YuvVideoRenderer.Upload(HardwareVideoSurfaceDescriptor)`**; **`ChannelMap.TryAccumulatePackedPermutationInterleaved`**; lab **`WIN32_NV12_D3D11_INTEROP_STRESS_ROUNDS`**; **`Doc/Todo.md`** counts.

**Previous update:** 2026-05-14 — **Tier B (host + GL diagnostics):** **`MediaContainerPlaybackHost`** (**`S.Media.PortAudio`**, **`VideoPlaybackSmoke`**); **`AudioPlayer.Timeline`**; **`LinuxDmabufGlHardwareFormats`** per-family **`GetPrimeGlImportBlocker`** + tests; **`Doc/Todo.md`** Tier **B** + spot-check.

**Previous update:** 2026-05-14 — **Tier B (tail):** **`LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker`** + enum parity test; **`Nv12DmabufGpuUploader`** ties **`ArgumentException`** to blocker text; **`VideoRouter`** dma-buf / Win32 shared **`Submit`** errors include **input id**; **`PlaybackTimelineClockExtensions.SubscribePositionChanged`** + **`MediaClockTests`**; **`Doc/Todo.md`** Tier **B** + spot-check.

**Previous update:** 2026-05-14 — **`S.Media.FFmpeg.AvRouter`**: pairs **`MediaContainerDecoder`** + **`IAvPlaybackSession`** (**`Pause`**/**`SeekCoordinated`** default **`FlushCodecPipelines`**); **`VideoPlaybackSmoke`**; **`AvRouterTests`** + **`S.Media.Core.Tests`** → **`S.Media.FFmpeg`** project ref; **`Doc/Todo.md`** Tier **5** + spot-check.

**Previous update:** 2026-05-14 — **Tier B (strategy B + AvRouter stepping stone):** **`IPlaybackTimeline.PlaybackRate`** (**`MediaClock`** / **`FakeMediaClock`** fixed **1.0** ); **`IAvPlaybackSession`** + **`MediaPlaybackSession : IAvPlaybackSession`** (**`Timeline`** DIM → **`Clock`** ); **`MediaPlaybackSession_ImplementsIAvPlaybackSession_TimelineIsClock`**; **`Doc/Todo.md`** unified Tier **5**/**6** + spot-check.

**Previous update:** 2026-05-13 — **Tier B (partial):** **`LinuxDmabufGlHardwareFormats`**; **`VideoRouter`** configure-time NV12 fan-out + CPU-branch **`ILogger`** warning; **`IPlaybackTimeline`** + **`IMediaClock : IPlaybackTimeline`**; **`WIN32_NV12_D3D11_STRICT_TEXTURE_ADAPTER_LUID`**; **`Doc/Todo.md`** §Unified Tier A complete + Tier B deltas.

**Previous update:** 2026-05-13 — **Tier A (Windows D3D11 NV12 → GL):** **`D3d11TextureKeyedMutexScope.TryAcquireForGpuRead`** (strict fail when mutex exists but **`AcquireSync(0)`** fails); **`WIN32_NV12_D3D11_KEYED_MUTEX_TIMEOUT_MS`**; **`D3D11InteropUtility.TryGetAdapterLuidFromTexture`**; **`VideoWin32Nv12Backing`** remarks; stress **`KeyedMutexScope_TryAcquireForGpuRead_RoundTrips_WhenStressEnabled`**; **`Doc/Todo.md`** §Unified Tier A + Historical **#1**.

**Previous update:** 2026-05-13 — **`Doc/Todo.md`**: **§Unified backlog (prioritized by impact)** (single ordered list, end of doc) consolidates former **“remains …”** gaps + **`[ ]`** / **`[~]`** items; **§Historical suggested backlog (archive)** preserves numbered **#1–#12** with full inline status; **§Remaining `[ ]` backlog** table folded into the unified list.

**Previous update:** 2026-05-16 — **`VideoWin32Nv12Backing.LibavD3D11*ComPtr`** + **`D3D11VaNv12BackingFactory`**; **`Nv12Win32SharedHandleGpuUploader.TryOpenD3D11GpuTexture`** (same-device skips **`OpenSharedResource`**); **`D3D11InteropUtility.TryValidateTexture2DComPointer`**; **`Doc/Todo.md`** backlog rows 1–2.

**Previous update:** 2026-05-15 — **`WindowsNv12D3D11TextureInterop`** + **`HardwareVideoMemoryKind.Win32D3D11Nv12Texture`** + **`HardwareVideoSurfaceDescriptor.D3D11DeviceComPtr`** (Core-only libav D3D11 NV12 COM descriptor); **`D3D11VaNv12BackingFactory`** `<remarks>`; **`HardwareVideoInteropTests`**; **`Doc/Todo.md`** backlog row 2 **partial**; **`MediaFramework-concepts.md`**.

**Previous update:** 2026-05-15 — **`Nv12Win32SharedHandleGpuUploadProfiling`** (**`MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD=1`**); **`RUN_WIN32_NV12_D3D11_INTEROP_STRESS`** + **`Win32Nv12D3d11InteropStressTests`**; **`Doc/Todo.md`** zero-host design subsection + **`S.Media.OpenGL.Tests`** **`ProfilingTestProcessDefaults`**.

**Previous update:** 2026-05-13 — **`VideoPlaybackSmoke`**: one **`MediaContainerDecoder`** (was separate **`VideoFileDecoder`** + **`AudioFileDecoder`**); audio registered with **`AudioPlayer.Router.AddSource`**; **`MediaPlaybackSession`** for **`Play`**/**`Pause`** (**`flushSharedMuxAfterPause`** → **`MediaContainerDecoder.FlushCodecPipelines`**); **`MediaPlaybackSession_Pause_forwards_flush_delegate`** test; HUD **`mux shared`** + mux **`ISeekableSource`** positions.

**Previous update:** 2026-05-13 — **Windows D3D11 GL spike:** **`D3D11GlInteropDeviceHost`** (**`S.Media.OpenGL`**) — owned **`D3D11CreateDevice`** (BGRA + **`VideoSupport`**, feature levels **11.1→10.0**); **`SDL3GLVideoSink`** uses it instead of inline Vortice; **`S.Media.SDL3`** drops direct **`Vortice.Direct3D11`** package ref; **`D3D11GlInteropDeviceHostTests`** (headless create / dispose / double-**`Dispose`**). **`S.Media.OpenGL.Tests`** **7**.

**Previous update:** 2026-05-13 — **`ChannelMap`** / **`AudioRouter.ApplyRoute`**: SIMD **`TryAccumulatePackedIdentityInterleaved`** for **`ChannelMap.Identity(N)`** with **`N ≥ 3`** (same-width interleaved additive passthrough); **`ChannelMapTests.PackedIdentitySimd_*`**. **`S.Media.Core.Tests`** **229** with **`dotnet test MFPlayer.sln -c Release`**.

**Previous update:** 2026-05-13 — **`Doc/Todo.md`** audit + **`AudioRouterControlTests.NaturalEof_FlushesFlushableSinks`** (**`IFlushableSink.Flush`** after natural EOF, **`FinishRunLoopThreadLifetime`**); NDI “quick verification” step 4 corrected (aggregator shipped); **`SinkPump.Commit`** spot-check row reconciled.

**Previous update:** 2026-05-13 — **NDI egress tooling:** **`NdiEgressMuxPlayheadClock`** (**`IPlaybackClock`**, mux max-PTS playhead before send); **`RUN_NDI_EGRESS_SOAK=1`** optional **`NdiEgressPresentationTimeline`** sequential soak (**`NdiEgressPresentationTimelineTests`**); **`NdiEgressMuxPlayheadClockTests`**.

**Previous update:** 2026-05-13 — **NDI egress shared presentation timeline:** **`NdiEgressPresentationTimeline`** for **`NDIVideoTimecodeMode.PresentationRelativeTicks`** — **`NDIOutput`** wires one anchor to **`NDIVideoSender`** + **`NDIAudioSink`** (**`Submit(in AudioFrame)`**); **`ResetVideoPresentationTimecodeAnchor`** clears it; **`NdiEgressPresentationTimelineTests`**. **`VideoPtsClockTests.Pause_FreezesElapsed`** tolerance widened for parallel **`dotnet test`** load.

**Previous update:** 2026-05-14 — **`ChannelMap`** / **`AudioRouter.ApplyRoute`**: SIMD **swapped stereo spread** — **`TryAccumulateStereoDuplexWideSwappedInterleaved`** (**`[1,0,1,0]`**), **`StereoToNSwapped`**, **`TryAccumulateStereoToNInterleavedSwapped`**; **`NdiEgressPresentationTimelineTests`**: optional **`RUN_NDI_EGRESS_SOAK_STRESS=1`** (**1M** rounds).

**Previous update:** 2026-05-14 — **Pass-through descriptor arena:** **`ReturnPassThroughDescriptors`** (**`VideoFileDecoder`**, **`MediaContainerSharedDemux`**) — **`Array.Clear`** plane + stride arrays **before** acquiring **`_passThroughArena`** (shorter **`PassThroughArenaProfiling.RecordReturn`** window); **pool-at-cap** overflow path now clears abandoned descriptor arrays.

**Previous update:** 2026-05-14 — **`AvPlaybackCoordinator`** / **`MediaPlaybackSession`**: optional **`flushSharedMuxAfterPause`** (wire **`MediaContainerDecoder.FlushCodecPipelines`**); **`NDIPlayer`** help text; **`MediaContainerDecoder.FlushCodecPipelines`**; **`NDIOutput`** send-side tally/metadata; **`AudioRouter.SampleRate`** docs.

**Previous update:** 2026-05-13 (late) — **`NDIPlayer`**: PTS-ordered mux pump, wall pacing **tail + anchor drift** (**`--no-wall-drift-correct`**), **`Doc/Todo.md`** NDI egress § + **`MediaContainerDecoder`** checkbox (**`EAGAIN`** pending packets, seek-prime discard on **`SelectOutputFormat`**).

**Previous update:** 2026-05-12 — **Checklist hygiene**: evening audit **`[~]`** items reconciled to **`[x]`** where shipped; split **`SinkSlavedRouterClock`** vs dynamic-router-rate **`[ ]`**; **`RUN_MEDIA_SOAK=1`** optional heavier **`MediaContainerDecoderSoakTests`** rounds; **`VideoFileDecoder`** `<remarks>` on pass-through arena; **`Doc/Todo.md`** executive **PortAudio** / decoder lines.

**Previous update:** 2026-05-12 — **Per-sink adaptive resampling**: **`PumpPressurePlaybackHintMonitor`** sink-id overload; **`S.Media.FFmpeg.Audio.AdaptiveRateAudioSink`** (**`swresample`**, **`PumpPressure`**-driven); tests; **`AudioRouter`** `<remarks>` + **`Doc/Todo.md`** roadmap.

**Previous update:** 2026-05-12 — **`SinkSlavedRouterClock`**: expanded **`SinkSlavedRouterClockTests`** (delegation, lazy wall fallback, sink null ↔ present toggling, pre-cancelled **`CancellationToken`** on sink + wall paths, ctor validation); **`<remarks>`** on **`resolveSink`** threading vs **`AudioRouter`** lock-free snapshot.

**Previous update:** 2026-05-12 — **Suggested backlog #10**: teardown diagnostics — **`SinkPump.CompleteAdding`** **`ObjectDisposedException`** and **`VideoPlayer.OnVideoTick`** submit failure logs only in **`#if DEBUG`** ( **`MediaDiagnostics`** ); **`OSCServer`** / **`AudioRouter.Dispose`** / **`SinkPump.Dispose`** Cancel / **`AudioPlayer`** / inner frame-**`Dispose`** already matched this pattern.

**Previous update:** 2026-05-12 — §**Suggested backlog #3** (fuzz harness): **`ApplyAndApplyAdditive_MultiChannelRandomLayouts_MatchNaive`** (**3–12** src ch); **`AudioRouterTests.RunLoop_MultiSourceStereoDupAndFullSilence_MatchesReference`**; live seeded graph map pool **`[-1,-1]`**; **`AudioRouterLiveGraphSeededTests`** **`stackalloc`** hoisted (**CA2014**).

**Previous update:** 2026-05-12 — **`ChannelMap`**: SIMD **`TryAccumulateStereoDupSingleChannelInterleaved`** (**`[0,0]`** / **`[1,1]`**) + **`TryAccumulateStereoFullSilenceStereoInterleaved`** (**`[-1,-1]`**); **`ChannelMapTests`** coverage; **`AudioRouter.ApplyRoute`** steady-gain fast-path order aligned with **`ApplyAdditive`**.

**Previous update:** 2026-05-13 — §**Implementation verification (2026-05-13)** re-checked representative `[x]` claims + full `dotnet test MFPlayer.sln` (green).

**Previous update:** 2026-05-12 (night, +architecture pass) — §**Architecture roadmap — A/V router unification & clocks** (dynamic audio vs video fan-out, `AudioPlayer` primary hand-off, composite / NDI clock ideas, `MediaContainerDecoder` tie-in).

**Previous update (2026-05-12 morning):** Implementation audit: every previously-checked box was verified present. §"Audit findings (2026-05-12)" called out one likely bug (P010 bit-scale), three minor bugs, four robustness/contract gaps, plus optimization and cleanup opportunities.

**Previous update (2026-05-11):** Linux NV12 DRM PRIME dma-buf → EGL/GL (`RetainDmabufForGl`, `YuvDmabufEglInterop`, `Nv12DmabufGpuUploader`); FFmpeg VAAPI zero-copy MVP.

Legend: `[x]` done, `[~]` partial / best-effort, `[ ]` intentional future work. **Open backlog (single priority list):** **§Unified backlog (prioritized by impact)** at the **end** of this document; **§Historical suggested backlog (archive)** keeps the original **#1–#12** text with inline status tags. **Cross-tier `[ ]` tails** also appear in **§Tier F — Deferred registry** (rows **21–36**, including **Tier E** mirrors **27–33**).

---

## Deferred work — index

Open work (**`[~]`** partial items, **`[ ]`** intentional gaps, and prose **“remains …”** limitations) is tracked in three places:

- **§Unified backlog (prioritized by impact)** — single ordered list at the **end** of this document (**highest → lowest** estimated product/engineering impact). This is the canonical “what is still open” view (includes items that previously lived only in checklist prose or the old table).
- **§Tier F — Deferred registry** — compact **`[ ]`** list (rows **21–36**) for cross-tier tails **plus** mirrored **Tier E** acceptance (**27–33**); **policy:** new deferrals land here.
- **§Historical suggested backlog (archive)** — the original numbered **#1–#12** list with full inline **`[x]`** / **`[~]`** / **`[ ]]`** annotations preserved for traceability and PR archaeology.

Cross-references elsewhere in this file to the old **“§Remaining `[ ]` backlog”** table or **“§Suggested backlog below”** should be read as pointing at the **unified** list unless they explicitly mean the **archive** section.

### Windows zero-host — libav-internal DXGI (design backlog)

**Shipped today:** frames carry **NT shared handles** (**`VideoWin32Nv12Backing`**, or **zero** NT handles when libav COM pointers are set) plus optional non-owning libav **`ID3D11Device`** / **`ID3D11Texture2D`** COM pointers; the **GL path** uses a negotiated borrow, SDL’s optional **`D3D11GlInteropDeviceHost`**, or (opt-in **true zero-host**) **no** owned interop device — **`YuvVideoRenderer`** may lazy-create **`Nv12Win32SharedHandleGpuUploader`** from **`LibavD3D11DeviceComPtr`** on the first Win32 NV12 frame; then **`OpenSharedResource`** (skipped when decode device matches) + **`WGL_NV_DX_interop`** or staging **`Map`** / **`glTexSubImage2D`**; when **`IDXGIKeyedMutex`** is present, **`D3d11TextureKeyedMutexScope.TryAcquireForGpuRead`** (key **0**) guards the copy/interop window and **aborts** if acquire fails; **`HardwareVideoSurfaceDescriptor`** → **`YuvVideoRenderer.Upload`** path.

**Why a Core-only “libav opens the DXGI texture for GL” path stays deferred:** D3D11VA / **`AVHWFramesContext`** owns **`ID3D11Texture2D`** lifetime, possible **DXGI keyed mutex** usage, and driver-specific export rules. **`WindowsNv12SharedHandleInterop`** in Core remains descriptor-only by design. Re-opening the same backing on a **different** device without the negotiated borrow story risks subtle failures on multi-GPU; **`IHardwareD3D11GlInteropSource.TryGetHardwareD3D11AdapterLuid`** exists to validate adapter alignment for the shipped handle path. Engineering follow-ups tied to this surface are listed under **§Unified backlog** (Windows GL / Core-only rows).

**Checklist — Tier E row 14** (explicit non-goal; registry **§Tier F** rows **27** / **34** / **35**): shipped Win32 NV12 → GL may use **NT shared handles**, **libav `ID3D11Device`/`ID3D11Texture2D` COM** on **`HardwareVideoSurfaceDescriptor`** / backing, a **negotiated device borrow**, or SDL’s **`D3D11GlInteropDeviceHost`**. **True zero-host** in tools means **no SDL-owned** D3D11 interop device while **still** binding from libav COM when the frame exposes it — **not** “no `ID3D11Device` COM on frames.” **GL with zero COM on the media path** and **Core-only decode→GL without any D3D11 on descriptors/frames** stay **out of scope** here until libav lifetime + mutex ownership from Core alone is tractable.

**Future shapes (non-binding):** keep **one shared `ID3D11Device`** (negotiator borrow + optional owned helper) as the primary contract; if FFmpeg ever exposes stable D3D11 texture pointers on **`AVFrame`**, pair them with explicit **`AVBufferRef`** rules and LUID checks before GL registration; if keyed mutex is required, serialize decode vs GL with it (large API surface). **2026-05-15:** **`WindowsNv12D3D11TextureInterop`** + **`Win32D3D11Nv12Texture`** descriptor kind pack libav **`ID3D11Texture2D`** + **`ID3D11Device`** COM pointers (no DXGI **`CreateSharedHandle`**) for portable **`IHardwareVideoInterop`** consumers. **2026-05-16:** **`VideoWin32Nv12Backing`** + **`Nv12Win32SharedHandleGpuUploader`** same-device path avoids **`OpenSharedResource`** when COM pointers match.

---

## Implementation verification (2026-05-13)

**Automated:** `dotnet build MFPlayer.sln` (0 warnings) and `dotnet test MFPlayer.sln` — **S.Media.Core.Tests** 279, **S.Media.FFmpeg.Tests** 82, **S.Media.PortAudio.Tests** 20, **S.Media.OpenGL.Tests** 24, **S.Media.NDI.Tests** 15 — all passed.

**Optional Windows NV12 D3D11→GL diagnostics / lab stress:** **`MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD=1`** enables **`Nv12Win32SharedHandleGpuUploadProfiling`** (counts **`TryUpload`** attempts, interop vs staging successes, interop miss before staging, dual-path failures). **`RUN_WIN32_NV12_D3D11_INTEROP_STRESS=1`** enables **`Win32Nv12D3d11InteropStressTests`** (**`D3D11GlInteropDeviceHost`** churn, **`KeyedMutexTexture_AcquireSync_ReleaseSync_RoundTrips`**, **`KeyedMutexScope_TryAcquireForGpuRead_RoundTrips_WhenStressEnabled`**); optional **`WIN32_NV12_D3D11_INTEROP_STRESS_ROUNDS`** (**1k–500k**, default **20k**). Optional **`WIN32_NV12_D3D11_KEYED_MUTEX_TIMEOUT_MS`** (**1–60000**, default **2000**) for **`Nv12Win32SharedHandleGpuUploader`** **`IDXGIKeyedMutex.AcquireSync`** timeout. Optional **`WIN32_NV12_D3D11_STRICT_TEXTURE_ADAPTER_LUID=1`** rejects **`ID3D11Texture2D`** when its DXGI adapter LUID ≠ uploader device LUID. **`S.Media.OpenGL.Tests`** uses a **`ModuleInitializer`** to force profiling **off** unless a test opts in (same pattern as **`MF_MEDIA_PROFILE_PASS_THROUGH_ARENA`** in FFmpeg tests).

**Spot-checked `[x]` items (code matches the checklist, no untick):**

| Claim in doc | Verified in code |
|----------------|------------------|
| P010 `bitScale` / high 16-bit storage | `GlVideoFormatSupport.cs` — `P010` recipe `bitScale: 1f` + comment; planar 10-bit uses `65535f/1023f` etc. |
| NV12 dma-buf upload + GL errors + no persistent EGLImages | `Nv12DmabufGpuUploader` in **`S.Media.OpenGL/EglDmabufNv12Uploader.cs`** — `GetError()` after each `glEGLImageTargetTexStorageEXT`; both `eglDestroyImage` in `TryUpload` success/fail paths |
| DRM interop size guard | `AvDrmFrameDescriptorInterop.WarnIfInteropSizeMismatchLp64LoggedOnce()` from `DrmPrimeNv12BackingFactory.TryParseNv12` |
| HW decode `NativePixelFormats` includes NV12 without PRIME | `VideoFileDecoder` sets `_nativePixelFormats = [PixelFormat.Nv12]` on software NV12 from hw contexts |
| Passthrough / converted frame pooling | `VideoFileDecoder` — `_passThroughArena`, `ArrayPool<byte>.Shared` in `BuildConvertedFrame` |
| ctor-bound frame upload | `YuvVideoRenderer` — `_uploadFromFrame = CreateUploadFromFrameDelegate(...)` |
| `NDIVideoSender.PaceBeforePack` sub-ms | coarse `Thread.Sleep` (minus 1 ms) + `Thread.Sleep(remainder)` until `deadlineTicks` (no `SpinWait`) |
| `MediaClock.Pause`/`Stop` + cancellation | `MediaClock.cs` — `Pause(CancellationToken)` drives cooperative driver join |
| SDL `Submit` disposed throw + PresentFrame logs | `SDL3GLVideoSink` / `SDL3VideoSink` — `ObjectDisposedException.ThrowIf`; `MediaDiagnostics.LogError` on PresentFrame |
| `VideoPlayer.OnVideoTick` Submit errors | `MediaDiagnostics.LogError(ex, "VideoPlayer.OnVideoTick sink Submit")` |
| Win32 NV12 same-device texture validation | `D3D11InteropUtility.TryValidateTexture2DComPointer` — **`D3D11InteropUtilityTests`** (Windows) |
| `PortAudioOutput` callback fault detail | `CallbackFaultException` — first `catch` in native callback (`Interlocked.CompareExchange`); cleared on next `Start` | §**1.1** “prior GL correctness” items (unpack restore, viewport fit, HDR uniforms, all samplers) — no regressions observed in tests/build; treat as inherited from the prior audit unless you touch that code.

**Navigation:** Linux dma-buf uploader class **`Nv12DmabufGpuUploader`** lives in **`S.Media.OpenGL/EglDmabufNv12Uploader.cs`**; supported PRIME→GL **`PixelFormat`** values and diagnostics are in **`LinuxDmabufGlHardwareFormats`** (**`IsSupportedForPrimeGlImport`**, **`GetPrimeGlImportBlocker`**). Windows shared-handle uploader is **`Nv12Win32SharedHandleGpuUploader`** in **`S.Media.OpenGL/Nv12Win32SharedHandleGpuUploader.cs`** (**`D3d11TextureKeyedMutexScope`** keyed-mutex acquire in **`S.Media.OpenGL/Internal/D3d11TextureKeyedMutexScope.cs`**). Opt-in Win32 NV12 upload counters: **`Nv12Win32SharedHandleGpuUploadProfiling`** in **`S.Media.OpenGL/Diagnostics/Nv12Win32SharedHandleGpuUploadProfiling.cs`**. Owned Windows GL-helper D3D11 device is **`D3D11GlInteropDeviceHost`** in **`S.Media.OpenGL/D3D11GlInteropDeviceHost.cs`**. COM / DXGI LUID helpers for Win32 NV12 GL are **`D3D11InteropUtility`** in **`S.Media.OpenGL/D3D11InteropUtility.cs`** (**`TryGetAdapterLuid`**, **`TryGetAdapterLuidFromTexture`**).

**Silent `catch` — doc vs code:** The evening audit still applies to **playback hot paths** (`OnVideoTick` outer catch, SDL pump / render-loop `PresentFrame`, event `SafeRaise*`). **Dispose / teardown** still intentionally swallows exceptions in several places (`SinkPump.Dispose` around `_cts.Cancel()`, `SDL3GLVideoSink` GL/renderer dispose, `NDIOutput` / `NDIVideoSender.FlushAsync`, `YuvVideoRenderer` uploader dispose). **`AudioRouter.Dispose`**, **`VideoPlayer.Dispose`** / **`VideoPlayer.OnVideoTick`** frame **`Dispose`** after a failed **`Submit`**, and **`AudioPlayer.Dispose`** log failures in **`#if DEBUG`** via **`MediaDiagnostics`**. **`OSCServer.Dispose`** / **`DisposeAsync`** use **`ILogger`** **`LogDebug`** on cooperative shutdown failures (**`#if DEBUG`** in **`Dispose`**). Tier **E** **19** treats this **`MediaFramework/`** inventory as reviewed; new code paths remain in **§Tier F** row **32**.

---

## Deep library audit (2026-05-13) — all assemblies in `MFPlayer.sln`

Scope: every shipping library under **`MediaFramework/`** (managed wrappers + tools), not third-party native trees. **Tests:** `dotnet test MFPlayer.sln` still green after fixes noted below.

### `S.Media.Core`

- **Strengths:** `AudioRouter` immutable graph snapshots + per-sink `SinkPump`; `MediaClock` cooperative driver shutdown; `VideoPlayer` queue + late-frame policy documented in code.
- **Watch:** Multi-sink ppm drift remains design-level (see **§Unified backlog** and Architecture roadmap). **`AudioRouter.Pause`** / **`Flush`** snapshotting is now **`[x]`** (single **`_gate`** pass).
- **`IHardwareVideoInterop`:** [x] **`WindowsNv12D3D11TextureInterop`** + **`HardwareVideoMemoryKind.Win32D3D11Nv12Texture`** — portable libav D3D11 NV12 **COM** surface descriptor (**`D3D11DeviceComPtr`** + texture pointer + array slice in **`Modifier`**); no DXGI **`CreateSharedHandle`** in Core. **`VideoWin32Nv12Backing`** + **`Nv12Win32SharedHandleGpuUploader`** implement same-device decode→GL without **`OpenSharedResource`** when pointers match.

### `S.Media.FFmpeg`

- **`FFmpegRuntime.EnsureInitialized`:** first successful call wins; a later call with a **different** non-null `rootPath` is ignored — [x] **`<remarks>`** + one-time **`MediaDiagnostics.LogWarning`** when the requested path disagrees with the active **`ffmpeg.RootPath`** (hot-swap still requires a new process).
- **Decoders:** `VideoFileDecoder` enables libav **frame** or **slice** threading on **software** decode when no hardware device context is attached (`DecoderThreadCount`, default auto from CPU count, capped); threading is **not** enabled on the active hardware-accelerated open path. `AudioFileDecoder` forwards optional **`CodecThreadCount`** and sets **`thread_type`** when the audio codec advertises frame/slice threading (see **`LibavCodecThreadType`**); many PCM-like decoders still ignore extra threads. [x] **Passthrough descriptor arena** — `VideoFileDecoder` / `MediaContainerSharedDemux` share **`PassThroughDescriptorArena`** (per–plane-count fixed **`PoolCap`** slots, Treiber free-list for rent/return, **`Array.Clear`** before push/early-out, dispose flag for **`Return`**). Optional **`MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1`** + **`PassThroughArenaProfiling`** counters (CAS-loop wall time) for tuning.

### `S.Media.PortAudio`

- **`PortAudioOutput`:** ring math and `Volatile` indices match the stated SPSC model. **`Callback`** wraps the body in `try/catch` and returns `paAbort` on any exception (cannot throw across native boundary). **`CallbackFaulted`** is a lock-free diagnostic flag set before `paAbort`; avoid logging inside the callback (RT thread). [x] Poll **`CallbackFaulted`** from another thread for diagnostics. [x] **`CallbackFaultException`** — first caught exception is retained (**`Interlocked.CompareExchange`**, first fault wins), cleared at the next **`Start`**; inspect from a non-callback thread (no logging inside the RT callback). [x] **`IPlaybackClock.ElapsedSinceStart`** uses **`Pa_GetStreamTime`** between callbacks (per-segment anchor to **`PlayedSamples`** after **Start**/**Flush**/**Stop**) so **`MediaClock`** playhead is not quantized to the output-buffer cadence alone — **`S.Media.PortAudio.Tests`** integration probe when a device exists.
- **`PortAudioRuntime`:** ref-counted `Pa_Initialize` / `Pa_Terminate` pairing is consistent with ctor/dtor paths in `PortAudioOutput`.

### `S.Media.NDI`

- **`NDIAudioReceiver`:** format unknown until first frame — documented; snapshot swap on format change drops the old ring (acceptable). **`samples * channels`** is computed in **`long`** and rejected when **`<= 0`** or **`> Array.MaxLength`** before **`float[]`** allocation / **`NDIAudioInterleaved32f`** pin, so pathological SDK dimensions cannot overflow **`int`** math.
- **`NDIAudioSink`:** **`Submit(in AudioFrame)`** stamps **100 ns** **`Timecode`** from **`AudioFrame.PresentationTime`** (mux PTS); **`Submit(ReadOnlySpan<float>)`** uses the running sample counter when no PTS is available. **`EnsurePackedCapacity`** uses **`NativeMemory.Realloc`** with **≥2×** headroom (power-of-two) to limit churn when upstream chunk sizes change. **`NDIOutput`:** child sink lifetime tied to parent; dispose order (video then audio then sender) is intentional.

### `S.Media.SDL3` + `S.Media.OpenGL`

- **Threading:** video submit vs GL pump stays “single producer per sink” as documented on `VideoPlayer`.
- **`Nv12DmabufGpuUploader.TryCreate`:** failures on the probe path are logged once via **`MediaDiagnostics.LogError`** (ctor-time only).
- **Windows NV12 (D3D11 shared handle) → GL:** [x] **`Nv12Win32SharedHandleGpuUploader`** — **`OpenSharedResource`** or **same-decode-device** libav **`ID3D11Texture2D`** COM pointer (**`VideoWin32Nv12Backing.LibavD3D11DeviceComPtr`** / **`LibavD3D11Texture2DComPtr`** when uploader device matches); prefers **`WGL_NV_DX_interop`** (register D3D texture → GL **`R8`**, FBO blit into **`YuvVideoRenderer`** plane textures), falls back to D3D11 staging **`Map`** + **`glTexSubImage2D`**; one-time **`MediaDiagnostics.LogInformation`** for which path won; **`ComObject`**-wrapped device, **`TryCreate`** validation, **`Dispose`** releases COM; optional **`Nv12Win32SharedHandleGpuUploadProfiling`** when **`MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD=1`**. [x] **`D3D11InteropUtility`** — **`TryValidateDeviceComPointer`**, **`TryGetAdapterLuid`**, **`TryValidateTexture2DComPointer`**. [x] **`SDL3GLVideoSink`** on Windows + NV12: when **`createFallbackD3D11InteropDeviceForWin32Nv12`** is true (default), uses **`D3D11GlInteropDeviceHost`** (feature levels **11.1→10.0**, **`VideoSupport`**) if neither ctor **`borrowD3D11DeviceComPtrForNv12Gl`** nor **`IHardwareD3D11GlInteropSource`** supplies a device, and passes **`NativePointer`** into **`YuvVideoRenderer`** (**`win32D3D11DeviceComPtrForNv12`**); when the flag is false (**true zero-host**), **`YuvVideoRenderer`** may lazy-bind the uploader from **`LibavD3D11DeviceComPtr`** on the first decoded frame; one-time adapter LUID log on owned path. **`VideoPlaybackSmoke`** **`--d3d11-gl`** sets **`RetainD3D11SharedHandleForGl`** only; **`--d3d11-gl-zero-host`** disables the SDL-owned fallback device (NDI mutual-exclusion warning mirrors **`--drm-gl`**); negotiator still borrows the decoder device when connected. Lab: **`RUN_WIN32_NV12_D3D11_INTEROP_STRESS=1`** (**`Win32Nv12D3d11InteropStressTests`**, Windows).
- **Dispose:** GL context / renderer disposal keeps empty `catch` — see Implementation verification (by design).

### `PALib` / `JackLib` / `NDILib`

- **`PALib`:** large `LibraryImport` surface — correctness depends on staying aligned with upstream PortAudio ABI; no issues spotted in this pass beyond normal binding risk.
- **`JackLib` / `JackClient`:** delegates rooted via instance fields **and** `GCHandle.Alloc`; `Dispose` frees handles after `jack_client_close` — good pattern for preventing delegate GC holes.
- **`NDILib`:** `Utf8Buffer` uses `Marshal.StringToCoTaskMemUTF8` / `FreeCoTaskMem` — callers must keep `using` discipline (wrappers already do).

### `PMLib` (`PortMidi`)

- **`MIDIInputDevice.Close`:** cooperative join strategy documented elsewhere; no new regressions found.
- **`PMLibModuleInit`:** `[ModuleInitializer]` + `CA2255` suppression is documented in-source for the custom `DllImportResolver` story.

### `OSCLib`

- [x] **`OSCServer.HandleOversizePacket`** — was incrementing `_oversizeDrops` with plain `++` while **`OversizeDropCount`** reads via **`Interlocked.Read`** — fixed to **`Interlocked.Increment`** so concurrent observers on 32-bit hosts never see a torn 64-bit write.
- **`Dispose` / `DisposeAsync`:** synchronous **`Dispose`** cooperative join uses **`#if DEBUG`** **`ILogger.LogDebug`** on catch (same pattern as the media stack); **`DisposeAsync`** logs **`StopAsync`** failures the same way.

### Tools (`PlaybackSmoke`, `VideoPlaybackSmoke`)

- [x] **`PlaybackSmoke` drain-phase HUD** — after the main loop, the drain loop tested `status.ElapsedMilliseconds` without resetting **`Stopwatch`** state, so status line timing was wrong for the drain phase; **`status.Restart()`** added immediately after starting the drain timer.

### Solution hygiene

- [x] **`MFPlayer.sln` duplicate `VideoPlaybackSmoke` project** — duplicate **`Project(...)`** / **`ProjectConfigurationPlatforms`** block removed (single **`{CD1269E9-…}`** entry remains). [x] **`Global`** section header + **`NestedProjects`** — removed stray duplicate **`Media`** / **`Tools`** / **`Test`** folder projects and reparented child projects (Visual Studio / MSBuild–friendly layout).

---

## Executive summary — where to look

| Area | Implementation |
|------|----------------|
| Extended pixel formats | `PixelFormat.cs`, `PixelFormatInfo.cs`, `VideoFileDecoder` `MapNativePixelFormat` / `ToAVPixelFormat` |
| GL recipes + shaders | `GlVideoFormatSupport.cs`, `YuvVideoRenderer.cs`, `Shaders/argb.frag.glsl`, `abgr.frag.glsl`, `gray.frag.glsl`, `yuva_planar.frag.glsl` |
| Frame transfer metadata | `VideoTransferHint.cs`, `VideoFrame.ColorTransferHint`, libav `AVFrame.color_trc` → `VideoFileDecoder.MapTransferHint` |
| FFmpeg hardware decode (CPU transfer) | `VideoDecoderOpenOptions` (**`TryHardwareAcceleration`** default **true**, **`DecoderThreadCount`**, **`RetainDmabufForGl`**), `VideoHardwareDecodeContext`, `av_hwframe_transfer_data` |
| Linux NV12 DRM PRIME → GL | `VideoDecoderOpenOptions.RetainDmabufForGl`, `VideoFrame.DmabufNv12` / `CreateNv12Dmabuf`, `DrmPrimeNv12BackingFactory`, `YuvDmabufEglInterop`, `Nv12DmabufGpuUploader` (incl. **`EGL_EXT_image_dma_buf_import_modifiers`**), `SDL3GLVideoSink` + `YuvVideoRenderer` |
| Windows NV12 D3D11 shared handle → GL | `VideoDecoderOpenOptions.RetainD3D11SharedHandleForGl`, `VideoWin32Nv12Backing` (**`LibavD3D11DeviceComPtr`** / **`LibavD3D11Texture2DComPtr`**), `YuvVideoRenderer` + **`Nv12Win32SharedHandleGpuUploader`** (**`WGL_NV_DX_interop`** + staging fallback + same-device texture path), **`Nv12Win32SharedHandleGpuUploadProfiling`** (**`MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD=1`**), **`D3D11InteropUtility`**, **`VideoFormatNegotiator`** + **`IVideoSinkD3D11GlBorrowSetup`** / **`IHardwareD3D11GlInteropSource`**, optional owned **`D3D11GlInteropDeviceHost`**, lab **`RUN_WIN32_NV12_D3D11_INTEROP_STRESS`**, **`VideoPlaybackSmoke --d3d11-gl`** |
| SDL GL HDR | `SDL3GLVideoSink.ApplyTransferHintToRenderer` + optional `GlVideoSinkHdrPreference` / `HdrPreference` property |
| Router SIMD (stereo+) | Stereo identity/swap, **stereo `[0,0]` / `[1,1]`** dup-L / dup-R (`TryAccumulateStereoDupSingleChannelInterleaved`), **stereo `[-1,-1]`** full-silence no-op (`TryAccumulateStereoFullSilenceStereoInterleaved`), **mono `[0,0]` → duplex** (dedicated AVX path), **mono `MonoToN(N≥3)`** (stack or **`ArrayPool`** scratch ≤ **262144** floats), **mono silence / source-0 only** (`TryAccumulateMonoSilenceOrZeroDupInterleaved`, e.g. **`[-1,0,0,-1]`**), **stereo silence / L–R only** (`TryAccumulateStereoSilenceOrZeroDupInterleaved`, e.g. **`[-1,0,1,0]`**), **`StereoToN` / `[0,1,0,1,…]`** (pooled scratch for large **N**), **`[0,1,0,1]`** quad AVX path, **packed same-width identity `Identity(N≥3)`** (`TryAccumulatePackedIdentityInterleaved`), `AudioRouter.ApplyRoute` |
| libav resampling | `AudioResampler` (`swresample`) for packed float audio; optional **`AdaptiveRateAudioSink`** (per-sink ppm correction from **`PumpPressure`**) |
| Thread join slicing | **`CooperativePlaybackJoin`** (`S.Media.Core.Threading`) — **`MediaClock`**, **`VideoPlayer`**, **`AudioRouter`**, **`SinkPump`**, **`SDL3*VideoSink`**, **`NDIAudioReceiver`**; short-slice **`MIDIInputDevice.Close`**, **`OSCServer.Dispose`** |
| Clock / player join with cancel | **`IMediaClock.Pause` / `Stop`**, **`VideoPlayer.StopInternal`** observe **`CancellationToken`** while draining decode / driver threads (via **`CooperativePlaybackJoin`**)
| Router stop + cancel | `AudioRouter.Stop(CancellationToken)` cooperatively joins the mixer thread between short sleeps |
| Zero-copy video interop | `IHardwareVideoInterop`, **`HardwareVideo*Descriptor`** (**`D3D11DeviceComPtr`** + **`Win32D3D11Nv12Texture`** for libav D3D11 COM), **`NoOpHardwareVideoInterop`**, **`LinuxDmabufNv12Interop`**, **`WindowsNv12SharedHandleInterop`**, **`WindowsNv12D3D11TextureInterop`**, **`VulkanExternalNv12Interop`**, **`MetalIosurfaceNv12Interop`** |
| NDI optional wall-clock pacing | `NDIOutput` ctor `minimumVideoSubmitSpacing`, `NDIVideoSender.PaceBeforePack` |
| NDI egress mux playhead (optional) | **`NdiEgressMuxPlayheadClock`** (`S.Media.NDI.Clock`) — **`IPlaybackClock`** from max mux PTS before send; pair with **`NdiEgressPresentationTimeline`**-relative NDI timecodes |
| A/V coordinated playback | **`AvPlaybackCoordinator`** (`S.Media.Core.Playback`), **`VideoPtsClock`** (`IPlaybackClock` for PTS + wall), **`VideoPlayer.FramePresentationTimePresented`** |
| File A/V smoke (`VideoPlaybackSmoke`) | **`MediaContainerDecoder.Open`** (single demux); **`MediaContainerPlaybackHost`** (**`Router.AddSource(container.Audio)`**, borrowed — container owns disposal); **`AvRouter`** + **`MediaPlaybackSession`** for **`Play`**/**`Pause`** (**`AvRouter.Pause`** defaults **`FlushCodecPipelines`** on shutdown); **`[video]`** negotiated + per-output pixel path log (**`VideoRouter.TryGetInputFanOutPixelFormats`**, **`YuvVideoRenderer.SupportedPixelFormats`** hint) |
| Dynamic audio graph | **`AudioRouter`** — `AddSource` / `RemoveSource`, `AddSink` / `RemoveSink`, `AddRoute` / `RemoveRoute` while running; per-sink **`SinkPump`** |
| Video multi-output (today) | **`VideoOutputRouter`** (primary + branch; branch pixel pick prefers **RGBA32 → BGRA32 → …** then subsampled YUV); **`VideoRouter`** + optional **`VideoSinkPump`** (**`VideoSinkPumpMetrics`**, **`TryGetVideoSinkPumpMetrics`**) for async branches (e.g. NDI); **`VideoSinkFanoutFormats`** prefers **negotiated** pixel format when the branch accepts it (keeps dma-buf fan-out on **NV12**); **`VideoRouter.TryGetInputFanOutPixelFormats`** for tooling; legacy **`TwinCpuVideoSink`** compositor; app-level fork |
| Architecture backlog (routers + clocks) | §**Architecture roadmap — A/V router unification & clocks** below |
| Concept / format matrix docs | `Doc/MediaFramework-concepts.md`, `Doc/PixelFormats-OpenGL.md` |

---

## 1. OpenGL renderer (`S.Media.OpenGL`)

### 1.1–1.4

- [x] Prior correctness / architecture items unchanged (unpack restore, viewport fit, HDR uniforms, samplers, native pointer upload, etc.).
- [x] **Extended format coverage** — `Argb32`, `Abgr32` (FFmpeg memory order + swizzle shaders), `Gray8`/`Gray16` (`gray.frag.glsl`), `Yuv420P10Le` / `Yuv420P12Le` / `Yuv444P10Le` (planar R16 + `yuv_planar.frag.glsl` bitScale), `Yuva420p` (`yuva_planar.frag.glsl` + fourth alpha plane).
- [x] **Hardware GL decode (Linux NV12)** — EGL `EGL_EXT_image_dma_buf_import` (+ **`EGL_EXT_image_dma_buf_import_modifiers`** when non-zero **`DRM_FORMAT_MOD_*`**) + GL `GL_EXT_EGL_image_storage`, split Y (`DRM_FORMAT_R8`) / UV (`DRM_FORMAT_GR88`). **non-NV12 PRIME** / multi-planar FFmpeg layouts remain backlog — **§Unified backlog**; gate helper **`LinuxDmabufGlHardwareFormats.IsSupportedForPrimeGlImport`** (**NV12** only).
- [x] **Hardware GL decode (Windows NV12, D3D11)** — **`Nv12Win32SharedHandleGpuUploader`** + **`SDL3GLVideoSink`** D3D11 device wiring + **`YuvVideoRenderer`**; **`WGL_NV_DX_interop`** when available; **`VideoFormatNegotiator`** can borrow the decode **`ID3D11Device`** for GL (**`IVideoSinkD3D11GlBorrowSetup`** / **`IHardwareD3D11GlInteropSource`**); same-device **`VideoWin32Nv12Backing`** COM pointers skip **`OpenSharedResource`**. [x] **True zero-host** — no **`D3D11GlInteropDeviceHost`** when **`createFallbackD3D11InteropDeviceForWin32Nv12`** is false; **`YuvVideoRenderer.allowLazyWin32Nv12UploaderFromDecodedFrame`** binds the uploader from **`LibavD3D11DeviceComPtr`** on first decoded Win32 NV12 frame (**`VideoPlaybackSmoke`**: **`--d3d11-gl-zero-host`** with **`--d3d11-gl`**). [ ] “libav alone opens DXGI for GL with **no** COM **`ID3D11Device`** surface on frames” remains non-goal / deferred — **§Tier F** row **34**.

---

## 2. Core video (`S.Media.Core`)

- [x] **Argb32 / Abgr32** — enum + `PixelFormatInfo` plane metadata + alpha flag.
- [x] **Gray8 / Gray16**, **420 10/12**, **444 10**, **YUVA420P** — descriptors in `PixelFormatInfo`; tests in `PixelFormatInfoTests`.
- [x] **NV12 dma-buf metadata** — `VideoDmabufNv12Backing`, `VideoFrame.DmabufNv12` / `CreateNv12Dmabuf` (CPU `Planes` are empty stubs on that path).
- [x] **Hardware video interop surface contract** (`IHardwareVideoInterop`, `HardwareVideoSurfaceDescriptor`, `NoOpHardwareVideoInterop`) — [x] **Linux** **`LinuxDmabufNv12Interop`** + **`DmabufNv12InteropToken`**; [x] **Windows** **`WindowsNv12SharedHandleInterop`** + **`Win32Nv12InteropToken`** (NT shared handles — descriptor only); [x] **Windows** **`WindowsNv12D3D11TextureInterop`** + **`Win32D3D11Nv12Texture`** (libav D3D11 NV12 COM descriptor); [x] **Vulkan** **`VulkanExternalNv12Interop`** + **`VulkanExternalNv12InteropToken`** (external-memory handle + **`VkExternalMemoryHandleTypeFlagBits`** via **`HardwareVideoPlaneDescriptor.ExternalMemoryHandleType`**; optional UV byte offset in plane1 **`Modifier`** for unified allocations); [x] **Apple** **`MetalIosurfaceNv12Interop`** + **`MetalIosurfaceNv12InteropToken`** (**`IOSurfaceRef`** in **`HandleOrDescriptor`**). [x] **Windows NV12 → GL upload path** (host or borrowed D3D11 device + **`WGL_NV_DX_interop`** / staging — see §1). [x] **`HardwareVideoWin32Nv12`** + **`YuvVideoRenderer.Upload(HardwareVideoSurfaceDescriptor)`** (no **`VideoFrame`**). [x] **True zero-host** — lazy uploader from frame backing when SDL fallback interop device is disabled (see §1).

---

## 3–5. FFmpeg / NDI / SDL3

- [x] **FFmpeg mappings** for new `PixelFormat` values where libav exposes a stable `AV_PIX_FMT_*`.
- [x] **`color_trc` → `VideoTransferHint`** per frame on pass-through and sws output paths.
- [x] **`SDL3GLVideoSink`** applies hint to `YuvVideoRenderer.HdrTransfer`; optional `GlVideoSinkHdrPreference` overrides or ignores per-frame hints.
- [x] **NDI pacing** — optional minimum spacing between submits (pairs with SDK `clockVideo:false` scenarios).
- [x] **Hardware FFmpeg decode** — VAAPI / D3D11VA / QSV / … via libav; **Linux VAAPI** zero-copy path uses `RetainDmabufForGl` → `drm_prime` + `VideoDmabufNv12Backing` → GL; **Windows NV12** shared-handle path uses **`RetainD3D11SharedHandleForGl`** + **`Nv12Win32SharedHandleGpuUploader`** (see §1). **`TryHardwareAcceleration`** defaults **on**; use **`TryHardwareAcceleration = false`** for deterministic software-only tests. **`IHardwareVideoInterop`**: **`LinuxDmabufNv12Interop`**, **`WindowsNv12SharedHandleInterop`**, **`WindowsNv12D3D11TextureInterop`**, **`VulkanExternalNv12Interop`**, **`MetalIosurfaceNv12Interop`** (descriptor tokens). [x] **True zero-host GL** — **`SDL3GLVideoSink`** can skip **`D3D11GlInteropDeviceHost`**; uploader uses libav’s **`ID3D11Device`** from backing when frames expose **`LibavD3D11DeviceComPtr`**. [ ] Core-only decode→GL **without** any D3D11 COM device on descriptors/frames (still deferred) — **§Tier F** row **35**.
- [x] **Software video multithreading** — `VideoFileDecoder` sets libav **frame** or **slice** `thread_count` / `thread_type` when opening **without** an attached HW device context (skipped on DRM PRIME / other HW paths); `DecoderThreadCount` **0** = auto from **`Environment.ProcessorCount`** (clamped).
- [x] **NDI RGBA** — `NDIVideoSender` accepts **RGBA32** (FourCC `RGBA`) and **BGRA32**; **`VideoOutputRouter`** branch conversion prefers packed RGB before NV12/I420 for quality on 10-bit YUV sources.
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
- [x] **`ChannelMap`** — SIMD regressions (**`StereoSimd_*`**, **`MonoDupSimd_*`**, **`MonoDupNSimd_*`** incl. large **N** pool path, **`MonoSilenceOrZeroSimd_*`**, **`StereoSilenceOrZeroSimd_*`**, **`StereoDupSingleChannelSimd_*`**, **`StereoFullSilenceStereo_ApplyAdditive_LeavesDstUnchanged`**, **`StereoToNSimd_*`**, **`StereoDuplexWideSimd_*`**) plus seeded **`Apply_RandomLayouts_Deterministic_MatchNaive`**, **`ApplyAndApplyAdditive_MultiChannelRandomLayouts_MatchNaive`**, **`ApplyAdditive_RandomLayouts_Deterministic_MatchNaive`**; **`AudioRouterApplyRouteDeterminismTests`**; **`AudioRouterLiveGraphSeededTests`** (live **`AudioRouter`** run loop vs scalar reference); **`AudioRouterTests.RunLoop_MultiSourceStereoDupAndFullSilence_MatchesReference`**; **`AudioResampler`** (identity/up + **`Resampler_RandomishBuffer`**); **`AdaptiveRateAudioSinkTests`**; **`HardwareVideoInteropTests`**; **`MediaContainerDecoderSoakTests`** (**`RUN_MEDIA_SOAK=1`** → **64** seek rounds). Further long-run property tests remain optional — **§Unified backlog**.
- [x] **`#if DEBUG` teardown diagnostics** — **`SinkPump.CompleteAdding`** / **`CTS.Cancel`** and **`VideoPlayer.OnVideoTick`** (**`Submit`** + frame **`Dispose`** on failure) use **`MediaDiagnostics`** only in debug builds; **`OSCServer.Dispose` / `DisposeAsync`** use **`ILogger.LogDebug`** the same way.
- [x] **`SinkSlavedRouterClock`** — **`SinkSlavedRouterClockTests`** (lazy wall fallback, **`IClockedSink`** delegation, sink removal/reappearance, cancellation, ctor validation).

### 10.4 Docs

- [x] **Concept overview** — `Doc/MediaFramework-concepts.md`.
- [x] **Pixel format × GL mapping** — `Doc/PixelFormats-OpenGL.md`.

---

## Historical suggested backlog (archive)

> **Current priorities:** read **§Unified backlog (prioritized by impact)** at the **end** of this document. The numbered items below are the **original #1–#12 backlog** with full inline **`[x]`** / **`[~]`** / **`[ ]]`** status preserved.

1. **`IHardwareVideoInterop` — platform import adapters** — [x] **Linux** **`LinuxDmabufNv12Interop`** (DRM PRIME fds via **`DmabufNv12InteropToken`**); [x] **Windows** **`WindowsNv12SharedHandleInterop`** (NV12 plane **NT shared handles** via **`Win32Nv12InteropToken`** — no DXGI device open in Core); [x] **Windows** **`WindowsNv12D3D11TextureInterop`** (libav **`ID3D11Texture2D`** + **`ID3D11Device`** COM — **`Win32D3D11Nv12Texture`** descriptor kind); [x] **Vulkan** **`VulkanExternalNv12Interop`** (NV12 external-memory handles + **`VkExternalMemoryHandleTypeFlagBits`** in **`HardwareVideoPlaneDescriptor.ExternalMemoryHandleType`**); [x] **Apple Metal / IOSurface** **`MetalIosurfaceNv12Interop`**. [x] **GL path for Windows NV12** — **`Nv12Win32SharedHandleGpuUploader`** + **`WGL_NV_DX_interop`** (host **`ID3D11Device`** from **`SDL3GLVideoSink`**), same-device libav texture COM when **`VideoWin32Nv12Backing`** matches. [x] **Descriptor → GL** — **`HardwareVideoWin32Nv12.TryCreateWin32Nv12Backing`** + **`YuvVideoRenderer.Upload(HardwareVideoSurfaceDescriptor)`**. [x] **Keyed-mutex** — **`D3d11TextureKeyedMutexScope.TryAcquireForGpuRead`** + strict failure when **`IDXGIKeyedMutex`** exists but acquire fails; **`VideoWin32Nv12Backing`** / uploader docs (libav releases key **0** before frame hand-off). [~] Multi-hour interop / exotic **multi-GPU** soak remains lab backlog (**§Unified backlog** Tier A item **2**).
2. SIMD for remaining hot **`ChannelMap` shapes** — [x] **`MonoToN` with `N ≥ 3`** (stack scratch ≤ **512** floats else **`ArrayPool`** up to **262144** floats); [x] **`StereoToN` / `[0,1,0,1,…]`** (same pool rule); [x] **`StereoToNSwapped` / `[1,0,1,0,…]`** + **`TryAccumulateStereoDuplexWideSwappedInterleaved`** + **`TryAccumulateStereoToNInterleavedSwapped`**; [x] **Mono maps mixing silence (`-1`) and source `0` only** (`TryAccumulateMonoSilenceOrZeroDupInterleaved`); [x] **Stereo maps mixing silence (`-1`) and L/R (`0`/`1`) only** (`TryAccumulateStereoSilenceOrZeroDupInterleaved`); [x] **Stereo dup one channel to both** **`[0,0]`** / **`[1,1]`** (`TryAccumulateStereoDupSingleChannelInterleaved`); [x] **Stereo full silence** **`[-1,-1]`** on stereo dst (`TryAccumulateStereoFullSilenceStereoInterleaved`); [x] **Packed multi-channel identity** **`ChannelMap.Identity(N≥3)`** (`map[i]==i`, same-width interleaved) — **`TryAccumulatePackedIdentityInterleaved`**. [x] **Packed bijective permutations** **`N=4`/`N=8`** — **`TryAccumulatePackedPermutationInterleaved`**. [ ] Further SIMD for **non-permutation** asymmetric maps remains **profile-driven** — **`ApplyAndApplyAdditive_MultiChannelRandomLayouts_MatchNaive`** already guards **≥3**-channel scalar correctness; multi-sink drift has **`AdaptiveRateAudioSink`**.
3. Dedicated fuzz / property-test harness — [x] **`ApplyAdditive_RandomLayouts_Deterministic_MatchNaive`** (seeded random maps + non-zero dst priming) extends **`Apply_RandomLayouts`**-style coverage for **`ApplyAdditive`**. [x] **`AudioRouter.RunLoop_MultiSourceStereoLayoutsAndGains_MatchesReference`** (+ **`RunLoop_MultiSourceStereoDupAndFullSilence_MatchesReference`**). [x] **`AudioRouterApplyRouteDeterminismTests.ApplyRoute_SeededRandomMultiRouteGraphs_MatchScalarReference`**. [x] **`AudioRouterLiveGraphSeededTests.RunLoop_SeededRandomDistinctSourceRoutes_FirstChunkMatchesScalarReference`** — live **`AudioRouter`** (**`SinkPump`**), **1–4** stereo sources, **1–4** routes with distinct sources, seeded maps/gains (**`[-1,-1]`** in map pool), first chunk vs scalar reference. [x] **`MediaContainerDecoderSoakTests.SharedDemux_Soak_*`** (bounded soak; optional **`RUN_MEDIA_SOAK=1`** for **64** seek rounds vs default **8**). [x] **`RUN_NDI_EGRESS_SOAK=1`** — optional **`NdiEgressPresentationTimelineTests.Soak_sequential_reset_and_timecode_rounds`** extends sequential rounds (**120k** vs **4k** default); optional **`RUN_NDI_EGRESS_SOAK_STRESS=1`** (**1M** rounds, lab-only). [ ] multi-hour / memory-pressure / full-wire **NDI** harness remains product-level backlog.
4. **`MediaContainerDecoder`** (single demux → split packet streams) — prerequisite for tight long-seek A/V and for any “one router brain” that owns both decoders; see §Architecture roadmap. **`VideoPlaybackSmoke`** now uses it end-to-end (was dual **`VideoFileDecoder`**/**`AudioFileDecoder`**).
5. **`VideoRouter`** — [x] exclusive routing + fan-out; [x] **`VideoSinkPump`** per async output (bounded queue, drop-oldest, drainer thread **`AboveNormal`**); [x] **`VideoSinkPumpAttachOptions`** on **`AddOutput`**; [x] **`VideoSinkPumpMetrics`** + **`TryGetVideoSinkPumpMetrics(..., out VideoSinkPumpMetrics)`** (drops, submitted, **MaxQueueDepth**, **CurrentQueuedDepth**) + throttled **`MediaDiagnostics`** warnings on sustained drops (mirrors audio **`SinkPumpStats`** / PortAudio ring guidance); [x] optional **`PumpPressure`** / **`VideoRouterPumpPressureEventArgs`** (and **`VideoSinkPump.PumpPressure`**) for per-drop callbacks without polling metrics. **`VideoOutputRouter`** negotiates per-sink pixel formats (CPU **`sws_scale`**) so a narrow sink (e.g. NDI) does not receive unsupported layouts. **Update:** same-**NV12** DRM dma-buf fan-out via refcounted backing; mixed dma-buf + per-branch **`VideoCpuFrameConverter`** remains unsupported.
6. **`CompositePlaybackClock`** / **`MediaClockExtensions.SetMasterChain`** — [x] **`CompositePlaybackClock`** + extension on **`IMediaClock`** builds a priority merge (**`PortAudioOutput`**, **`VideoPtsClock`**, **`NdiIngestPlaybackClock`**, …). [x] **Documented behaviour** when several clocks advance: highest priority wins instantly for **`ElapsedSinceStart`** (no temporal cross-fade). [ ] **Temporal cross-fade** — **§Tier F** row **21**.
7. **`NDIAudioReceiver` → `IPlaybackClock`** — [x] **`NdiIngestPlaybackClock`** (`S.Media.NDI.Clock`): receiver optional ctor param + **`AttachReceiver`** / **`NotifyCaptureStopped`**; map SDK timecode / timestamp (100 ns) + frame duration, wall extrapolation between frames (**`MediaClock.SetMaster`** for NDI ingest sync). [x] **NDI egress mux playhead** — **`NdiEgressMuxPlayheadClock`** (**`IPlaybackClock`**, **`NotifyVideoPresentation`** / **`NotifyAudioPresentation`**, **`Reset`** after seek) for hosts that want one mux-PTS envelope before send.
8. **`AudioPlayer` primary hand-off** — [x] **`RemoveOutput`** auto-promotes the next **`IClockedSink`** ( **`AudioRouter.RetargetSlaveClock`** ).
9. **`Nv12DmabufGpuUploader.TryCreate` diagnostics** — [x] outer `try/catch` logs via **`MediaDiagnostics.LogError`** on failure (ctor-time only).
10. **Optional debug logging on Dispose `catch { }` paths** — [x] **`SinkPump.Dispose`** (**`AudioRouter`**) logs **`Cancel`** failures behind **`#if DEBUG`** (**`MediaDiagnostics`**); **`SinkPump.CompleteAdding`** **`ObjectDisposedException`** behind **`#if DEBUG`**; **`AudioRouter.Dispose`** logs **`SinkPump.Dispose`** failures behind **`#if DEBUG`** (**`MediaDiagnostics`**); **`VideoPlayer.Dispose`** / **`VideoPlayer.OnVideoTick`** (**`Submit`** failure + frame **`Dispose`** after failed **`Submit`**) and **`AudioPlayer.Dispose`** (**`Stop`** / owned disposables) log behind **`#if DEBUG`** (**`MediaDiagnostics`**); **`OSCServer.Dispose` / `DisposeAsync`** log cooperative shutdown failures behind **`#if DEBUG`** (**`ILogger`**, no media dependency). Other teardown `catch { }` paths remain silent by design unless you extend the same pattern.
11. **`MFPlayer.sln` cleanup** — [x] duplicate **`VideoPlaybackSmoke`** entry removed; [x] **`Global`** + duplicate solution-folder **`Media`**/**`Tools`**/**`Test`** entries removed (**`NestedProjects`** consolidated).
12. **`AudioRouter` nominal sample rate** — [x] **`SampleRate`** is unchanged while **`IsRunning`** (**XML** + class `<remarks>`); [x] **stopped-only** **`ReconfigureSampleRate`** when every source/sink already matches the new Hz (**`SinkSlavedRouterClock`** / **`WallClockRouterClock`** rebuilt); **`AddSource`** rejects mismatched source rates. [ ] **Nominal rate change while the run loop is active** — **§Tier F** row **22** — use **per-sink** **`AdaptiveRateAudioSink`** for drift at a fixed graph rate, or stop and **`ReconfigureSampleRate`** / new router.

## Architecture roadmap — A/V router unification & clocks (2026-05-12)

Design goal from product direction: **one place** that understands play/pause/seek, **one authoritative timeline** for audio and video, and **hardware / external clocks** (PortAudio, NDI SDK pacing, file PTS) composable rather than bolted on per demo.

### What exists today (accurate mental model)

- **Audio** — A real **router**: **`AudioRouter`** mixes many **`IAudioSource`** → many **`IAudioSink`** with explicit **`Route`** graphs. The graph is **mutable while running** (immutable snapshots under the hood). **Pacing** is **`IRouterClock`** (**`WallClockRouterClock`** or **`SinkSlavedRouterClock`** → one **`IClockedSink.WaitForCapacity`**). **Playhead** for the rest of the stack is **`MediaClock`**, optionally mastered to **`IPlaybackClock`** (**`PortAudioOutput`**, **`VideoPtsClock`**, **`NdiIngestPlaybackClock`** when ingesting NDI audio, …).
- **Video** — **`VideoPlayer`** is still one **`IVideoSource`** → one negotiated **`IVideoSink`**. Multiple displays / encoders use **`VideoRouter`** (many inputs, many outputs; **each output accepts at most one input** — conflicting **`TryAddRoute`** is declined with **`ILogger`** error) or the slimmer **`VideoOutputRouter`** (primary + one branch). **`VideoPlaybackSmoke`** uses **`VideoRouter`** for SDL + NDI.
- **A/V sync** — When audio is present, **`AudioPlayer.AddOutput(PortAudioOutput)`** wires **`MediaClock.SetMaster(IPlaybackClock)`** + **`AudioRouter.SlaveTo`**. Video frames track **`IMediaClock.CurrentPosition`**. That is already **shared-clock** playback; **`AvPlaybackCoordinator`** + **`MediaContainerDecoder`** (single **`AVFormatContext`**) close the façade / container-alignment gap for file sources.

### “Combine audio and video router” — three incremental strategies

| Strategy | What it means | Effort / fit |
|----------|----------------|---------------|
| **A. Session façade only** | New **`MediaPlaybackSession`** (name TBD) holds **`AudioPlayer`**, **`VideoPlayer`**, optional **`VideoPtsClock`**, wires **`AvPlaybackCoordinator`**, exposes add/remove **audio** outputs and swap **video** sink (or compositor) under one lock ordering contract. | Low — mostly composition + docs; no new mixer core. |
| **B. Shared timeline service** | **`IPlaybackTimeline`** (**`S.Media.Core.Clock`**) includes **`CurrentPosition`**, **`IsRunning`**, **`PlaybackRate`** ( **`MediaClock`** is **1.0** until variable-speed exists), and **`Seek`** on **`IMediaClock`**; **`AudioPlayer.Timeline`** mirrors the same playhead; **`PlaybackTimelineClockExtensions.SubscribePositionChanged`** eases **`PositionChanged`** lifetime; further subscription-style wiring for **`AudioRouter`**/**`VideoPlayer`** remains optional. | Medium — incremental from current **`MediaClock`** / **`CompositePlaybackClock`**. |
| **C. Full `AvRouter`** | One graph object owning demux + audio routes + **dynamic video outputs** (see **§Unified backlog** Tier **B** item **5**). **`MediaContainerPlaybackHost`**, **`IAvPlaybackSession`** (**`MediaPlaybackSession`**), and **`S.Media.FFmpeg.AvRouter`** are the current stepping stones. | High — depends on **`MediaContainerDecoder`** for sane seek and PTS. |

Recommendation in **Todo** terms: ship **A** + **`MediaContainerDecoder`** (already listed) before **C**; use **B** if **`CompositePlaybackClock`** becomes necessary (e.g. NDI ingest + local preview).

### Clocks: PortAudio, PTS, NDI — how they could share

- **`IRouterClock`** (audio producer cadence) and **`IPlaybackClock`** (**`MediaClock`** master) are **different roles**. A future **NDI wall clock** might drive **`IRouterClock`** when playing **out** to NDI with **`clockAudio:true`**, or **`IPlaybackClock`** when **receiving** NDI and slaving OpenGL to network audio.
- **`VideoPtsClock`** — wire on **audio-less** paths (**`VideoPlaybackSmoke`** without routing): **`FramePresentationTimePresented`** + **`BeginSession`**; extend **`AvPlaybackCoordinator.Play`** with optional **`IPlaybackClock? videoOnlyMaster`** (item already in evening audit).
- **Multi-sink audio drift** — **`AudioRouter`** remarks document **ppm drift** when only one sink is **`IClockedSink`**. Optional **per-sink adaptive resampling**: **`S.Media.FFmpeg.Audio.AdaptiveRateAudioSink`** + **`PumpPressurePlaybackHintMonitor`** (sink-id filter) ease sustained **`PumpPressure`** drops without retuning the master clock; **`PumpPressurePlaybackHintMonitor`** still supports host **`IPlaybackClock`** bias.

### Dynamic inputs/outputs — matrix

| Capability | Audio | Video |
|------------|-------|-------|
| Multiple inputs | Yes — **`AddSource`** | Yes — **`VideoRouter.AddInput`** (each returns an **`IVideoSink`**) |
| Multiple outputs | Yes — **`AddSink`** + routes | Yes — **`VideoRouter.AddOutput`** + **`TryAddRoute`** fan-out |
| Remove while playing | **`RemoveSink`** / **`RemoveSource`** / **`RemoveRoute`** | **`RemoveOutput`** / **`RemoveInput`** / **`TryRemoveRoute`** (re-**`Configure`** when the graph changes) |
| Re-pick pacing master after remove | [x] **`AudioPlayer.RemoveOutput`** auto-promotes the next **`IClockedSink`** when **`AutoWirePrimary`** is on; **`AudioRouter.RetargetSlaveClock`** | N/A |

### Tracked items (checkboxes)

- [x] **`MediaPlaybackSession`** — thin façade: **`VideoPlayer`** + **`IMediaClock`** + optional **`AudioPlayer`**; **`Play`**/**`Pause`**/**`Seek`** delegate to **`AvPlaybackCoordinator`**; implements **`IAvPlaybackSession`** (**`Timeline`** → **`IPlaybackTimeline`**); **`VideoPlaybackSmoke`** composes **`MediaContainerPlaybackHost`**, **`AvRouter`**, and this session for shared-mux flush defaults; `<remarks>` document lock-order guidance (hosts keep owning `using` on players).
- [x] **`CompositePlaybackClock`** — priority-ordered merge of several **`IPlaybackClock`** implementations; **`IsAdvancing`** / **`ElapsedSinceStart`** use the highest-priority advancing candidate. **`MediaClockExtensions.SetMasterChain`** attaches that merge to any **`IMediaClock`** (including **`MediaClock`**).
- [x] **`MediaContainerDecoder`** — **`S.Media.FFmpeg.MediaContainerDecoder`**: always **`MediaContainerSharedDemux`** (one **`AVFormatContext`**, demux thread + bounded A/V packet queues, hardware video with software fallback, **`SeekPresentation`**); legacy dual-open path removed. [x] **`avcodec_send_packet` → `EAGAIN`** retains the packet (**`_vPendingPacket` / `_aPendingPacket`**) for retry instead of dropping it. [x] **`FlushCodecPipelines()`** — re-syncs decoders to the current mux playhead (**`SeekPresentation(max(video,audio) position)`**).
- [x] **`VideoRouter`** — [x] exclusive routing + fan-out; [x] **`VideoSinkPump`**; [x] optional **`VideoSinkPumpAttachOptions`** on **`AddOutput`** (first-class pump wiring); [x] **NV12 DRM dma-buf fan-out** when every branch stays **NV12** (refcounted **`VideoDmabufNv12Backing`** + **`VideoFrame.CreateNv12DmabufSharedReference`**); dma-buf + per-branch **`VideoCpuFrameConverter`** remains unsupported (use CPU decode or one dma-buf sink) — **§Unified backlog**. [x] **`VideoSinkPumpMetrics`** + throttled queue-full diagnostics (**`VideoPlaybackSmoke`** HUD **`ndiVidQ`**).
- [x] **`NDI` clock adapters** — [x] **`NdiAlignedRouterClock`** (**`IRouterClock`** façade over **`WallClockRouterClock`**, documented extension point for SDK cadence); [x] ingest **`IPlaybackClock`** — **`NdiIngestPlaybackClock`** + **`NDIAudioReceiver`** optional ctor wiring (**`AttachReceiver`** / **`NotifyCaptureStopped`**).
- [x] **`AudioPlayer` primary hand-off** — **`RemoveOutput`** removes the primary **`IClockedSink`** → **`Router.RetargetSlaveClock`** picks the next sink (sorted id order) that implements **`IClockedSink`** and re-**`SetMaster`** when it also implements **`IPlaybackClock`**.
- [x] **Per-sink audio rate correction** — optional **`S.Media.FFmpeg.Audio.AdaptiveRateAudioSink`** wraps **`IAudioSink`** / **`IClockedSink`** and applies small libav **`swresample`** rate tweaks from **`PumpPressurePlaybackHintMonitor`** (aggregate or **per-sink id** overload); **`AudioRouter`** `<remarks>` document wiring. Automatic **master-clock** ppm tracking remains a host concern; **occasional drop/repeat** policies beyond resampling remain optional — **§Tier E** row **18** (checklist **[x]** at the documentation boundary; first-party coordination module **§Tier F** row **31**).
- [x] **`AvPlaybackCoordinator`** — [x] **`Pause`/`Stop`** use **`try { video.Pause } finally { audio?.Pause; flush? }`** with optional **`flushSharedMuxAfterPause`** (e.g. **`MediaContainerDecoder.FlushCodecPipelines`**); [x] optional **`IPlaybackClock? videoOnlyMaster`** on **`Play`** (audio-null paths); [x] optional **`verifyPrebufferAfterPrefill`**; [x] **`Seek`** calls **`video.Clock.Seek`** when **`audio`** is null; [x] **`SeekCoordinated`** forwards the flush hook to **`Pause`** then **`Seek`** (no auto-**`Play`**); [x] **`<remarks>`** on **`Seek`/`SeekCoordinated`** document pause-bracketed seek with **`MediaContainerDecoder.SeekPresentation`** + **`MediaClock.Seek`** when using the shared-demux façade.
---

## Audit findings (2026-05-12)

Original morning entries reproduced below with the working-tree status applied: every "Likely bug" and "Robustness / contract gap" is now `[x]` (resolved in the working tree), including **`AudioRouter.Pause`** / **`Flush`** snapshotting. The optimization and cleanup lists are mostly resolved; the unresolved ones are kept as `[ ]` so they remain on the radar.

### Likely bugs

- [x] **P010 `bitScale` over-amplifies** — fixed in `GlVideoFormatSupport.cs`: `P010` recipe now uses `bitScale = 1f` with a comment explaining the 10-bit-in-high-bits storage layout.

- [x] **`Nv12DmabufGpuUploader.TryUpload` ignores GL errors after `glEGLImageTargetTexStorageEXT`** — fixed: `_gl.GetError()` is checked after each plane upload; failure short-circuits the second `glEGLImageTargetTexStorageEXT` and propagates `false` to the caller. The two `EGLImage` handles are destroyed inline regardless of success (no leak on the failure path). **Implementation file:** `S.Media.OpenGL/EglDmabufNv12Uploader.cs` (class `Nv12DmabufGpuUploader`).

- [x] **EGLImages are leaked between frames** — fixed: `_yImage` / `_uvImage` fields are gone. The two images are destroyed immediately after the `glEGLImageTargetTexStorageEXT` calls in the same `TryUpload`.

- [x] **Dead inner guard in `Nv12DmabufGpuUploader.AppendPlaneAttribs`** — removed (the modifier-availability check now lives only in `TryUpload`).

### Robustness / contract gaps

- [x] **`AvDrmFrameDescriptorInterop` size hard-coded to 528** — fixed: `WarnIfInteropSizeMismatchLp64LoggedOnce` runs once per process from `DrmPrimeNv12BackingFactory` and logs via `MediaDiagnostics.LogWarning` if `Marshal.SizeOf<AvDrmFrameDescriptorInterop>()` disagrees with `ExpectedSizeBytes`.

- [x] **`DrmPrimeNv12BackingFactory` modifier preference is surprising** — fixed: `VideoDmabufNv12Backing` now carries `YPlaneDrmFormatModifier` + `UvPlaneDrmFormatModifier` independently (with a `UsesDistinctDmaBufObjects` convenience). `AppendPlaneAttribs` is invoked per-plane, so `DRM_FORMAT_MOD_LINEAR == 0` no longer collides with "missing modifier."

- [x] **`VideoFileDecoder.NativePixelFormats` is empty when HW decode runs without DRM PRIME** — fixed: the hw-software fallback now advertises `PixelFormat.Nv12` (matching VAAPI/D3D11VA layouts after `av_hwframe_transfer_data`) plus a docstring note. Sinks that accept NV12 can now negotiate around the BGRA32 sws path.

- [x] **`AudioRouter.Pause` re-enters the lock to enumerate sinks for `Flush`** — **`StopInternal`** snapshots **`IFlushableSink`** candidates in the **same** **`_gate`** pass that stops the run loop, then **`Flush`**es after pumps are abandoned (no second **`lock (_gate)`** for enumeration).

### Optimization candidates

- [x] **`VideoFileDecoder.BuildConvertedFrame` allocates a fresh `byte[]` per frame** — fixed: rents from `ArrayPool<byte>.Shared`, releases via the existing `VideoFrame.release` callback. Source/dest scratch arrays are now per-decoder fields (`_swScaleSrcLines` / `_swScaleSrcStride` / `_swScaleDstLines` / `_swScaleDstStride`) — single-threaded use is annotated in a remark.

- [x] **`VideoFileDecoder.BuildPassThroughFrame` … per-frame arrays** — addressed: pooled `ReadOnlyMemory<byte>[]` / `int[]` (keyed by `planeCount`, cap 32 each), returned from the AVFrame-backed frame’s `release` callback (dispose may run off the decode thread).

- [x] **`ChannelMap.StereoSwapAdjacentChannels` … `WithElement` chain** — addressed: stack buffer + pairwise swap + `MemoryMarshal.Cast` reconstructs the vector.

- [x] **`ChannelMap.TryAccumulateMonoDupStereoInterleaved` and `TryAccumulateStereoDuplexWideInterleaved` fan out …** — addressed with SSE/AVX fast paths (`Sse`/`Avx` unpack and `0x44`/`0xEE` shuffles) plus generic `Vector<float>` scratch fallback when lane widths differ.

- [x] **`NDIVideoSender.PaceBeforePack` wall pacing** — coarse `Thread.Sleep` (minus 1 ms) plus `Thread.Sleep(TimeSpan)` for the sub-millisecond remainder (no busy `SpinWait` on the async pump thread).

### Simplifications / cleanup

- [x] **`VideoHardwareDecodeContext.IsActive` is unreferenced** — deleted.
- [x] **`AudioRouter.CooperativeJoin` and `VideoPlayer.TryJoinDecodeThread` are 1-line proxies** — inlined. The helper indirection is gone.
- [x] **`YuvVideoRenderer.DispatchUploadFromFrame` 17-arm switch** — addressed: ctor-bound `Action<VideoFrame>` from a `switch` expression (`CreateUploadFromFrameDelegate`); per-frame dispatch is a single delegate invoke.
- [x] **`MIDIInputDevice.Close` and `OSCServer.Dispose` reimplement `CooperativePlaybackJoin`** — `<remarks>` blocks now document the intentional duplication. ("PMLib/OSCLib stay free of any S.Media.Core dependency for thread joins.")
- [x] **`SDL3GLVideoSink.Submit` swallows `_disposed` silently** — fixed: now throws `ObjectDisposedException`. `SDL3VideoSink.Submit` standardized the same way. `VideoPlayer.OnVideoTick`'s try/catch already disposes the frame on rethrow, so no frame leak.
- [x] **`try { … } catch { /* best effort */ }` blocks (hot path)** — `VideoPlayer.OnVideoTick` Submit catch, `SDL3*VideoSink.Pump` + `RenderLoop` PresentFrame catches, and the `SafeRaise*` handlers now log via `MediaDiagnostics.LogError`. Silent glitches on those paths are diagnosable. **Dispose / teardown** paths still use empty `catch` by design — see §**Implementation verification (2026-05-13)**.

---

## VideoPlaybackSmoke audit (2026-05-12 PM)

`MediaFramework/Tools/VideoPlaybackSmoke/` is a new smoke test that opens a media file, plays its video through SDL3 (GL or CPU) optionally mirrored over NDI, and slaves the video clock to PortAudio's playback clock. Below is a focused audit of (1) the tool itself, (2) likely root causes of the audio dropouts the user heard with 720p24 NV12 content, and (3) a roadmap to combining the audio and video paths under a single clock-aware facade.

### Tool-level cleanup

- [x] **`VideoDmabufNv12Backing.cs` `using System.Threading;`** — **verified needed** (`Interlocked` in `Dispose`); the earlier “unused using” note was stale — treat as resolved.

- [x] **`TwinCpuVideoSink.Dispose` / ownership** — class `<remarks>` document primary vs secondary; `Dispose` only closes the window sink.

- [x] **`TwinCpuVideoSink.DuplicateCpuBackedFrame` heap `byte[]` per plane** — `ArrayPool<byte>.Shared` + `VideoFrame.release` return (failure path returns too).

- [x] **`PlaybackCli` usage vs `--ndi` + `--drm-gl`** — help text states they are mutually exclusive up front.

- [x] **Probe `PortAudioOutput` in `TryCreate`** — removed (device validation happens on the real output ctor).

- [x] **Status-line dropout diagnostics** — prints `vLate`, `paUnd`, `paDr`, `pumpDr`, show/decoded counts.

### Audio dropouts — likely root causes (720p24 NV12)

A few independent suspects, ordered from most to least likely:

- [x] **Startup ordering / prebuffer** — `VideoPlaybackSmoke`: **decoder-direct** `PrefillMainOutputDirectFromDecoder` (into PortAudio only) → `StartHardwareOutput()` → `AudioPlayer.Play()`. Running the router before the device is open made `WaitForCapacity` a no-op and filled/dropped the ring while mux audio **`ISeekableSource.Position`** (shared demux) raced ahead.

- [x] **Dedicated `Prefill` on `PortAudioOutput` / `AudioPlayer`** — `PortAudioOutput.PrefillFrom` plus `AudioPlayerPortAudioExtensions.TryPrefillPrimaryPortAudio`; `VideoPlaybackSmoke` prefill delegates to that path.

- [x] **`SinkPump` thread priority** — drainer thread is now `AboveNormal` (matches router producer).

- [x] **Default `chunkSamples`** — `VideoPlaybackSmoke` default **960** (20 ms @ 48 kHz).

- [x] **First-chunk silence-padding on resume** — documented on `AudioRouter.Resume` (`<remarks>`: partial-read silence pad on first chunk after `Pause`).

### Audio + video clock unification — proposal

Today's situation:
- `MediaClock` is the visible playhead, optionally mastered to an `IPlaybackClock`.
- `PortAudioOutput` implements both `IClockedSink` (paces the audio router) and `IPlaybackClock`. **`ElapsedSinceStart`** tracks **`PlayedSamples`** in the long term but advances with **`Pa_GetStreamTime`** between callbacks so the master is not stuck for a whole output buffer. `AudioPlayer.AddOutput` auto-wires both when present.
- `VideoPlayer` subscribes to `IMediaClock.VideoTick` and uses `IMediaClock.CurrentPosition` to pick the most recent in-window frame.
- `VideoFileDecoder` provides each frame's libav PTS as `VideoFrame.PresentationTime` (the "PTS clock" the user mentioned, indirectly).

So **A/V sync to PortAudio's hardware clock already works** when the master is wired. What's *not* unified:

- [x] **No single facade that owns Play / Pause / Stop / Seek for both** — **`AvPlaybackCoordinator`** centralizes ordered **`Play`** / **`Pause`** / **`Seek`** for **`AudioPlayer`** + **`VideoPlayer`**; **`MediaPlaybackSession`** forwards those calls; **`VideoPlaybackSmoke`** uses **`MediaContainerPlaybackHost`** + **`MediaPlaybackSession`** after prefill/**`StartHardwareOutput`**.
- [x] **No `PtsClock` for video-only / live playback** — `VideoPtsClock` implements `IPlaybackClock` (PTS anchor + wall delta); wire via `VideoPlayer.FramePresentationTimePresented` or call `NotifyFramePts` yourself.
- [x] **Two `AVFormatContext`s per AV file** — **`MediaContainerDecoder`** is always **`MediaContainerSharedDemux`** (single format context + threaded demux); dual-open removed.
- [x] **`SinkSlavedRouterClock` falls back to `WallClockRouterClock` if the slaved sink is removed** — [x] **`WallClockRouterClock`** is now created **lazily** on first **`Reset`** / first miss, using the router's **`sampleRate`** + **`chunkSamples`** (no stale pre-built fallback). [x] **`AddSource`** rejects sample-rate mismatches, so the router's rate does not change at runtime today — the "revisit if rate changes" note is satisfied by that invariant until a future dynamic-rate API exists. [x] **`SinkSlavedRouterClockTests`** cover ctor validation, **`WaitForCapacity`** delegation, pre-cancelled tokens (sink + wall paths), and sink-present → missing → present toggling.

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

- [x] **`TargetQueueSamples` cushion is tight** — `VideoPlaybackSmoke` / `AudioRouting.TryCreate` now targets **`max(4×chunk, min(16×chunk, ring/3))`** (~320 ms headroom at defaults) instead of **`clamp(8×chunk, 4×chunk, ring/8)`**.
- [x] **`PortAudioOutput.TargetQueueSamples` clamp ignores `CapacitySamples` lower bound** — replaced with the **`max`/`min`** formula above (no **`Math.Clamp`** with inverted bounds).
- [x] **PortAudio's default suggested latency is `defaultHighOutputLatency`** — **`VideoPlaybackSmoke`** exposes **`--device-latency-ms=`** (passed as PortAudio **`suggestedLatency`** seconds) and bumps **`TargetQueueSamples`** when set.
- [x] **No backpressure-aware decoder catch-up on Pause→Resume** — [x] **`AudioRouter.Resume`** `<remarks>` document host mitigations (same-timestamp **`ISeekableSource.Seek`** to reset converter state, or prebuffer before **`Start`**). [x] Run loop **silence-pads** partial **`ReadInto`** results (same **`Resume`** remarks). [x] **Decoder/resampler drain at the container** — **`MediaContainerDecoder.FlushCodecPipelines()`** re-syncs both streams to **`max(Video, Audio)`** **`ISeekableSource.Position`** via **`SeekPresentation`** (same libav flush + demux rewind as an explicit seek to the current mux playhead; call with pumps stopped). **`AudioRouter`** still does not own file decoders; hosts wire **`FlushCodecPipelines`** / **`SeekPresentation`** from their coordinator.
- [x] **`PortAudioOutput.Submit` silently drops on full ring** — [x] **`DroppedSamples`** counter (HUD); [x] throttled **`MediaDiagnostics.LogWarning`** (~2s, CAS-gated) when drops occur, with guidance on prefill / **`TargetQueueSamples`**. Callback path unchanged (no logging in real-time callback).
- [x] **`AudioRouter.RunLoop` allocates a new `float[]` per pumped chunk when the free-pool is exhausted** — `SinkPump.Commit` now drops in place and reuses **`_working`** when both pool and **`_ready`** are empty (**`RecordDrop`** unchanged).

### NDI audio "doesn't look healthy" — root-cause shortlist

`VideoPlaybackSmoke` plumbs NDI audio as `routing.Player.AddOutput(ndSink, sinkPumpCapacityChunks: …)` (larger per-sink **`SinkPump`** queue than the router-wide default). The NDI sink shares the same audio router as PortAudio, but with several mismatches:

- [x] **NDI audio inherits 20 ms chunk cadence (50 Hz frame rate)** — [x] optional **`NdiAudioAggregatingSink`** in **`VideoPlaybackSmoke`** (default target ≈ one video frame of samples from fps; **`--ndi-audio-aggregate=0`** disables, **`>0`** fixed); [x] **`NDIAudioSink`** 100 ns timecodes + **`clockAudio: true`** in smoke; [x] **Send-side SDK introspection** — **`NDIOutput`** exposes **`TryGetReceiverTally`**, **`CaptureReceiverMetadata`**, **`FreeReceiverMetadata`**, **`GetReceiverConnectionCount`**, **`ClearConnectionMetadata`** / **`AddConnectionMetadata`** (**`NDIlib_send_get_tally`** / **`NDIlib_send_capture`** / connection metadata); [x] **Documented egress clock / pacing** — **`NDIVideoSender`**, **`NDIAudioSink`**, **`NDISender.Create`** remarks (**Tier C** row **8**). NDI Monitor–specific fusion stays **field-driven** only.
  - [x] **Stamp real timecodes** — **`NDIAudioSink`** uses **100 ns** ticks from a running sample counter.
- [x] **NDI audio starts ~prefill-duration *after* PortAudio audio** — **`PrefillMainOutputDirectFromDecoder`** now mirrors the same PCM into the NDI audio sink (when present) alongside PortAudio.
- [x] **NDI audio sink shares `pumpCapacityChunks=8`** — [x] **`ndiDr`** in **`VideoPlaybackSmoke`** HUD; [x] per-sink pump depth via **`AudioRouter.AddSink`** / **`AudioPlayer.AddOutput`** (**`--ndi-audio-pump-chunks=`**, default **24** for NDI in the smoke tool).
- [x] **`NDIOutput` `clockAudio: true`** in **`VideoPlaybackSmoke`**.
- [x] **`NDIAudioSink.Submit` allocates / frees a native buffer only when capacity grows** (`EnsurePackedCapacity`) — [x] growth uses **`NativeMemory.Realloc`** with **≥2×** prior size (power-of-two target) so early-session chunk-size shifts cause at most a couple of resizes on the pump thread; steady-state remains allocation-free.
- [x] **NDI receivers handle audio at NDI video frame cadence by default** — **`VideoPlaybackSmoke`** targets ~one frame of samples when **`--ndi-audio-aggregate`** is left at auto (see **`--ndi-audio-aggregate=`**); fixed sizes via **`>0`**. Further SDK-specific tuning remains optional — **§Unified backlog**.

### `VideoPtsClock` integration

`VideoPtsClock` ships with unit tests; **`VideoPlaybackSmoke`** now wires **`FramePresentationTimePresented`** when audio routing is unavailable.

- [x] **Wire `VideoPtsClock` in audio-less playback** — `VideoPlaybackSmoke` wires **`FramePresentationTimePresented`**, calls **`BeginSession`** on the first PTS, **`MediaPlaybackSession.Play(videoOnlyMaster: videoPtsClock)`**, and **`Pause`**/**unsubscribe** in teardown.
- [x] **`AvPlaybackCoordinator` / audio-less hosts** — [x] **`Play`** accepts **`IPlaybackClock? videoOnlyMaster`** when **`audio`** is null; [x] **`MediaPlaybackSession`** façade for ordered **`Play`**/**`Pause`**/**`Seek`**.

### New code — minor regressions / cleanups

- [x] **Unused `using System.Collections.Generic;` in `VideoFileDecoder.cs:2`** — removed.
- [x] **`VideoFileDecoder` / `MediaContainerSharedDemux` `_passThroughArena`** — Treiber-stack pooled descriptor arrays + **`Array.Clear`** before reuse; see **`VideoFileDecoder`**, **`MediaContainerSharedDemux`**, **`PassThroughDescriptorArena`** `<remarks>`. [ ] Structural lock-free / outer-lock changes only if profiling demands it — **§Tier F** row **29** (same scope as **Tier E** **16**).
- [x] **`VideoFileDecoder`… stale `ReadOnlyMemory<byte>`** — **`ReturnPassThroughDescriptors`** **`Array.Clear`** on plane + stride arrays before pooling (including pool-at-cap overflow); clear runs before the arena **`lock`**.
- [x] **`AvPlaybackCoordinator.Play` order** — [x] optional **`verifyPrebufferAfterPrefill`** (`Func<bool>?`) after **`prefillBeforeHardware`**, before **`startHardware`** — throws **`InvalidOperationException`** when **`false`**; [x] **`Pause`/`Stop`** use **`try { video.Pause } finally { audio?.Pause; flush? }`** with optional **`flushSharedMuxAfterPause`**.
- [x] **`AvPlaybackCoordinator.Seek` order** — [x] **`video.Clock.Seek`** when **`audio`** is null; [x] **`SeekCoordinated`** + coordinator `<remarks>` document pause-bracketed seek with **`MediaContainerDecoder.SeekPresentation`** when both streams share one demuxer.
- [x] **`SinkSlavedRouterClock` fallback creation eagerness** — **`WallClockRouterClock`** is created lazily under a small lock (first **`Reset`** or first miss when **`resolveSink`** returns **`null`**), using the owning router's **`sampleRate`** / **`chunkSamples`** captured at construction.
- [x] **Per-leaf ppm / resampling drift** — **`AdaptiveRateAudioSink`** + **`PumpPressure`** (see §Deep library audit). [x] **`AudioRouter.SampleRate`** — unchanged while running; **stopped-only** **`ReconfigureSampleRate`** when all routes already match the new Hz; **`SinkSlavedRouterClock`** / **`WallClockRouterClock`** capture **`sampleRate`** / **`chunkSamples`** when those clocks are constructed. [ ] **Changing nominal** **`sampleRate`** **while `IsRunning`** — **§Tier F** row **22** — **§Unified backlog**.
- [x] **`PortAudioOutput` `IPlaybackClock` stair-stepped at buffer rate** — **`ElapsedSinceStart`** only moved when **`PlayedSamples`** advanced in the PA callback (~10–25 ms @ 48 kHz), so **`VideoPlayer`** saw **~47 Hz** playhead motion on 60 fps video; fixed with **`Pa_GetStreamTime`** + per-segment anchor (see **`PortAudioOutput.cs`**).
- [x] **`ChannelMap`… `Vector<float>.Count == 16`** — when **`Avx.IsSupported && vn == 16`**, **`TryAccumulateMonoDupStereoInterleaved`** and **`TryAccumulateStereoDuplexWideInterleaved`** run the existing AVX2 kernels twice per 16-wide block (no **`Avx512F`** dependency); hardware without AVX still uses SSE / **`Vector<float>`** paths.
- [x] **`VideoPlayer.FramePresentationTimePresented`** — `<remarks>` on the event documents the **media-clock driver thread**; keep handlers light.

### Quick verification when investigating the user's audio reports

For local "audio jumps":
1. Run `VideoPlaybackSmoke <file>` (hardware decode is default; add **`--no-hw`** for software-only) for 60 s.
2. Read the HUD's final `paUnd` / `paDr` / `pumpDr` / `ndiDr` (with `--ndi`).
3. If all three are 0 but the user still hears jumps, the source is likely PortAudio's audio thread itself (Pulse server jitter, ALSA period underrun). Try `defaultLowOutputLatency` or pass an explicit `framesPerBuffer` matching the ALSA period.

For NDI Monitor audio health:
1. Run `VideoPlaybackSmoke <file> --ndi MFPlayer` (without `--drm-gl`).
2. On the receiver side, run `mediainfo` against the NDI source or watch NDI Monitor's audio waveform meter for cadence wobble.
3. If video stays centered green and audio shows uneven bars: cadence / prefill issues — **`clockAudio: true`** + real audio timecodes are now default in the smoke tool; check **`ndiDr`** in the HUD.
4. If problems persist, tune **`--ndi-audio-aggregate=`** (auto targets ~one video frame of samples; **`0`** disables) and **`--ndi-audio-pump-chunks=`** — chunk aggregation and larger per-sink pumps are shipped (see §**NDI audio "doesn't look healthy"** above). Deeper SDK clock coupling remains **§Unified backlog**.

---

## NDI egress A/V sync & Monitor video health (2026-05-13)

**User report:** 720p24 NV12 file via **`VideoPlaybackSmoke --ndi`**: audio and video **feel out of sync**, **video stutters**; in **NDI Studio Monitor** audio health **max**, video health **very poor** (inverse of the “audio bars uneven / video green” case already noted in §**Quick verification** above).

**Code review — likely contributing factors (ordered):**

1. **Asymmetric NDI SDK clock flags (`VideoPlaybackSmoke`)** — default remains **`clockVideo: false`** + optional **`PaceBelowFramePeriod`** wall pacing; use **`--ndi-clock-video`** (often with **`--ndi-disable-wall-pace`**) to let the SDK pace video. Audio stays **`clockAudio: true`**.

2. **Timecode model (updated 2026-05-13)** — **`NDIAudioSink`** stamps **100 ns** timecodes from **`AudioFrame.PresentationTime`**. With **`NDIVideoTimecodeMode.PresentationRelativeTicks`**, **`NDIOutput`** shares one **`NdiEgressPresentationTimeline`** between **`NDIVideoSender`** and **`NDIAudioSink`** so both streams use the same session anchor (first submitted PTS from either stream). **`NDIVideoTimecodeMode`** (**`PresentationRelativeTicks`** in **`VideoPlaybackSmoke`**; **`MuxerPresentationTicks`** in **`NDIPlayer`**); **`NDIOutput.ResetVideoPresentationTimecodeAnchor()`** clears that anchor after seek when hosts wire it.

3. **`PaceBelowFramePeriod` = `0.93 / fps`** — unchanged default when wall pacing is on; **`--ndi-disable-wall-pace`** disables it for A/B with **`--ndi-clock-video`**.

4. **`PaceBeforePack` remainder (shipped 2026-05-13)** — sub-millisecond tail now uses **`Thread.Sleep(remainder)`** instead of a busy **`SpinWait`** loop (coarser but gentler on the pump thread).

5. **Bounded NDI video queue (`VideoPlaybackSmoke` default, 2026-05-13)** — **`VideoPlaybackSmoke`** default **`VideoSinkPump`** depth is now **8** (**`--ndi-video-pump-frames=`** override).

6. **Fan-out cost (SDL primary + NDI branch)** — For negotiated CPU **NV12** with matching branches and no per-branch **`VideoCpuFrameConverter`**, **`VideoRouter`** / **`VideoOutputRouter`** share one backing via **`VideoFrame.TryCreateNv12CpuFanOutViews`** (no router **`DuplicateCpuBacking`**). **`NDIVideoSender.PackNv12`** still copies into ping-pong staging for the wire. Ineligible fan-out (converters, non-NV12, no release, …) still uses **`DuplicateCpuBacking`** on the branch. See **§Unified backlog** if you need deeper NDI/router perf work.

7. **`NDIPlayer` wall pacing vs NDI Monitor health** — [x] **`PaceToPresentationTime`** no longer sleeps only in **1 ms** steps (coarse sleep + **`Thread.Sleep(0)`** + bounded **`SpinWait`** for the last **~0.35 ms**) so cumulative quantization does not walk seconds late vs mux PTS. [x] Default **wall-anchor drift leak** (small proportional nudge to **`Stopwatch`** anchor after each paced emit) keeps real-time alignment closer over long runs; **`--no-wall-drift-correct`** disables for A/B.

8. **`MediaContainerDecoder` / seek prime** — [x] Changing **`SelectOutputFormat`** after **`SeekPresentation`** invalidates **`_vPrimedAfterSeek`** when the negotiated **pixel format** changes so the first discrete read matches **`IVideoSink.Configure`**.

**Backlog checkboxes**

- [x] **Unified NDI egress timeline** — [x] **`NDIVideoTimecodeMode.PresentationRelativeTicks`** + **`NDIOutput.ResetVideoPresentationTimecodeAnchor()`** in **`VideoPlaybackSmoke`**; [x] **`NDIPlayer`** uses **`MuxerPresentationTicks`** on video + mux PTS on **`NDIAudioSink.Submit(in AudioFrame)`** with **PTS-ordered** mux pump; [x] **shared presentation anchor** — **`NdiEgressPresentationTimeline`** on **`NDIOutput`** when **`PresentationRelativeTicks`** is selected (audio + video **`Submit(in …Frame)`** timecodes); [x] optional **`IPlaybackClock`** for egress — **`NdiEgressMuxPlayheadClock`** (**`NotifyPresentation`**, **`Pause`**/**`Resume`**/**`Reset`**, **`MediaClock.SetMaster`**) when hosts want one mux envelope object (not wired by default in tools).
- [x] **`VideoPlaybackSmoke` NDI CLI** — **`--ndi-clock-video`**, **`--ndi-disable-wall-pace`**, **`--ndi-video-pump-frames=`**, **`--ndi-video-tc=pts|synth`** (defaults: **pts** timecode, **pump 8**, wall pace **on** unless disabled).
- [x] **`PaceBeforePack` without `SpinWait`** — remainder uses **`Thread.Sleep(TimeSpan)`** after coarse sleep.
- [x] **`NDIPlayer` CLI** — **`--no-wall-pace`**, **`--no-wall-drift-correct`** (wall pace default **on**, anchor leak default **on** when wall pacing is enabled).

**Suggested experiments (host / tool, no code required first):**

- Watch **`ndiVidDr`** in the **`VideoPlaybackSmoke`** HUD during the session; non-zero correlates with **router pump drops** and stutter.
- Try **`--ndi-audio-pump-chunks=`** only if **`ndiDr`** is non-zero (audio side); the reported issue is **video**, so prioritize **video queue / clocking**.
- Further A/B: **`--ndi-clock-video --ndi-disable-wall-pace`**, **`--ndi-video-tc=synth`** vs default **`pts`**, tune **`--ndi-video-pump-frames=`**.
- **`NDIPlayer`**: compare default **wall-anchor drift correction** vs **`--no-wall-drift-correct`** if Monitor health drifts over minutes; **`--no-wall-pace`** is decode-as-fast-as-possible (SDK **`clockAudio:true`** still paces audio on the wire).

---

## Checklist spot re-verification (2026-05-13)

Representative **`[x]`** claims from this doc were re-read in source and **`dotnet test MFPlayer.sln -c Release`** was executed (all projects **Passed**).

| Claim | Status |
|-------|--------|
| **`NDIAudioSink`** stamps **100 ns** `Timecode` from **`AudioFrame.PresentationTime`** (or shared **`NdiEgressPresentationTimeline`**); **`Submit(ReadOnlySpan<float>)`** uses the running sample counter | **Present** (`NDIAudioSink.cs`). |
| **`NDIVideoSender`** `PaceBeforePack` coarse sleep + **`Thread.Sleep`** remainder (no **`SpinWait`**) | **Present** (`NDIVideoSender.cs`). |
| **`VideoPlaybackSmoke`** NDI: **`PresentationRelativeTicks`** default, **`--ndi-*`** toggles, pump default **8**, wall pace unless **`--ndi-disable-wall-pace`** | **Present** (`Program.cs`). |
| **`NDIPlayer`** mux-ordered pump, **`MuxerPresentationTicks`**, wall drift CLI, **`MediaContainerSharedDemux`** primed-frame discard on **`SelectOutputFormat`** | **Present** (`NDIPlayer/Program.cs`, `MediaContainerSharedDemux.cs`). |
| **`NdiEgressMuxPlayheadClock`** | **Present** (`S.Media.NDI/Clock/NdiEgressMuxPlayheadClock.cs`). |
| **`RUN_NDI_EGRESS_SOAK`** / **`NdiEgressPresentationTimelineTests`** | **Present** (`NdiEgressPresentationTimelineTests.cs`). |
| **`RUN_NDI_EGRESS_SOAK_STRESS`** (optional **1M** rounds, same test) | **Present** (`NdiEgressPresentationTimelineTests.cs`). |
| **`MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD`** / **`Nv12Win32SharedHandleGpuUploadProfiling`** | **Present** (`S.Media.OpenGL/Diagnostics/Nv12Win32SharedHandleGpuUploadProfiling.cs`, **`Nv12Win32SharedHandleGpuUploader.TryUpload`**). |
| **`RUN_WIN32_NV12_D3D11_INTEROP_STRESS`** / **`Win32Nv12D3d11InteropStressTests`** | **Present** (`S.Media.OpenGL.Tests/Win32Nv12D3d11InteropStressTests.cs`; **`ProfilingTestProcessDefaults`** disables upload profiling by default). |
| **`MediaContainerDecoder.FlushCodecPipelines`**, **`NDIOutput`** tally/metadata poll | **Present** (`MediaContainerDecoder.cs`, `NDIOutput.cs`). |
| **`OSCServer.HandleOversizePacket`** uses **`Interlocked.Increment`** for **`_oversizeDrops`** | **Present** (`OSCServer.cs`). |
| **`SinkPump.Commit`** drops in place when free pool and consumer queue are both empty | **Present** (`AudioRouter.SinkPump.Commit`: **`RecordDrop()`** + early **`return`** reuses **`_working`** — ~1071–1080). |
| **`AudioRouter`** natural EOF: **`RunLoop`** **`finally`** → **`FinishRunLoopThreadLifetime`** (**`IFlushableSink.Flush`**, pump drain, CTS dispose) | **Present** (`AudioRouter.cs`); covered by **`AudioRouterControlTests.NaturalEof_FlushesFlushableSinks`**. |
| **`D3D11InteropUtility.TryGetAdapterLuidFromTexture`** | **Present** (`D3D11InteropUtility.cs`; **`D3D11InteropUtilityTests`** on Windows). |
| **`LinuxDmabufGlHardwareFormats`** | **Present** (`GetPrimeGlImportBlocker` per-layout families, **`IsSupportedForPrimeGlImport`**, **`LinuxDmabufGlHardwareFormatsTests`** incl. enum parity). |
| **`IPlaybackTimeline`** / **`IMediaClock : IPlaybackTimeline`** | **Present** (`IPlaybackTimeline.cs` incl. **`PlaybackRate`**, **`AudioPlayer.Timeline`**, **`PlaybackTimelineClockExtensions`**, **`MediaClockTests`**). |
| **`AvRouter`** + **`IAvPlaybackSession`** | **Present** (`S.Media.FFmpeg/AvRouter.cs`, **`VideoPlaybackSmoke`**, **`AvRouterTests`**). |
| **`MediaContainerPlaybackHost`** | **Present** (`S.Media.PortAudio/MediaContainerPlaybackHost.cs`, **`VideoPlaybackSmoke`**, **`MediaContainerPlaybackHostTests`**). |

No **`[x]`** items were found falsely checked for the rows above; the new NDI findings are **additive** (gaps / improvements), not reversions of prior audit conclusions.

---

## Unified backlog (prioritized by impact)

**Impact** here means estimated **product risk, correctness, or major capability** (not strictly engineering effort). Nothing in this list blocks default **`dotnet test MFPlayer.sln`**. The **verbatim numbered archive** with per-line **`[x]`** / **`[~]`** / **`[ ]]`** tags remains **§Historical suggested backlog (archive)** (**#1–#12**). Cross-tier **`[ ]`** tails that used to live only in prose are also listed in **§Tier F — Deferred registry**.

### Tier A — Highest (subtle correctness / production edge cases)

1. **Windows D3D11 VA: decode vs GL keyed-mutex contract** — **[x]** Documented producer/consumer contract on **`D3d11TextureKeyedMutexScope`** + **`VideoWin32Nv12Backing`**; **`Nv12Win32SharedHandleGpuUploader`** uses **`TryAcquireForGpuRead`** and **aborts** interop + staging when a keyed mutex exists but **`AcquireSync(0)`** fails (no silent unsynchronized read). Lab: **`WIN32_NV12_D3D11_KEYED_MUTEX_TIMEOUT_MS`** (1–60000, default **2000**). One-time **`MediaDiagnostics.LogWarning`** on acquire failure.

2. **Windows NV12 → GL hardening** — **[x]** Lab: **`RUN_WIN32_NV12_D3D11_INTEROP_STRESS`**, **`WIN32_NV12_D3D11_INTEROP_STRESS_ROUNDS`** (up to **500k** for overnight-style device churn), **`KeyedMutexScope_TryAcquireForGpuRead_RoundTrips_WhenStressEnabled`**, **`D3D11InteropUtility.TryGetAdapterLuid` / `TryGetAdapterLuidFromTexture`**, optional **`WIN32_NV12_D3D11_STRICT_TEXTURE_ADAPTER_LUID=1`** (reject **`ID3D11Texture2D`** when DXGI adapter LUID ≠ uploader device — multi-GPU guard). Borrow-path LUID mismatch is already logged in **`SDL3GLVideoSink`**. Long **WGL_NV_DX_interop** on real decoded video remains a **manual** soak (no headless GL harness in CI).

### Tier B — High (large feature gaps and router architecture)

3. **Linux hardware GL beyond NV12/P010 PRIME** — **[x]** Shipped: **`LinuxDmabufGlHardwareFormats`** (**`Nv12`**, **`P010`**, **`P016`**), **`Nv12DmabufGpuUploader.TryUploadP010`** / **`TryUploadP016`**, **`VideoFrame.DmabufP010`** / **`DmabufP016`**, **`DrmPrimeP010BackingFactory`** / **`DrmPrimeP016BackingFactory`**, **`InferDrmPrimeOutputPixelFormat`** (12-bit / **`P016LE`** / **`Yuv420P12Le`**). Other PRIME / multi-planar FFmpeg → GL layouts remain open (**§Tier F** row **36**).

4. **`VideoRouter`: mixed DMA-BUF + per-output `VideoCpuFrameConverter`** — **[x]** Runtime **`Submit`** **`NotSupportedException`** text includes **`VideoRouter input '{Id}':`** for dma-buf / Win32 shared NV12 + branch CPU converter (**`VideoPlaybackSmoke --no-hw`** hint); **configure-time** **`ILogger`** warning when **NV12, P010, or P016** fan-out includes any branch **`VideoCpuFrameConverter`**. **GPU→CPU readback** for mixed paths remains deferred (**§Tier F** row **23**).

5. **Full `AvRouter` (Architecture strategy C)** — **[x]** **`MediaContainerAvRouter.Create`**; **`MediaContainerPlaybackGraph`** (decoder + **`VideoPlayer`** + **`IMediaClock`** + optional **`AudioPlayer`** + **`AvRouter`** references — same disposal rules as **`AvRouter`**); **`AvPlaybackCoordinator.Pause`** when **`AudioPlayer`** is null; **`MediaContainerPlaybackHost`**. **Still deferred:** one mega-host that owns demux + **`AudioRouter`** + dynamic **`VideoRouter`** + full lifecycle (**§Tier F** row **24**).

6. **`IPlaybackTimeline` / strategy B** — **[x]** **`IPlaybackTimeline`** on **`IMediaClock`**; **`AudioPlayer.Timeline`**; **`IAvPlaybackSession.Timeline`**; **`PlaybackTimelineClockExtensions.SubscribePositionChanged`**; further pause-only service extraction remains optional.

#### Tier B — deferred follow-ups (explicit)

These are called out inline on rows **4–5** above; they stay **out of scope** until a dedicated design pass. **Also** **§Tier F** rows **23–24**.

- **GPU → CPU readback** for **mixed** DRM PRIME / Win32 NV12 **dma-buf** paths **plus** per-output **`VideoCpuFrameConverter`** (true zero-copy fan-out with **swscale** on a branch). Today the router **rejects** that combination at **`Submit`** time; removing the restriction needs a driver-specific readback or decode-to-CPU policy, not router-only glue.
- **Process-wide mega-host** that **owns** the shared **`MediaContainerDecoder`** demux thread, the live **`AudioRouter`** / **`AudioPlayer`** graph, dynamic **`VideoRouter`** outputs, and **coordinated** teardown order (today **`MediaContainerPlaybackGraph`** + **`AvRouter`** only **reference** those pieces — callers still own disposal).

### Tier C — Medium (labs, soak, NDI field behaviour)

7. **NDI long-run and full-wire harness** — **[x]** Optional **`RUN_NDI_EGRESS_SOAK=1`** / **`RUN_NDI_EGRESS_SOAK_ROUNDS`** / **`RUN_NDI_EGRESS_SOAK_STRESS`** (**`NdiEgressPresentationTimelineTests`**); **`RUN_NDI_MUX_SOAK=1`** (**`NdiEgressMuxPlayheadClockTests`**). **`VideoPlaybackSmoke`** / **`NDIPlayer`**: **`--ndi-wait-first-receiver-ms=n`** → **`NDIOutput.GetReceiverConnectionCount`**. Multi-hour / memory-pressure runs remain **manual** lab (**open:** **§Tier F** row **25**).

8. **Deeper NDI SDK clock coupling** — **[x]** **`NDIOutput`**: **`GetReceiverConnectionCount`** / **`ConnectionCount`**, **`TryGetReceiverTally`**, **`CaptureReceiverMetadata`** / **`FreeReceiverMetadata`**, **`ClearConnectionMetadata`** / **`AddConnectionMetadata`** (delegate to **`NDISender`**). **`NDIVideoSender`** / **`NDIAudioSink`** remarks cover **`clockVideo`** / **`clockAudio`** vs host pacing and shared **`NdiEgressPresentationTimeline`**. NDI Monitor–specific fusion stays **field-driven** (**open:** **§Tier F** row **26**).

9. **NDI + SDL: CPU NV12 fan-out copy cost** — **[x]** **`NDIVideoSender.PackNv12`** / **`PackI420`** contiguous fast paths; **`VideoFrame.TryCreateNv12CpuFanOutViews`** + **`VideoRouter`** / **`VideoOutputRouter`** refcounted shared CPU NV12 when every branch matches negotiated NV12 with no **`VideoCpuFrameConverter`** (no per-branch **`DuplicateCpuBacking`**).

10. **Further NDI SDK-specific tuning** — **[x]** Lab env lines in **`VideoPlaybackSmoke`** and **`NDIPlayer`** usage; **`NDIOutput`** forwards connection metadata to **`NDISender`**; **`NDISendCreate`** / **`NDISender.Create`** document **`clockVideo`** / **`clockAudio`**-only create settings until the native struct grows.

### Tier D — Lower (API / design constraints)

11. **`AudioRouter` nominal sample rate** — **[x]** Unchanged while **`IsRunning`**; **stopped-only** **`ReconfigureSampleRate`** when every source/sink already matches the new Hz; **`AdaptiveRateAudioSink`** for **per-sink drift** at a fixed nominal graph rate. Hot retune while running: **§Tier F** row **22**. (**§Historical #12**; **§10** / audit.)

12. **Multi-sink inter-sink PPM drift** — **[x]** **`AudioRouter`** `<remarks>` (**`IClockedSink`** slave, **`PumpPressure`**, **`AdaptiveRateAudioSink`**) — per leaf, not a global multi-output master. (**`S.Media.Core` deep audit “Watch”**.)

13. **`CompositePlaybackClock`** — **[x]** Priority merge + **instant** **`ElapsedSinceStart`** handoff when several clocks advance; temporal **cross-fade** is **not** implemented (**open:** **§Tier F** row **21**). (**§Historical #6** trailing sentence.)

### Tier E — Smaller (deferred research, profile-only work, hygiene)

All **Tier E** rows below use **`[ ]]`** (open), **`[~]`** (partial), or **`[x]`** (checklist scope met for this tier; long-tail follow-ups stay in **§Tier F** **27–33**).

14. **[x]** **“Zero COM” / Core-only GL** — [x] **Tier E checklist (explicit non-goal):** product decision recorded in **§Windows zero-host** and the unified checklist **§1** / **§2** / **§3** (hardware GL / hardware FFmpeg / interop contract rows): shipped paths may still carry libav **`ID3D11Device`/`ID3D11Texture2D`** COM on **`HardwareVideoSurfaceDescriptor`** / **`VideoWin32Nv12Backing`** or use a **negotiated borrow** / SDL **`D3D11GlInteropDeviceHost`**; **true zero-host** today means **no SDL-owned** interop device while **still** using libav COM when the backing exposes it — **not** “no COM on frames.” **GL with no `ID3D11Device` COM on frames** and **Core-only decode→GL without any D3D11 on descriptors/frames** remain **out of scope** until a libav lifetime + mutex story can be owned from Core alone — **§Tier F** rows **27**, **34**, **35** (registry).

15. **[x]** **`ChannelMap` SIMD: non-permutation asymmetric maps** — [x] Same-width packed **permutation** SIMD for **N ∈ {3, 4, 5, 6, 7, 8}** (**`TryAccumulatePackedPermutationInterleaved`**, AVX2; **N = 4** uses SSE **`SHUFPS`**). [x] Stereo → quad **paired duplicate** maps **<c>[0,0,1,1]</c>** / **<c>[1,1,0,0]</c>** (**`TryAccumulateStereoDuplexGroupedInterleaved`**, **`TryAccumulateStereoDuplexGroupedSwappedInterleaved`**). [x] Stereo → **N** using only **<c>0</c>** / **<c>1</c>** / **<c>-1</c>** with at least one silence (**`TryAccumulateStereoSilenceOrZeroDupInterleaved`**, e.g. **<c>[-1,0,0,-1]</c>**) plus the other SIMD paths **`ApplyAdditive`** tries before its scalar fallback. Further arbitrary same-width non-permutation kernels are **not** Tier E scope — add only when profiling shows a hotspot — **§Tier F** row **28**. (**§Historical #2**.)

16. **[x]** **`VideoFileDecoder` pass-through arena** — Treiber **`PassThroughDescriptorArena`** pools + **`Array.Clear`** contract, **`Rent`/`Return`** without an extra arena mutex (single-threaded decode/demux per context; see type `<remarks>`). [x] **`PassThroughArenaProfilingTests`**. Optional outer-lock / wait-free restructuring stays **§Tier F** row **29** (profiling-gated **`MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1`** only).

17. **[x]** **Long-run property tests** — [x] **Tier E checklist (micro-stress + env):** **`CompositePlaybackClockTests`** (incl. three-way / equal-priority / rotating stress, **`EqualPriority_fourCandidates_seeded_subsetAdvancing_firstRegisteredWinsAmongAdvancing`**), **`MediaClockMasterTests`** (incl. **`SetMasterChain_threeCandidates_seededReads_stayNonRegressingWhileHighestWins`**), **`MediaClockTests.RepeatedSeek_whileRunning_doesNotThrow`**, **`PumpPressurePlaybackHintMonitorTests`** (incl. **`ApplyObservation_manySteps_staysClamped_and_finite`**, **`ApplyObservation_sinkFilterConstructor_manySteps_staysClamped_and_finite`**), **`PassThroughArenaProfilingTests`** (incl. **`ManyRentReturn_cycles_underProfiling_incrementsCounters`**), **`RUN_MEDIA_SOAK` / `RUN_MEDIA_SOAK_ROUNDS`**, **`ChannelMapTests.ApplyAdditive_GroupedStereoQuad_seeded_steps_match_naive`**, **`ChannelMapTests.ApplyAdditive_Packed8Permutation_seeded_steps_match_naive`**. [x] **`CompositePlaybackClock`** stable tie-break on equal **`Priority`**. **Open:** multi-hour / heavy CI property suites — **§Tier F** row **30**.

18. **[x]** **Automatic master-clock PPM + drop/repeat policies** — [x] **Tier E checklist (documentation):** `<remarks>` on **`PumpPressurePlaybackHintMonitor`**, **`AudioRouter`**, **`MediaClock`**, **`MediaClockExtensions`** / **`CompositePlaybackClock`** (composite master selection), **`MediaPlaybackSession`**, **`AvPlaybackCoordinator`**, **`IAvPlaybackSession`**, **`AvRouter`**, **`MediaContainerPlaybackGraph`**, **`AdaptiveRateAudioSink`** — graph-wide coordinated master PPM + synchronized drop/repeat is **host-owned**; shipped code exposes **`PumpPressure`**, **`PumpPressurePlaybackHintMonitor`** hints, **`MediaClock`/`SetMasterChain`** authority selection, and per-sink **`AdaptiveRateAudioSink`** only. **Open:** first-party coordinated policy module / wiring — **§Tier F** row **31**.

19. **[x]** **Extended teardown `catch { }` logging** — **`#if DEBUG`** **`MediaDiagnostics`** on **`VideoPlayer`**/**`AudioPlayer`** dispose, production teardown paths in **`NDIOutput`**/**`NDIVideoSender`**/**`SDL3GLVideoSink`**/**`YuvVideoRenderer`**/**`Nv12Win32SharedHandleGpuUploader`**, **`NDIPlayer`** verbose HUD wait, tests’ temp **`File.Delete`**. Release **`#else`** silence on GL/NDI/SDL cooperative teardown remains **intentional** (see **Implementation verification**). **Tier F** row **32** now means **new** assemblies/paths only.

20. **[x]** **`AudioFileDecoder` parallel decode (single context)** — **`CodecThreadCount`** + **`thread_type`** + **`LibavCodecThreadType`** + `<remarks>`. Multi-decoder instances, affinity, explicit per-codec **`thread_type`** overrides — **§Tier F** row **33**.

### Tier F — Deferred registry (explicit `[ ]` tails)

**Policy:** whenever this doc or code defers work (“remains …”, **`[ ]`** in the unified checklist), add or refresh a row here so **`grep 'Tier F'`** / **`§Tier F`** finds it. **`[~]`** partial progress stays on the matching **Tier E** line where applicable. **Tier F** rows **27–33** stay **`[ ]]`** as the long-tail registry (Tier **E** rows **14–20** may be **`[x]`** while their mirror rows remain open until those follow-ups actually ship).

Single grep-friendly list for items that are **called out elsewhere** (Tier **B**/**C**/**D** prose, unified backlog **`[ ]`** bullets) so they are not “lost” in long paragraphs.

21. **[ ]** **`CompositePlaybackClock` temporal cross-fade** — Blend or ramp **`ElapsedSinceStart`** / authority when several **`IPlaybackClock`** candidates advance together; today **priority snap** only (**Tier D** row **13**).

22. **[ ]** **`AudioRouter` nominal sample rate while `IsRunning`** — Hot retune of **`SampleRate`** with the run loop active (vs **stopped-only** **`ReconfigureSampleRate`** today, **Tier D** row **11**).

23. **[ ]** **GPU → CPU readback** — **`VideoRouter`** mixed DRM PRIME / Win32 NV12 **dma-buf** + per-branch **`VideoCpuFrameConverter`** without rejecting **`Submit`** (**Tier B** deferred follow-up).

24. **[ ]** **Process-wide mega-host** — One process owns shared **`MediaContainerDecoder`**, live **`AudioRouter`**, dynamic **`VideoRouter`**, coordinated teardown (**Tier B** deferred follow-up).

25. **[ ]** **NDI long-run memory-pressure harness** — CI/automation beyond optional env soaks; multi-hour / memory-pressure remains **manual** today (**Tier C** row **7**).

26. **[ ]** **NDI Monitor–specific clock / health fusion** — Deep SDK coupling beyond pacing toggles + **`PumpPressure`** (**Tier C** row **8** tail).

#### Tier F (continued) — mirror of **Tier E** rows **14–20** (long-tail / non-goals; Tier **E** may mark **`[x]`** while rows **27–33** stay open until those follow-ups actually ship)

27. **[ ]** (same scope as **Tier E** **14** — **Tier E** row **14** checklist is **[x]** as an explicit **non-goal** / shipped-contract clarification) — **“Zero COM”** on the media path (no **`ID3D11Device`** COM on frames) and **Core-only** decode→GL **without** D3D11 on descriptors/frames; see **§Windows zero-host** and **§Tier F** rows **34**/**35** for the narrower libav/Core-only tails.

28. **[ ]** (same scope as **Tier E** **15** — **Tier E** row **15** checklist is **[x]**) — **`ChannelMap`** SIMD for **additional** asymmetric packed maps beyond the shipped **`ApplyAdditive`** fast paths (permutations **N ∈ {3,…,8}**, stereo→quad paired dups **<c>[0,0,1,1]</c>** / **<c>[1,1,0,0]</c>**, stereo **<c>0</c>/<c>1</c>/<c>-1</c>** patterns, mono dup **N**, wide stereo spreads, **`StereoToN`**, packed identity **N ≥ 3**, etc.). Arbitrary other same-width non-permutation layouts stay scalar until profiling justifies a new kernel.

29. **[ ]** (same scope as **Tier E** **16**) — optional **`VideoFileDecoder`** / **`MediaContainerSharedDemux`** outer lock or wait-free restructuring around **`PassThroughDescriptorArena`** only if **`MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1`** shows contention beyond Treiber pools (Tier **E** **16** checklist otherwise **[x]**).

30. **[ ]** (same scope as **Tier E** **17** — **Tier E** row **17** checklist is **[x]** at micro-stress + optional soak env scope) — large seeded long-run / **multi-hour** / heavy **CI** property suites beyond **`CompositePlaybackClockTests`**, **`MediaClockMasterTests`**, **`MediaClockTests`**, **`PumpPressurePlaybackHintMonitorTests`**, **`PassThroughArenaProfilingTests`**, **`ChannelMapTests`** seeded parity (**`ApplyAdditive_GroupedStereoQuad_seeded_steps_match_naive`**, **`ApplyAdditive_Packed8Permutation_seeded_steps_match_naive`**), **`RUN_MEDIA_SOAK`** / **`RUN_MEDIA_SOAK_ROUNDS`**, **`RUN_NDI_EGRESS_SOAK`** / rounds.

31. **[ ]** (same scope as **Tier E** **18** — **Tier E** row **18** checklist is **[x]** at the documentation boundary) — first-party **automatic master** clock ppm + **drop/repeat** coordination **module** / wiring beyond shipped hints, **`PumpPressure`**, composite **`MediaClock`** master selection, and per-sink **`AdaptiveRateAudioSink`**.

32. **[ ]** (same scope as **Tier E** **19**) — teardown **`catch { }`** triage on **new** assemblies or new dispose paths not yet reviewed; Tier **E** **19** inventory is **[x]** for current **`MediaFramework/`** shipping libs + tests + **`NDIPlayer`** (intentional Release **`#else`** silence on cooperative GL/NDI/SDL teardown unchanged — **Implementation verification**).

33. **[ ]** (same scope as **Tier E** **20**) — **`AudioFileDecoder`** *host policy* beyond one libav context: multi-decoder instances per stream, demuxer affinity, CPU pinning, or explicit **`thread_type`** overrides when auto frame/slice selection is insufficient (Tier **E** **20** single-context wiring is **[x]**).

#### Tier F (continued) — other checklist **`[ ]`** deferrals

34. **[ ]** **Libav DXGI for GL with zero COM `ID3D11Device` on frames** — checklist **§1** / hardware GL row (libav-alone DXGI).

35. **[ ]** **Core-only decode→GL without any D3D11 COM on descriptors/frames** — checklist **§1** / hardware FFmpeg row.

36. **[ ]** **Linux hardware GL beyond shipped PRIME layouts** — other PRIME / multi-planar FFmpeg → GL paths (**Tier B** row **3** tail).
