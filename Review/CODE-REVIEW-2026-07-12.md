# MFPlayer / HaPlay Code Review - 2026-07-12

## Scope and method

Reviewed the first-party framework, HaPlay UI/desktop host, tests, tools, and build scripts. `External/`,
`Reference/`, generated output, `bin/`, and `obj/` were excluded. The supplied HaPlay logs and both UI-hang
dumps were inspected as incident evidence, not as source. The current tree contains about 1,217 relevant files
and 199k lines; the recent 2,379-line visualizer/effect/encode/stream change set received line-by-line attention,
and the surrounding ownership, clocking, dispatcher, compositor, router, persistence, and shutdown paths were
traced end to end.

This is a review artifact only. No source changes were made. The worktree was already dirty and the existing
staged deletion under `Review/` was not altered.

Severity: **H** = correctness, availability, or security issue to fix before relying on the feature in a show;
**M** = material robustness/performance/maintainability issue; **L** = cleanup or smaller edge case.

## Executive summary

The core playback/routing code remains well structured, and the new persistent visualizer source does preserve
projectM state across normal media-player track rebuilds. The mesh-warp/output-splitting path is also sensibly
organized: one canvas render, GPU warp stages, PBO readback fallback, explicit output ownership, and useful tests.

The UI-hang dump does **not** show the UI thread inside projectM. It shows an Avalonia native tooltip popup being
closed synchronously while Avalonia's render thread is stuck in GLX buffer swap. The projectM worker is waiting in
its normal frame pacer at the snapshot. A projectM preset may have triggered or amplified the GPU/driver condition,
but the dump cannot prove which preset or native projectM call caused it. The exact UI deadlock surface is the
native X11 popup teardown.

The most urgent code issues beyond that incident are:

1. Audio-effect wrappers erase hardware clock/capacity interfaces and compose ownership incorrectly.
2. Visualizer, encoder, and network-sink shutdown paths dispose state after a failed join while workers may still
   be executing native code.
3. Visualizer settings use mutually stale whole-file snapshots, so unrelated saves can revert them; the displayed
   "Match output" mode is actually hard-coded 1080p60.
4. Fixed encode FPS is advertised but not enforced, and the ProRes pixel-format selection is incompatible with
   the installed FFmpeg encoder.
5. Stream keys are persisted in project files and included in sink names/logs as full resolved URLs.

## UI-hang dump analysis

### Timeline

- `15:35:56.254`: continuous projectM starts with 552 presets at 1920x1080@60
  (`UI/HaPlay.Desktop/logs/haplay-20260712-143807.log:158`).
- The dispatcher heartbeat stops around `15:45:36`; the watchdog captures
  `haplay-uihang-20260712-154544.dmp` at `15:45:44` (`...log:711,814`).
- Audio routing continues normally during the freeze and reaches natural EOF at `15:47:29`
  (`...log:815-872`). There is no recovery heartbeat before the process ends.
- There is no `SLOW preset load` or `SLOW preset render` entry around the incident.

### Latest dump: proven stack chain

`haplay-uihang-20260712-154544.dmp` contains these relevant threads:

- **UI thread, OS tid 496813**: `Task.InternalWaitCore` ->
  `Avalonia.Controls.PresentationSource.Dispose` -> `TopLevel.HandleClosed` -> X11 popup cleanup ->
  `Popup.CloseCore` -> `ToolTip.Close` -> `ToolTipService` input handling.
- **Avalonia render thread, OS tid 496868**:
  `Avalonia.X11.Glx.GlxGlPlatformSurface.RenderTarget.Session.Dispose` while disposing the popup render target.
  JIT disassembly maps its managed return address to the third native call in that method,
  `GlxDisplay.SwapBuffers`; the native instruction pointer is sleeping in a libc futex. In practical terms, the
  render thread is stuck in `glXSwapBuffers`.
- **ProjectMRenderer, OS tid 508656**: waiting at
  `ProjectMOffscreenRenderer.cs:233` in its cancellation-aware frame pacer. It is not in preset load, projectM
  render, or `glReadPixels` at the instant of capture.
- Audio router and media workers remain alive, consistent with the log continuing through EOF.

The earlier `haplay-uihang-20260712-023917.dmp` has the same UI tooltip-close wait and GLX swap stall. In that older
build, the composition/projectM path was also blocked in synchronous `glReadPixels`. The dedicated renderer removed
the session/composition-thread dependency, but it did not remove the shared process/GPU-driver failure domain or
the X11 popup disposal wait.

### What this explains

The visualizer remaining visible and later changing preset is compatible with the dump: its dedicated thread is
independent and was no longer stuck when sampled. The current song also plays through because audio and session
workers do not depend on Avalonia's dispatcher.

The next song does not start because media-player end detection and playlist advancement are exclusively driven by
an Avalonia `DispatcherTimer` in `MediaPlayerViewModel.ShowSession.cs:1290-1360`. Once the dispatcher freezes, no
poll observes EOF and no playlist command is issued. `ShowSession` already has a `ClipNaturallyEnded` event
(`ShowSession.cs:504-509`), but the deck mapper does not request it and deck auto-advance is still UI-owned.

### Incident remediation

1. On X11, enable `Avalonia.X11PlatformOptions.OverlayPopups = true` in
   `UI/HaPlay.Desktop/Program.cs:48-52`. Avalonia 12.1 documents this option and the current default is false.
   Embedded tooltips/menus avoid creating and synchronously tearing down a separate native GLX render target,
   directly removing the wait shown in both dumps.
2. Offer a show-safe Linux mode with Avalonia's main UI on the software renderer. This isolates dispatcher chrome
   from GLX stalls at the cost of extra UI CPU; SDL/projectM output can remain OpenGL.
3. Treat projectM presets as untrusted native/GPU workloads. Run projectM in a helper process with a bounded
   heartbeat, shared-memory frame ring, and kill/restart/quarantine policy. A managed thread cannot cancel a native
   preset load, driver wait, or crash safely.
4. Log/persist the attempted preset **before** calling the native load, time `glReadPixels` separately, and retain
   the last few phase transitions. The current instrumentation cannot name a preset whose load never returns and
   does not time the most synchronization-heavy call.
5. Move deck natural-end/auto-advance ownership into a headless playback controller fed by
   `ClipNaturallyEnded`; post only observable state to Avalonia. This will not make a frozen UI usable, but unattended
   transport can continue and recover independently.

## High-severity findings

### H1. A dedicated projectM thread is not a native/GPU isolation boundary

Files: `ProjectMOffscreenRenderer.cs:91-264`, `SDL3OffscreenGlContext.cs:21-42`.

`projectm_load_preset_file`, `projectm_opengl_render_frame`, and synchronous `gl.ReadPixels` all execute in-process
against the same driver stack used by Avalonia and the output compositors. A dedicated context/thread prevents the
session dispatcher from directly calling projectM, but a driver-global lock, GPU reset, native deadlock, or native
memory fault can still stall or terminate the whole process. The latest hang occurred with this dedicated renderer
already active.

Use a helper process for the robust boundary. A practical protocol is: configuration + preset commands over IPC,
PCM in a bounded shared ring, triple-buffered BGRA frames in shared memory, heartbeat/current-phase metadata, and a
supervisor that kills the worker and blacklists the last attempted preset after a deadline. A preflight worker can
also compile/render each preset for several frames before admitting it to the live rotation.

### H2. Visualizer shutdown can block the UI and then leave a live worker using disposed state

Files: `ProjectMOffscreenRenderer.cs:308-313`, `ProjectMVisualSource.cs:177-183`,
`MediaPlayerViewModel.ShowSession.cs:147-158,271-279`.

`Dispose` cancels, synchronously joins for five seconds, ignores whether the thread exited, and always disposes the
CTS. Toggle-off and settings replacement call this synchronously from UI-affine code. A stuck native call therefore
freezes the UI for five seconds; if it lasts longer, the old renderer/context remains alive while its synchronization
primitive is disposed. Repeated changes can accumulate orphan threads and GL contexts.

There is also a last-writer race. Toggle-off disposes `_visualizerSource` **outside** `_visualizerApplyGate`. If an
earlier enable/apply is awaiting the session attach and has not assigned the field yet, toggle-off sees null; the
older operation then resumes, stores a live source even though `VisualizerEnabled` is false, and the queued detach
does not dispose it. Move creation, replacement, detach, and disposal under one generation-aware async lifecycle.
Never dispose worker-owned resources until exit is confirmed; if a native call cannot be stopped, quarantine/leak
that terminal worker or terminate its helper process rather than creating a use-after-dispose race.

The cue attach path has a smaller version of the same leak: if `SetCompositionVisualizerAsync` throws after source
creation, `CueShowSessionCoordinator.cs:747-776` catches without disposing the local source.

### H3. `AudioEffectOutput` removes the hardware clock and pacing contract

Files: `AudioEffects.cs:13-88`, `AudioRouter.cs:382-394`, `AudioRouter.Playback.cs:80-129`,
`MediaPlayerViewModel.ShowSession.cs:885-908`.

The wrapper claims capability forwarding, but implements only channel capabilities and flush. It erases
`IClockedOutput`, `IPlaybackClock`, `IAudioOutputPlaybackStats`, and `IAdaptiveRateWrappedOutput` from a PortAudio or
already-adapted inner sink. The outer metering wrapper can only forward interfaces it can see.

With a gain effect enabled on the main hardware output, `AudioRouter` no longer recognizes it as a real clock
master. It may wrap it as a non-master adaptive output, will not slave the media clock to hardware playback, and
its pump cannot call the hardware capacity wait through the wrapper. This can cause wall-clock pacing, drift,
unnecessary resampling, and output overruns specifically when an effect is inserted.

Use the same factory/subclass pattern as `MeteringAudioOutput.Wrap` and `ResamplingAudioOutput.Wrap`, or introduce a
single tested capability-preserving decorator abstraction. Add tests asserting every capability before/after each
decorator permutation.

### H4. Audio-effect lease ownership leaks either the terminal output or the effect chain

Files: `AudioEffects.cs:10-11,80-88`, `MeteringAudioOutput.cs:49`,
`MediaPlayerViewModel.ShowSession.cs:889-908`, `CueShowSessionCoordinator.cs:44-67`,
`ShowSession.cs:127-149`.

`AudioEffectOutput.Dispose` owns only effects and deliberately does not dispose its inner. The deck puts a meter
around it, then copies the original lease's `DisposeOutputOnRuntimeDispose` flag unchanged:

- For a session-owned backend output, disposing the meter reaches the effect wrapper but never the owned terminal
  device, so the device leaks.
- For a borrowed carrier, the session does not dispose the outer wrapper at all, so its effect instances leak.
- In the cue encode rate-mismatch path, disposing `ResamplingAudioOutput` also does not dispose the inner effect
  wrapper, so effects leak there too.

Model wrapper ownership separately from terminal ownership. The session should always retire its per-attachment
decorators, while the lease independently says whether to dispose or release the terminal sink. A composable
`AudioOutputLease`/owner stack is safer than copying one boolean through decorators.

### H5. Visualizer settings are not transactionally persistent and their UI semantics are false

Files: `AppSettings.cs:15,57-64,90-169`, `MainViewModel.cs:116,445-487`,
`DialogStatePersister.cs:19-64`, `MediaPlayerViewModel.ShowSession.cs:39-127`,
`VisualizerSettingsDialog.axaml.cs:20-55`.

`AppSettings.Save` locks individual writes, but every writer serializes an entire object snapshot. `MainViewModel`,
`DialogStatePersister`, and the visualizer each cache or load distinct instances. A later sidebar/window/dialog save
can therefore overwrite freshly saved visualizer values with stale zeros, even though the JSON file is never
corrupt. Fire-and-forget visualizer writes can also complete out of order after rapid edits.

The default fields are zero and the dialog labels zero as "Match output", but
`ResolveVisualizerRenderSize` maps zero to fixed 1920x1080@60. The Match Output button therefore does not match an
output. A new settings file also does not contain the requested explicit default. The static
`VisualizerSettingsSeed` never refreshes, so later-created decks can inherit stale launch-time values. Direct
numeric edits are persisted only by the Close button handler; closing via the title-bar X bypasses it. Partial
width/height values produce a mixed custom/default raster while the label still says Match output.

Replace the snapshots with one app-scoped `AppSettingsStore` and a serialized/debounced `Update` operation. Make
1920/1080/60 the actual model defaults and save them on first settings creation. Either implement a real
match-output mode as a distinct enum or remove the zero sentinel. Persist from view-model changes/closing, not one
specific button path.

### H6. Encoder and async network-sink disposal can free native state under live threads

Files: `FFmpegEncodeSession.cs:501-535`, `AsyncPacketSink.cs:100-115,207-226`,
`MuxPacketSink.cs:65-218`, `FileOutputRuntime.cs:78-104`, `LiveStreamSession.cs:281-306`.

After bounded joins expire, `FFmpegEncodeSession.Dispose` still disposes codec contexts, packet sinks, queues,
events, and CTS while its worker may be inside FFmpeg. `AsyncPacketSink` similarly joins a potentially blocked
network drain thread, then frees queued AVPackets, disposes the muxer/AVIO context, and disposes its wake event
without checking `thread.IsAlive`. `Finish` also returns after its ten-second join without reporting that the drain
thread remains alive.

This can turn a stalled encoder/network destination into native use-after-free or a process crash during stop. It
also contradicts the safer terminal-stuck policy used by the router/player pumps. Install FFmpeg interrupt callbacks
and protocol I/O deadlines where possible. If a worker remains alive, mark the object terminally stuck and retain
its native/synchronization state; for truly untrusted network/native paths, a helper process is the only forceful
stop boundary.

### H7. A configured fixed FPS is only metadata; no frame-rate conversion occurs

Files: `FfmpegVideoEncoderCore.cs:14-15,51-56,187-190`,
`FFmpegEncodeSession.cs:235-266,372-451`.

`VideoEncodeOptions.Fps` sets only `AVCodecContext.framerate`. Every submitted frame is still encoded, using its
incoming presentation time plus a monotonic clamp. `ConfigureVideo` even replaces `_videoFrameDuration90k` with a
new input cadence after a source switch. Therefore 60 fps input configured as 30 fps still encodes roughly 60
frames/s, while 30 fps input configured as 60 remains roughly 30/VFR. Live streams do not have the locked cadence
the validation and UI promise, and `EncodedFormat` continues to report the source rate.

Add a target-timebase scheduler before encoding: select/drop input frames for each target tick and duplicate/hold
the last frame when input is slower. Keep target duration derived from options for the whole session and expose the
target rational in `EncodedFormat`. Add 60->30, 30->60, jitter, and mid-track-rate-switch timestamp tests.

### H8. ProRes pixel selection chooses formats the real encoder rejects

Files: `FfmpegEncodeMaps.cs:129-166`, `FfmpegVideoEncoderCore.cs:173-233`.

On this host, FFmpeg 8.1.2 reports `prores_ks` accepts `yuv422p10le`, `yuv444p10le`, and `yuva444p10le`. The mapping
returns 8- or 12-bit YUV422 unchanged for ProRes 422, and always chooses 12-bit YUV444/YUVA444 for ProRes 4444.
Those paths reach `avcodec_open2` with unsupported formats and fail when the operator selects the new codecs.

Select from the chosen encoder's advertised `pix_fmts` rather than a codec-name assumption. At minimum, use
YUV422P10 for 422 and YUV444P10/YUVA444P10 for 4444 on `prores_ks`. Validate CRF ranges per codec (the new global
0..63 range now admits invalid x264/x265 values), check every `av_opt_set` return code, and exercise every exposed
codec/container choice when that encoder is available.

### H9. Stream credentials are displayed, persisted, and logged as clear text

Files: `AddLiveStreamOutputDialog.axaml:42-49`, `OutputDefinitions.cs:219-247`,
`LiveStreamSession.cs:21-36,166-177`, `MuxPacketSink.cs:41-45`, `AsyncPacketSink.cs:37,97,168-187`.

The stream key uses a normal `TextBox` and is stored directly in the project JSON. `PushTarget.ResolveUrl` folds the
key into the URL, then `MuxPacketSink.Name` returns that full URL. Sink metrics and warning/error logs consequently
contain the credential. URL-embedded usernames/passwords have the same problem, and UI-hang dumps may contain all
of these strings.

Use a masked/reveal control, persist a secret-store reference (or at least an explicitly protected per-machine
secret) instead of the value in a shareable project, and give sinks a redacted display name. Never put resolved
credential URLs in exception/status/log messages. The existing REST API token stored in `AppSettings` deserves the
same secret-store treatment.

## Medium-severity findings

### M1. Continuous visualizer transfer cost is very high and its most likely stall is uninstrumented

Files: `ProjectMOffscreenRenderer.cs:37-39,162-163,205-233`,
`ProjectMFrameBlitSurface.cs:42,67-84`.

At 1080p60, each frame performs synchronous GPU readback, an 8.3 MB copy into `_latestFrame`, another copy into the
surface upload buffer under the same lock, and a full texture upload. That is approximately 2 GB/s of combined
GPU/CPU transfer and memory-copy traffic; 4K60 is about 8 GB/s. The frame lock is held across the large managed
copies. This raises the probability and duration of driver stalls exactly where UI GL is sharing the device.

Use double/triple buffers with atomic publication, PBO/fence readback (as the main compositor already does), and a
bounded consumer rate. Shared GL textures plus fences can remove the round trip in-process if context-sharing is
made explicit and reliable; a helper process can publish a shared-memory ring without the extra locked copy.

Only `projectm_opengl_render_frame` is timed. `gl.ReadPixels` (`ProjectMOffscreenRenderer.cs:218-223`) is not, so a
deferred shader/GPU stall produces no `SLOW preset` warning. Set `CurrentPresetName` and a persisted "attempting"
marker before native load, and time load, render, readback, and publish separately.

The absolute-start pacing at `:231-234` also tries to "catch up" every missed frame after a long stall, running an
unpaced burst that can hammer the GPU. Reset the next deadline when late instead of replaying missed render ticks.

### M2. Continuous-mode initialization is an accidental lazy side effect and failed setup renders black

Files: `MediaRuntime.cs:81-120`, `ProjectMVisualSource.cs:22-29,60-83,139-144`,
`ProjectMOffscreenRenderer.cs:99-104`.

The offscreen factory is assigned only when `MediaRuntime.Buses` is first evaluated; `MediaRuntime.Initialize`
eagerly builds only `Host`. HaPlay currently tends to touch the static effect choices before creating a visualizer,
but this is an implicit ordering dependency. Framework consumers can silently get legacy in-composition mode. The
factory is also mutable process-global state, contrary to the registry's injected design and awkward for parallel
tests/multiple hosts.

If the factory exists but returns null asynchronously, the source still has a non-null renderer, reports
`IsContinuous=true`, and creates a blit surface. It never falls back to `ProjectMGlLayerSurface`, despite the comment
in `MediaRuntime.cs:94-99`; the UI can report "running" while output remains black.

Inject a renderer/context provider when constructing the source, initialize it deterministically, and expose an
async ready/fault state. Decide explicitly between fallback and a surfaced failure before attaching the layer.

### M3. projectM/surface native and GL resources have no GL-thread retirement path

Files: `ProjectMGlLayerSurface.cs:52-107,313`, `ProjectMFrameBlitSurface.cs:152`,
`ShowSession.cs:323-360`.

Legacy `ProjectMGlLayerSurface.Dispose` only marks the surface disposed and never calls `projectm_destroy`; destroying
the GL context does not free the projectM native object itself. Failure after `projectm_create` leaks it too. Blit
surfaces likewise leave texture/program/VAO objects alive until the entire composition context dies. Replacing
settings repeatedly on a still-live composition can accumulate those objects. If a replacement attach fails,
`SetCompositionVisualizerAsync` has already removed the prior working visualizer.

Add a compositor-owned deferred GL-thread retirement queue/API. Stage/validate a replacement before removing the
old slot, and give every partially constructed source/surface a rollback path.

### M4. Deck progression is UI-owned rather than transport-owned

Files: `MediaPlayerViewModel.ShowSession.cs:1290-1366`, `MediaPlayerShowMapper.cs:53-90`,
`ShowSession.cs:504-509,1356-1521`.

This is the direct reason the frozen UI did not advance after audio EOF. Polling every 250 ms also duplicates the
session's natural-end machinery and adds transient-seek heuristics. Set `NotifyNaturalEnd` for finite deck media,
subscribe a non-UI playback controller to `ClipNaturallyEnded`, and serialize playlist decisions there. Avalonia
should receive state notifications, not own the transport clock. A small watchdog on the playback controller can
also distinguish "UI unavailable" from "session unavailable" operationally.

### M5. Effect-chain hot swap disposes objects while another thread may still process them

Files: `AudioEffectBus.cs:40-55,77-86`, `AudioEffects.cs:34-47,57-75`,
`VideoEffectBusOutput.cs:36-55,72-95`.

Each `SetEffects` atomically exchanges the array and immediately disposes removed effects. A pump/read thread can
still be executing the old array. The implementation comment asks effects to tolerate `Process` racing `Dispose`,
but that requirement is absent from the public contracts and is unsafe for effects owning native/DSP/GPU state.
Use an in-flight epoch/reference count or retire old chains on the processing thread after a quiescent boundary.

`GainAudioEffect.Configure` computes a ten-millisecond step per **frame**, but `Process` advances it per interleaved
float (`AudioEffects.cs:129-150`). Stereo ramps in about 5 ms, and 16-channel audio in about 0.625 ms. Advance once
per frame/apply the same gain to all channels. Clamp/reject NaN and non-finite configuration values.

### M6. Output reconfiguration resumes on a worker thread and mutates UI-bound state there

Files: `OutputManagementViewModel.cs:657-738,1282-1287`.

`ReconfigureLineAsync` uses `ConfigureAwait(false)` through handler/runtime awaits and then calls
`line.ReplaceDefinition`, raises topology events, and may invoke subsequent handlers off the Avalonia thread.
`OutputLineViewModel` property notifications and `ObservableCollection` consumers are UI-affine. Marshal the commit
and events explicitly to `Dispatcher.UIThread`, or keep the UI context at this boundary while native runtime work
runs off-thread.

### M7. Live-stream keepalive handoff is racy, and startup rollback can leak a session

Files: `StreamKeepAlive.cs:50-105`, `FFmpegEncodeSinks.cs:79-129`,
`LiveStreamOutputRuntime.cs:93-134`, `LiveStreamSession.cs:151-249`.

The keepalive checks volatile active flags and submits outside the runtime acquisition lock. One black/silent submit
can already be in flight when real playback acquires the same sinks. Video queue insertion is locked, but
`FFmpegEncodeCombinedAudioSink` reuses mutable `_legScratch` arrays with no synchronization, so the transition can
race and corrupt/split audio. Coordinate check+submit with acquire/release, or put filler selection at one serialized
mux/session input point; make the combined sink's concurrency contract explicit and tested.

`LiveStreamSession.Start` declares the encode session inside the `try`; if keepalive construction/configuration
throws after `CreateWithSinks`, the `catch` disposes the sinks directly but cannot dispose the session/worker that now
owns them. Keep a session local outside the try and transfer ownership only on successful return.

Validation also accepts `LocalServer != null` with both TS and HLS disabled as a valid destination. With no push
targets this creates an encoder/keepalive with zero sinks and no server. Reject that configuration.

### M8. The LAN HTTP server needs bounded request handling and endpoint-correct sharing

Files: `HttpMediaServer.cs:29-50,61-105,122-243,303-350`,
`LiveStreamSession.cs:163-164,214,267-279`.

- Server pooling is keyed only by requested port, ignoring bind address. A loopback request can reuse an existing
  `IPAddress.Any` listener (or vice versa). Key by normalized endpoint and reject incompatible sharing.
- Request headers have an 8 KB cap but no deadline. Sixty-four slowloris clients can drip bytes forever and occupy
  all slots. Use a short linked cancellation deadline. The current read-then-increment client cap also oversubscribes
  under a connection burst; reserve the slot atomically before spawning the handler.
- The server normalizes/lowercases mount names, but `LiveStreamSession` retains the original name for status URLs.
  `"My Stream"` is served as `/mystream.ts` while the UI reports `/My Stream.ts`. Store `mount.MountName`.
- `HttpMount.BytesServed` is tracked, but `LiveStreamStatus` hard-codes zero. Expose it on `MountHandle`. HEAD HLS
  requests should not increment served bytes, and HEAD `/status` currently sends a body.
- Framework URL validation uses `StartsWith("rtmp")`/etc. Use `Uri.TryCreate` and exact schemes; validate port range
  and expose a loopback/selected-interface choice in the UI instead of always binding every interface.

### M9. TS history overflow produces a discontinuous stream for a new client

File: `TsFanOutBuffer.cs:87-110`.

Registration enqueues history from the last keyframe until the 256-chunk client queue fills, then breaks and adds
the client to live delivery. Contrary to the comment, this does not start "slightly later": it sends the beginning
of history, drops its middle/tail, then jumps to current live chunks. The decoder receives a corrupt discontinuity.
Snapshot/coalesce history into a bounded byte buffer, or select a later complete join boundary whose entire tail
fits before registering for live writes. Bound by bytes, not callback chunk count.

### M10. `preserveMatchingCompositions` is unused production complexity with an incomplete match contract

Files: `ShowSession.cs:570-704,2308-2355`, `ClipCompositionRuntime.cs:458-483`,
`CompositionPreservationTests.cs`; production usage search finds only an explicit `false` in
`MediaPlayerViewModel.ShowSession.cs:611-613`.

The persistent offscreen visualizer now survives full composition rebuilds, so no production caller opts into the
preservation machinery. If enabled, compatibility checks only id/raster/rate and skip the video-output factory.
Changed composition output mapping, name, output leases, or topology therefore retain stale live behavior from the
old document.

Remove this API and `ResetClockMaster` until a real caller needs it; that deletes a substantial fragile branch. If
kept, define full compatibility (including mapping/output topology) and test output replacement/release semantics.

### M11. Watchdog dumps need retention and ptrace cleanup

File: `UiHangWatchdog.cs:209-293`.

The comments say a normal dump should be a few MB, but the two supplied captures are 370 MB and 379 MB (the dump
type used for those runs is not recorded in the log).
There is no dump retention or total-byte cap, so recurring hangs can fill the show machine's disk. Prune by count
and total size, log the resulting file size, and keep large dump capture configurable.

On Linux, `PR_SET_PTRACER_ANY` is enabled before capture and never restored. Reset it after the child exits (including
failure/timeout paths), especially because process memory can contain stream/REST credentials.

### M12. Removing one value-equal effect removes every duplicate insert

File: `OutputLineViewModel.cs:147-154`.

The filter excludes both reference-equal and record-value-equal entries. Two identical gain stages are valid ordered
inserts, but removing one removes both. Remove by stable row id/index or exactly one reference occurrence. Add
reorder/edit support if the chain is intended to be a real insert rack rather than an append-only proof of concept.

### M13. MMD bake cancellation has a completion-vs-cancellation race

Files: `MMDBakedPhysics.cs:319-348`, `MMDBakeTests.cs:127-155`.

The Release full-suite run failed
`BakeCache_EvictsCompletedBakes_AndSurvivesCallerCancellation`: an already-cancelled caller received a result instead
of `OperationCanceledException`. `BakeAsync` starts/joins the shared task and then calls `shared.WaitAsync(token)`.
If the small bake completes before that call, task completion wins over the pre-cancelled token; the disk-cache fast
path ignores the token entirely. The isolated rerun passed, confirming timing-dependent behavior rather than a
stable assertion result.

Choose and enforce one contract. The current test/documentation expects a pre-cancelled caller to cancel, so call
`cancellation.ThrowIfCancellationRequested()` before the cache and task fast paths. If an already available cached
result is intentionally allowed to win, update both the XML contract and test and add explicit tests for both
pre-cancelled cached and in-flight cases.

## Low-severity findings and cleanup

### L1. Release build has seven obsolete Avalonia bindings

`dotnet build -c Release` reports `AVLN5001` for `TextBox.Watermark`; Avalonia 12.1 expects `PlaceholderText`:

- `AddFileOutputDialog.axaml:114,116`
- `AddLiveStreamOutputDialog.axaml:42,48,73,147,149`

These should be cleared so release warnings remain actionable.

### L2. projectM build inputs are not reproducible or retry-safe

File: `scripts/build-projectm.sh:18-22,30-43,63-74`.

Both Git clones fetch the current default branch with no pinned commit. A failed partial clone can leave its target
or `.tmp` directory and make later runs fail; the fallback then hides that the desired pack was not installed. The
script also mutates the vendored `Reference/projectm-4.1.6` source in place. Pin revisions, clone to cleaned temporary
directories with a trap, verify the patch/version, and patch a build copy. Linux RID detection always emits
`linux-x64`, even on arm64, while the runtime resolver correctly probes `linux-arm64`.

### L3. Small API/comment drift

- `MediaPlayerViewModel.ShowSession.cs:536-538` says to use the cue player for continuity, while the following code
  implements persistent deck continuity. `_visualizerSource` is also described as session-owned at `:138-139`, but
  the deck owns it.
- Cue visualizer FPS uses integer division at `CueShowSessionCoordinator.cs:751-755`; 60000/1001 becomes 59 rather
  than 59.94. Preserve/round the rational consistently.
- `HttpMount.Refs` (`HttpMediaServer.cs:16`) is unused.
- `ServeStatusAsync` ignores HEAD semantics, and the 405 body says "GET only" although HEAD is accepted.

### L4. Public bus factory methods do not isolate factory failures

File: `BusRegistry.cs:107-122`.

Methods named `TryCreate*` return false for unknown kinds but allow a registered factory exception to escape. A
partially built chain in `OutputManagementViewModel.BuildAudioEffects/BuildVideoEffects` can then leak the effects
already created. Either document that exceptions propagate and roll back partial chains, or make `TryCreate*`
actually isolate failure and report a diagnostic.

## Simplification opportunities

1. **One app settings service.** Centralize the in-memory model, updates, background flush, backup, and migration.
   This removes every stale-snapshot race and most synchronous disk I/O from the dispatcher.
2. **One composable output lease.** Represent per-attachment wrappers, terminal ownership, release hooks, and
   capability forwarding explicitly. Use it for meters, effects, resamplers, NDI/file/stream carriers, and router
   adaptive wrappers.
3. **A persistent playback controller per deck.** It should own `ShowSession`, output leases, visualizer worker,
   natural-end progression, and lifecycle generations. `MediaPlayerViewModel*.cs` is about 5,193 lines; moving the
   transport/runtime state out would remove many fire-and-forget races while leaving the VM responsible for
   observable UI state.
4. **An output runtime registry service.** `OutputManagementViewModel` is 1,692 lines and mixes dialogs,
   observable rows, runtime dictionaries/locks, lease acquisition, arm/stream operations, health, and persistence.
   Move runtime ownership and thread-safe lookup into a headless service; keep the VM as commands/projections.
5. **Delete unused composition preservation.** Full rebuild + caller-owned persistent visualizer already satisfies
   deck continuity with a smaller contract.
6. **Share the GL readback machinery.** The compositor already has PBO/fence and fallback code. Extract a reusable
   owner-thread readback ring rather than maintaining a synchronous projectM path.

## Verification and test gaps

Completed:

- `git diff --check`: clean.
- `dotnet build MFPlayer.sln --no-restore --nologo`: succeeded, 0 errors.
- `dotnet build MFPlayer.sln -c Release --no-restore --nologo`: succeeded, 0 errors, 7 obsolete-XAML warnings.
- Debug `dotnet test MFPlayer.sln --no-build --nologo --blame-hang-timeout 3m`: 1,801 passed, 9 skipped,
  0 failed.
- Release full-suite verification: 1,800 passed, 9 skipped, 1 timing-dependent MMD cancellation failure (M13).
  The failing test passed when immediately rerun in isolation, which is itself evidence of the race.
- Managed stacks from both dumps, plus JIT/native-call-site inspection of the latest GLX render-thread frame.

The suite is broad, but several new risks are structurally untested. Some environment-dependent tests also return
early and appear as passed rather than skipped. Add focused coverage for:

- Two real deck track rebuilds reusing one running projectM source/preset timeline.
- Helper-process timeout, kill, restart, and last-preset quarantine with a deliberately blocked fake native worker.
- Audio decorator capability preservation and owned/borrowed terminal/effect disposal in every wrapper order.
- Concurrent/stale settings writers and explicit 1920x1080@60 first-run persistence, including title-bar close.
- Fixed-FPS timestamp/frame-count conversion and every exposed codec's supported pixel-format intersection.
- Async sink/encoder join timeout without disposing live worker state.
- Keepalive-to-playback handoff stress on combined multi-track audio.
- No-destination local-server validation, normalized mount status, HEAD, slow headers, client-cap races, and TS
  history larger than one client queue.
- Credential redaction in metrics, exceptions, logs, and serialized projects.

## Patterns worth retaining

- Immutable router snapshots, bounded per-output pumps, format-versioned queues, and explicit clock-master policy.
- Validate/stage-before-commit document loading and per-route failure isolation.
- GPU-integrated output mapping/mesh warp with one canvas render, PBO fallback, and deferred driver-thread cleanup
  for mapping compositors.
- Caller-owned persistent visualizer source across full document rebuilds; this is the simpler continuity mechanism.
- The UI watchdog and rolling logs were decisive in separating dispatcher, audio, session, and renderer behavior.
  Keep them, with the retention/security changes above.

## Recommended order

1. Enable X11 overlay popups; add a software-UI launch mode; expand phase/preset diagnostics.
2. Fix audio-effect capability forwarding and lease ownership before using effects in production playback.
3. Replace settings snapshots and make 1920x1080@60 the explicit persisted default.
4. Move projectM to a supervised helper process and make all failed-join paths terminally safe.
5. Implement real frame-rate conversion and encoder pixel-format negotiation; add codec/cadence tests.
6. Redact/move secrets, then harden stream teardown, keepalive handoff, HTTP request bounds, and TS history.
7. Remove unused preservation code and split runtime ownership out of the large view models.
