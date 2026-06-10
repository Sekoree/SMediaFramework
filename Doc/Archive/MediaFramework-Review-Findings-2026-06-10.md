# MediaFramework + HaPlay review findings — 2026-06-10

> **Implementation status (updated 2026-06-10, same day):** most P1/P2 findings below are now
> **fixed** — see §9 "Implementation log" at the end for exactly what changed and what was
> deliberately deferred (F4 hardware mid-stream fallback, F5 network read retry, C6 composition
> fan-out copies, parallel standby preparation).

Deep review of the framework (FFmpeg demux/decode, Playback tier, Core engines) and the HaPlay
cue-player/playback paths, focused on the current priorities:

1. **Framework stability / reliability with any format**
2. **Stream selection for multi-stream containers (MKV)** — framework + UI
3. **Cue player: fast starts and tight sync**
4. **UI playback should start when at least one stream is playable and an output exists for it**

Files reviewed in depth: `MediaContainerSharedDemux.cs`, `MediaContainerDecoder.cs`,
`MediaContainerSession.cs`, `MediaPlayer.cs`, `MediaPlayerOpenBuilder/Options`, `CueGraph.cs`,
`ClipStandbyEngine.cs`, `ClipCompositionRuntime.cs`, `ClipAudioOutputRuntime.cs`, `MediaSession.cs`,
`AvPlaybackCoordinator.cs`, `AudioRouter.cs` (route/run-loop surface), `VideoPlayer.cs` (presentation
surface), `HaPlayPlaybackSession.cs`, `CuePlaybackEngine.cs`, `CueAudioSourceAdapters.cs`,
`CuePlayerViewModel.cs` (transport surface). Line references are to the state of the tree on 2026-06-10.

Legend: **P1** = fix soon (stability/correctness), **P2** = should fix (robustness/perf), **P3** = nice
to have / design improvement.

---

## 1. Top findings at a glance

| # | Pri | Area | Summary |
|---|-----|------|---------|
| F1 | P1 | FFmpeg open | Broken/unsupported **audio** stream fails the whole open — no audio→video-only degrade (video→audio-only degrade exists) |
| S1 | P1 | Stream selection | No stream enumeration or selection API at any layer; MKV multi-track files always play `av_find_best_stream`'s pick |
| C1 | P1 | Cue engine | Cue `Play()` runs on the **UI thread** and can block (prefill + video-buffer wait, worst-case 8 s); group fire starts cues sequentially on that thread |
| U1 | P2 | UI session open | One unavailable NDI carrier aborts the whole `HaPlayPlaybackSession.TryCreate`, while local-video acquire failures are skipped — inconsistent "play what you can" policy |
| U2 | P2 | Cue validation | One invalid audio route fails the whole cue (`ValidateRoutePlan`) instead of degrading to the playable routes |
| F2 | P2 | FFmpeg decode | Mid-stream **audio** format changes (sample rate / layout / sample format) are not tracked — swr is configured once at open |
| F3 | P2 | FFmpeg decode | Mid-stream **video** dimension changes are not tracked — sws context uses open-time dimensions (corruption/OOB risk on dimension-switching streams) |
| F4 | P2 | FFmpeg decode | No software re-fallback when **hardware decode fails mid-stream** (only at open) |
| C2 | P2 | Cue audio | `PausableAudioSource` / `AudioSourceFanout.Branch` don't forward `ICooperativeAudioReadInterrupt` — router can't interrupt cue-path decoder reads, so stops/seeks are slower |
| C3 | P2 | Cue loop | Loop wrap is a 150 ms poll + full seek — audible/visible gap at the loop point |
| M1 | P2 | Perf | Audio-only cues on video files still decode the full video stream into a discard sink (no "video off" open option) |
| C4 | P3 | Framework cue API | `ClipStandbyEngine.StartGroupAsync` starts clips sequentially without the paused-audio barrier HaPlay implements — framework consumers get worse group sync than the app |
| C5 | P3 | Standby | `RefreshStandbyAsync` prepares clips serially; sync-over-async disposals in `StorePrepared`/`ReturnPrepared` |
| F5 | P3 | FFmpeg demux | Transient `av_read_frame` errors fault the demux thread permanently (treated as EOF) — matters for network/RTSP inputs |
| F6 | P3 | FFmpeg open | Frame-rate normalization caps at 120 fps → high-fps content (144/240) is declared 30 fps |
| C6 | P3 | Composition | `PumpOneFrame` deep-copies the canvas frame per extra output (N−1 CPU clones/tick) |

What already works well is listed in §7 so it doesn't get "fixed" by accident.

---

## 2. Format robustness / framework stability

### F1 (P1) — No audio→video-only degrade on open

`MediaContainerSharedDemux.OpenInternalAfterFormatOpen` (MediaContainerSharedDemux.cs:280):

- The **video** setup block is wrapped in `try/catch (… ) when (_hasAudio)` and degrades to
  audio-only via `ConfigureNoVideoStubAfterVideoSetupFailure` (line 376/529). Good.
- The **audio** setup block (lines 300–327: `avcodec_open2`, `swr_alloc_set_opts2`, `swr_init`) is
  *not* wrapped. Any failure — unsupported audio codec build, corrupt codec parameters, an
  unmappable/UNSPEC channel layout that makes `swr_init` fail — throws and the **whole file refuses
  to open**, even when the video stream is perfectly decodable.

This is the single biggest gap against the "play if at least one stream is playable" goal. The fix is
symmetric to the video path: on audio-setup failure with `_hasVideo == true`, release `_aCtx`/`_swr`,
set `_hasAudio = false`, set the sentinel `AudioFormat(0,0)`, log a warning, and continue. Only throw
when both sides are unusable (the existing line 296 check already covers "neither decodable").

Note the related sub-case: `av_channel_layout_default(&outLayout, N)` (line 316) produces an
unspecified layout for channel counts without a standard default (e.g. some 22.2 or odd multichannel
WAVs); `swr_alloc_set_opts2` can then fail. With the degrade in place this becomes "video plays,
audio warns" instead of "file does not open".

### F2 (P2) — Mid-stream audio format changes are not tracked

The video side handles decoder format drift: `SyncVideoPixelFormatIfNeeded` (line 1719) re-derives
the pixel format and rebuilds sws when the first/changed frame disagrees with open-time state. The
audio side has no equivalent: `_swr` is built once from `_aCtx` at open (line 318) and every
`swr_convert` call passes `_aFrame->extended_data` assuming the open-time sample format, layout, and
rate. `TryAdvanceAudioTowardTarget` even acknowledges drift exists (`_aFrame->sample_rate > 0 ? … :`
line 1303) but the convert paths don't.

Real-world triggers: HE-AAC SBR streams that double their effective rate after the first frame on
some decoder builds, DVB/TS captures with mid-stream parameter changes, concatenated files. The
result is garbage audio or a native crash inside swr.

Suggested fix: before each `SwrConvertInto`/`ConvertAudioFrame`, compare
`_aFrame->format/sample_rate/ch_layout` against the swr configuration; on mismatch, drain swr,
`swr_free` + re-`swr_alloc_set_opts2` from the frame's parameters (keeping the *output* format
fixed at `Audio.Format`), and continue. That keeps the public `AudioFormat` stable so the router
graph doesn't need to renegotiate.

### F3 (P2) — Mid-stream video dimension changes are not tracked

`_swsCtx` is created with `_vCtx->width/height` at open (line 521) and again in
`SelectVideoOutputFormatLocked` (line 1798). `SyncVideoPixelFormatIfNeeded` only reacts to pixel
*format* changes. A stream that changes dimensions mid-file (DVB recordings, some MKV edits,
adaptive captures) feeds frames of a different geometry into an sws context configured for the old
one — at best corruption, at worst out-of-bounds reads in `sws_scale` (it trusts the configured
source dims, not the frame's).

Suggested fix: in the decode path (`BuildConvertedVideoFrame` / `SyncVideoPixelFormatIfNeeded`),
compare `workFrame->width/height` to the configured source dims; on change either (a) rebuild sws
with the new source dims while scaling into the unchanged `Video.Format` canvas (cheap, keeps the
graph stable — recommended), or (b) at minimum drop the frame with a rate-limited warning instead of
calling sws with mismatched geometry.

### F4 (P2) — No software fallback when hardware decode fails mid-stream

Hardware → software fallback exists only at `avcodec_open2` time (line 414). If VAAPI/D3D11 decode
starts fine but a specific file trips the driver mid-stream (`avcodec_receive_frame` returning a
hard error, or `av_hwframe_transfer_data` failing inside `TransferToScratch`), the exception
propagates out of `VideoTrack.TryReadNextFrame` and playback of that file is dead, even though
software decode would handle it. Given the "any format" goal and the variety of show-machine GPUs,
consider: on the first hard decode error from a hardware context, log, tear down `_hwAccel`, reopen
the codec in software at the current position (the seek-prime machinery can be reused), and continue.
Even a simpler "demote this session to software and surface a `DecodeDegraded` flag" beats a dead
video track mid-show.

### F5 (P3) — Demux thread treats every read error as terminal

`DemuxerThreadProc` (line 756): any `av_read_frame` error other than EOF throws, faults the thread
(`_demuxFault`), and consumers see EOF. For local files that's right. For network inputs
(`OpenUri` → http/rtsp), transient `AVERROR(EAGAIN)`/timeout/`5xx` hiccups permanently end playback.
If network playback matters, retry transient codes a few times with backoff before declaring the
fault (the interrupt callback already gives you clean cancellation).

### F6 (P3) — Frame-rate normalization caps at 120 fps

`NormalizeVideoFrameRate` (line 637) clamps `d < 0.05 || d > 120` to 30 fps. A legitimate 144/240 fps
file is declared 30 fps — presentation still follows PTS, but anything sized or paced from the
declared rate (jitter buffer seconds, NDI clocked sends, composition canvas defaults) is wrong by up
to 8×. Consider raising the cap to ~300 and only flooring the truly bogus values (e.g. 90000/1
timebase artifacts), or clamping *to the cap* rather than to 30.

### F7 (P3) — Open-time knobs for slow/odd containers

`avformat_find_stream_info(_fmt, null)` (line 229) runs with defaults. Two cheap robustness wins:

- For very large/odd files (long MKV with many streams), expose optional
  `probesize`/`analyzeduration` overrides on `VideoDecoderOpenOptions` — both faster opens for known
  content and deeper probing for pathological content as an explicit choice.
- `avformat_open_input` gets no options dictionary; an `err_detect`/`fflags discardcorrupt` toggle is
  a useful "tolerant mode" for damaged files.

### F8 (informational) — Unconsumed-stream back-pressure invariant

The demux thread blocks in `EnqueuePacketCopy` when a per-stream queue is full (line 819). The
elected audio and video streams must therefore *both* be consumed, or the other side starves after
~192 audio / ~384 video packets. The framework already protects the common paths (MediaPlayer always
routes decoder audio to a `DiscardingAudioOutput` when the router is on, MediaPlayer.cs:830;
HaPlay's `BuildFileClipSource` keeps `IncludeAudioRouter: true` exactly when no external audio
mixing exists, CuePlaybackEngine.cs:512). This invariant is easy to violate from new host code
though — worth one prominent paragraph in `MediaFramework-PublicAPI.md`, and ideally a debug
diagnostic: if the demux thread has been blocked on a full queue for > N seconds while the other
track keeps being read, log a warning naming the unconsumed track.

---

## 3. Stream selection for multi-stream containers (S1, P1 feature)

Current state — there is **no selection at any layer**:

- `MediaContainerSharedDemux` elects one audio stream via `av_find_best_stream` (line 285) and one
  video stream via `PickVideoStreamIndex` (line 612). All other streams' packets are silently
  dropped by the demux thread (line 784–787 only matches `_aStream`/`_vStream`).
- `VideoDecoderOpenOptions` / `MediaPlayerOpenOptions` have no stream-index fields.
- The UI never enumerates tracks (no track UI anywhere in `Models/`/`ViewModels/`).

For MKV/MP4 with multiple audio languages, commentary tracks, or multiple video angles, the user
gets whatever FFmpeg's default-disposition heuristic picks, with no recourse.

### Proposed design (incremental)

**Phase 1 — enumeration (framework).** Add a `MediaStreamInfo` record surfaced from the demux after
`avformat_find_stream_info`:

```
record MediaStreamInfo(
    int Index, MediaStreamKind Kind /* Audio|Video|Subtitle|Data */,
    string CodecName, string? Language, string? Title,
    int Channels, int SampleRate,            // audio
    int Width, int Height, Rational FrameRate, // video
    bool IsDefault, bool IsForced, bool IsAttachedPicture,
    bool IsDecodable /* avcodec_find_decoder != null */)
```

Read it straight off `AVStream->codecpar` + the `language`/`title` metadata entries — no decoding
needed. Expose as `MediaContainerDecoder.Streams` and add a static
`MediaContainerDecoder.ProbeStreams(path)` so the UI can fill pickers without holding a decoder.
HaPlay already probes files when building cue rows (CuePlayerViewModel.cs:3118–3131) — same place.

**Phase 2 — open-time selection (framework).** Add `AudioStreamIndex`/`VideoStreamIndex`
(`-1` = auto, current behavior; `-2`/`None` = explicitly disable the side, see M1) to
`VideoDecoderOpenOptions` and mirror them through `MediaPlayerOpenOptions.ToVideoDecoderOpenOptions`.
In `OpenInternalAfterFormatOpen`, when an explicit index is given: validate it is in range, of the
right type, and decodable; on validation failure fall back to auto with a logged warning (a missing
track must not make the cue fail mid-show — consistent with §2 F1). The attached-pic/sane-dimension
guards in `PickVideoStreamIndex` should still apply to explicit picks.

**Phase 3 — UI.** Per playlist item / media cue: an "Audio track" (and "Video track" where >1)
dropdown populated from `ProbeStreams`, persisted in `MediaPlayerConfig`/cue model as the stream
index plus the track's codec+language signature (so a re-muxed file with shifted indices falls back
to auto instead of silently picking the wrong track). Two integration points to not miss:

- `CuePlaybackEngine.BuildPreparedCueKey` (CuePlaybackEngine.cs:1597) must include the selected
  stream indices, otherwise a prepared standby decoder opened with the old track is reused after the
  operator changes tracks.
- The decoder cache in `MediaPlayerViewModel.OpenOrReload` (the `preOpened` path,
  MediaPlayerViewModel.cs:3170) must key on stream selection too.

**Phase 4 (optional) — runtime audio-track switching.** Switching without reopening is feasible
inside the existing seek machinery (under `_readSeekGate` write lock: stop demuxer, swap `_aStream`,
rebuild `_aCtx`+`_swr`, `SeekPresentation(current)` to re-prime). But the simple v1 — reopen the
decoder at the current position, exactly like the existing reload path — is fine for a cue tool and
avoids new locking surface. I'd ship phases 1–3 first.

Subtitles: enumerate them in Phase 1 (cheap) but defer rendering; that's a separate pipeline.

---

## 4. UI playback-start gating ("play what's playable")

What already works (worth stating so it's preserved):

- Video-unusable + good audio degrades to audio-only at the demux (§2 F1's existing half).
- Audio-only and video-only files play; the stub video source / sentinel audio format negotiate
  cleanly against `DiscardingVideoOutput` / the discard audio sink.
- `WireAudio` skips an unacquirable PortAudio line with a warning and keeps going
  (HaPlayPlaybackSession.cs:966); local-video acquire failures likewise `continue`
  (HaPlayPlaybackSession.cs:468).
- `TryAddOutput` rejects wiring an audio output to a no-audio source with a clear message rather
  than failing the session (HaPlayPlaybackSession.cs:1626).

Gaps:

### U1 (P2) — One missing NDI carrier aborts the whole session open

`HaPlayPlaybackSession.TryCreate` (HaPlayPlaybackSession.cs:439–447): if any selected NDI output
has no live carrier, the entire open is aborted — decoder disposed, error returned — even when
PortAudio + local video outputs were available and the media is fine. This is inconsistent with the
local-video policy (skip + continue) and against the "start if there is *an* output" goal. Suggest:
skip the dead carrier, collect a per-line warning list onto the session (the health/LED surface
already exists), and only fail when **zero** output lines could be wired.

### U2 (P2) — Cue route validation is all-or-nothing

`CuePlaybackEngine.ExecuteCoreAsync` (CuePlaybackEngine.cs:352–358): `ValidateRoutePlan` fails the
whole cue if *any* audio route targets a missing/incapable output or an out-of-range channel
(CuePlaybackEngine.cs:1288–1351). For a live show, a cue with 3 valid routes and 1 stale route
(operator deleted an output line yesterday) should fire the 3 and surface a warning. The framework
even has the vocabulary for this (`CueFaultPolicy.Continue` / `RouteToFallbackOutput` in
CueGraph.cs:6) but the UI engine doesn't consume it. Suggest: partition routes into valid/invalid
during planning, fire with the valid subset when at least one route (audio or video placement)
survives, and put the per-route failures into `StatusMessage`/cue log. Keep hard-fail only for
"nothing wired at all" (the existing `plan.HasAnyRoute` check).

### U3 (P3) — Stale per-cue stream metadata

Cue rows cache `SourceHasAudio/SourceHasVideo` from probe-at-add (CuePlayerViewModel.cs:3118–3131).
If the file is replaced on disk, validation decisions and `IncludeAudioRouter` selection
(CuePlaybackEngine.cs:512) run on stale flags: a file that gained audio keeps
`IncludeAudioRouter: false` with no audio routes → the new audio stream is unconsumed →
back-pressure stall (F8). Cheap guard: re-probe when file mtime/size changed; or make the demux
back-pressure diagnostic (F8) loud enough to catch it.

---

## 5. Cue player — fast starts and sync

### C1 (P1) — `Play()` runs on the UI thread and can block for seconds

Both fire paths hop **onto** the UI thread to start transports:

- Single: `ExecuteCoreAsync` → `Dispatcher.UIThread.InvokeAsync(() => { entry.StartPlayback(); … })`
  (CuePlaybackEngine.cs:401).
- Group: `ExecuteGroupAsync` does the same for every cue in sequence (CuePlaybackEngine.cs:316–330).

`StartPlayback` → `MediaPlayer.Play` → `AvPlaybackCoordinator.Play`, which (when the internal audio
router is in play, i.e. cues without external audio routes) performs `WaitForVideoBufferBeforeStartingAudio`
with an **8-second** deadline plus a 250 ms sync-present timeout (AvPlaybackCoordinator.cs:107,16).
Normal case is tens of ms, but a cold cache, a heavy 4K HEVC seek-prime, or a slow USB source can
hold the UI thread (and with it every other cue's start, MIDI feedback, meters) for seconds.

Suggestions:

1. Run `StartPlayback` on the thread pool; only the final `CueStarted` notification and VM mutations
   need the dispatcher. Audit what actually requires UI affinity in that block — `SetAudioPaused`
   and `EnsureAudioRuntimesStarted` don't look UI-bound.
2. For groups: start all video transports **in parallel** (Task per cue), then do the collective
   audio unpause (the actual sync barrier, which is already correct) once all `Play()` calls return
   or a per-cue timeout lapses. Today the serial starts make total group latency the *sum* of
   per-cue start costs.
3. Consider making the 8 s wait configurable per call; a cue fire probably wants ~1–2 s max with a
   "started late, video catching up" warning instead of a long stall.

### C2 (P2) — Cue audio path loses cooperative read interruption

`AudioRouter` collects `ICooperativeAudioReadInterrupt` from its *sources* to abort blocking decoder
reads during pause/stop (AudioRouter.cs:1232). The demux `AudioTrack` implements it, and
`ResamplingAudioSource` forwards it — but the cue path wraps decoder audio in `PausableAudioSource`
and `AudioSourceFanout.Branch` (CueAudioSourceAdapters.cs), neither of which implements/forwards the
interface. Net effect: stopping/seeking a cue can wait out a full blocking read
(`FeedAudioFromQueue`'s 50 ms waits, or worse during demux stalls) instead of yielding immediately —
this feeds directly into the known teardown-latency problem. Fix is mechanical: implement
`ICooperativeAudioReadInterrupt` on both wrappers, delegating to the inner source when it implements
it (same pattern as `ResamplingAudioSource`, ResamplingAudioSource.cs:67).

Related (lower priority): `AudioSourceFanout.ReadBranch` calls `_inner.ReadInto` while holding the
fanout gate (CueAudioSourceAdapters.cs:105–127), so when one cue source fans out to two output
runtimes, a slow decode on one router's pull blocks the other router's audio thread for that source.
Acceptable today; becomes a dropout source if fanout is used more heavily. A pull-ahead buffer filled
outside the lock would decouple them.

### C3 (P2) — Loop wrap has an audible/visible gap

`WatchNaturalEndAsync` polls position every **150 ms** and loops by `SeekActiveCueAsync(entry, 0)`
(CuePlaybackEngine.cs:1518–1538). Worst case the wrap triggers ~150 ms late, then pays a full
coordinated seek (pause → `avformat_seek` → prime → resume). For "background loop" cues (very common
in shows) that's a visible/audible hiccup every cycle. Options, cheapest first:

1. Tighten the poll as the end approaches (e.g. sleep `min(150ms, remaining/2)`).
2. Pre-arm the wrap: when `remaining < ~2 s`, prepare a second decoder seeked to `ClipWindow.Start`
   via the standby engine, and at the wrap point swap sources router-side (audio at a chunk boundary,
   video at the next tick). This reuses existing standby plumbing and gives near-gapless loops.
3. True gapless (sample-accurate splice in the router) is a bigger framework feature; (2) is likely
   good enough for show use.

### C4 (P3) — Framework `StartGroupAsync` lacks the barrier HaPlay built

`ClipStandbyEngine.StartGroupAsync` arms in parallel but then calls `clip.Start()` sequentially with
no collective audio gate (ClipStandbyEngine.cs:287–311) — each `Start` is a full `Play()` including
prefill, so framework consumers get starts staggered by the slowest predecessor. HaPlay's
`ExecuteGroupAsync` (wire paused → start all → unpause all together) is the better pattern; either
move that barrier *into* `StartGroupAsync` (Start with `prefillBeforeHardware` separated, audio
gated) or document that `StartGroupAsync` is not show-grade sync and point at the paused-wire
pattern. As is, the framework API silently under-delivers vs. the app.

### C5 (P3) — Standby engine details

- `RefreshStandbyAsync` prepares serially (`foreach` + `await PrepareAsync`,
  ClipStandbyEngine.cs:228); with a 4-cue warm window of heavy files the refresh latency stacks.
  A small parallel degree (2–3) keeps the UI's pre-roll snappy. Watch decoder-open CPU spikes.
- `StorePrepared` / `ReturnPrepared` block with `.GetAwaiter().GetResult()` on `DisposeAsync`
  (ClipStandbyEngine.cs:408, 520) — sync-over-async on whatever thread calls Arm/Refresh. Make these
  paths async (they're already in async flows) or fire-and-forget with logging.
- `RefreshStandbyAsync` has no re-entrancy guard; two overlapping refreshes can interleave
  remove/store for the same id. The UI probably serializes calls today, but an internal
  `SemaphoreSlim(1)` would make the engine safe by construction.
- `PrepareAsync` seeks with `CancellationToken.None` (ClipStandbyEngine.cs:362) — an aborted arm
  can't cancel a long in-flight seek-prime even though `CancelInFlightSeek` exists. Wire the token
  via `MediaContainerSession.SeekCoordinated`'s cancellation registration.

### C6 (P3) — Composition fan-out copies

`ClipCompositionRuntime.PumpOneFrame` clones the composited BGRA frame for every output except the
last (`VideoFrameCpuClone.DuplicateCpuBacking`, ClipCompositionRuntime.cs:339). A 1080p60 canvas to
3 outputs = ~2×8 MB×60/s of memcpy. The zero-copy fan-out view built for the async branch conversion
work (refcounted views over one backing) fits here directly.

### Cue sync — verified-good behaviors (no action)

- Group audio start is collectively unpaused after all transports start — the right barrier.
- Per-cue video is slaved to the cue's audio-runtime clock (`videoOnlyMaster`), and compositions take
  the first cue's master (`SetClockMaster` keeps the first, documented Phase 5.9 limitation).
- `RetimingVideoOutput` correctly rebases trimmed cues' PTS into cue-relative time
  (CuePlaybackEngine.cs:1182).
- Audio routes added under explicit route ids — `AudioRouter.AddRoute(routeId, …)` replaces in place
  with gain hard-reset (AudioRouter.cs:497), so `UpdateRoute`/`SetRouteGain` semantics are sound.

---

## 6. Other observations

### M1 (P2) — Audio-only cues fully decode the video stream

A file cue with only audio routes still builds the whole video pipeline: `MediaPlayer` always creates
a `VideoPlayer` over `decoder.Video` with a `DiscardingVideoOutput` lead, and on Play the decode loop
pulls/decodes/converts frames at full rate just to discard them. For a 4K source driving a
sound-only cue, that's most of a CPU core (or a hardware decode session) wasted, and it competes
with cues that *do* show video. The `VideoStreamIndex = None` option from §3 Phase 2 solves this for
free: with no elected video stream, the demux never queues video packets, and the existing stub-video
path (already used for audio-only files) takes over. `CuePlaybackEngine.BuildFileClipSource` would
pass `VideoStreamIndex: None` when `plan.Placements.Count == 0` — exactly mirroring what it already
does for audio with `IncludeAudioRouter`.

### M2 (P3) — `PausableAudioSource.IsExhausted` inverts after dispose

`IsExhausted => !_disposed && !IsPaused && _inner.IsExhausted` (CueAudioSourceAdapters.cs:26): a
*disposed* source reports "not exhausted" while returning 0 samples forever — to a router that
treats exhausted sources as removable, a disposed-but-still-registered source looks like a stalled
live source instead. Probably benign with current removal ordering, but the semantics should be
`_disposed || (…)`.

### M3 (P3) — `MediaSession.Own` registration order vs. docs

Doc and behavior agree (reverse-registration disposal after the player) — fine. Just noting that
`DisposeResourceSync` on an `IAsyncDisposable`-only resource blocks via `GetAwaiter().GetResult()`;
hosts that own encoders should prefer `DisposeAsync` (already documented in the XML remarks).

### M4 (P3) — `CueGraph.FireEntryAsync` recursion

Follow-on chains recurse (`FireEntryAsync` → `FireEntryAsync`, CueGraph.cs:220); a long
auto-continue chain (hundreds of cues) builds a deep async chain and the whole chain shares one
cancellation token with no per-hop loop-guard — a cue list with an accidental follow-on *cycle*
(A→B→A with AutoContinue) never terminates. Cheap guard: track visited cue ids per fire, or convert
to an iterative loop. (HaPlay's own engine doesn't use this path; framework consumers do.)

### M5 (P3) — `ClipAudioOutputRuntime` channel conventions

`AudioRouteSpec.SourceChannel` is 0-based while `OutputChannel` is 1-based
(ClipAudioOutputRuntime.cs:245–252). It's validated and error messages are clear, but it's a
recurring footgun for host authors; worth one highlighted line in the public API doc.

### M6 (P3) — `WatchNaturalEndAsync` posts progress through the dispatcher per active cue

Each active cue does a `Dispatcher.UIThread.InvokeAsync` every 150 ms (CuePlaybackEngine.cs:1530).
Fine at show scale (a handful of active cues); if cue counts grow (soundboard-style usage), batch
the progress updates into one dispatcher hop.

---

## 7. Things that look solid (keep as-is)

- **Seek machinery**: interleaved A/V prime with wall-clock deadline, cooperative cancellation
  (`CancelInFlightSeek`), coordinated-seek dedup keyed on "primed and unconsumed", keeper-frame
  pushback for audio, and `GetAlignedPresentationPosition`'s 250 ms spread guard. This is the most
  battle-hardened part of the demux and the comments capture the failure modes well.
- **Attached-picture handling**: no-HW-decode for covers, exhausted-latch after the single frame,
  odd-dimension rounding through the sws path, 30 fps declaration for NDI pacing.
- **Degrade-to-audio-only** for broken video streams (the existing half of F1).
- **Demux fault containment**: faulted reader thread → EOF semantics + `DemuxFault`, never a host
  crash; stuck-thread detection refuses unsafe native frees.
- **Back-pressure architecture**: bounded per-stream packet queues with pulse-based handoff; the
  discard-sink pattern keeping clocks advancing with zero outputs.
- **Present-newest-late + 16-frame file jitter buffer** in `VideoPlayer`, and the
  pause→clock-freeze-before-router-flush ordering in `AvPlaybackCoordinator.Pause`.
- **HaPlay group fire**: paused-wire → start-all → collective unpause is the right sync barrier
  (modulo the threading in C1).
- **Idempotent priming** (`PrepareOutputsBeforePlay` once per session) and priming through
  `MediaPlayer.VideoInput` so per-branch converters run.

---

## 8. Suggested order of attack

1. **F1** audio→video-only degrade (small, isolated, directly serves the stated goal).
2. **C1** move cue `Play()` off the UI thread + parallel group transport start (biggest perceived
   speed win; low framework risk since it's call-site threading).
3. **U1 + U2** "play what you can" policy in session open and cue route validation (UI-level,
   mirrors F1's philosophy).
4. **S1 phases 1–2** stream enumeration + open-time selection (framework), then **M1** (free once
   `VideoStreamIndex: None` exists), then **S1 phase 3** (UI pickers + cache-key updates).
5. **C2** interrupt forwarding in cue audio wrappers (small fix, improves stop/seek latency).
6. **F2/F3/F4** mid-stream format-change robustness, in that order (audio swr re-init is the
   cheapest and most likely to be hit; HW mid-stream fallback is the largest).
7. **C3** loop-wrap improvement via pre-armed standby swap.
8. Remaining P3s opportunistically.

---

## 9. Implementation log (2026-06-10)

Everything below was implemented the same day, validated by the full test sweep
(S.Media.Core 483, S.Media.FFmpeg 180, S.Media.Playback 75, HaPlay 449 — all green) and a full
solution build.

### Fixed

| Finding | Change |
|---------|--------|
| **F1** | `OpenInternalAfterFormatOpen` wraps audio setup in `try/catch when (_hasVideo)` → `ConfigureNoAudioStubAfterAudioSetupFailure` degrades to video-only (mirrors the existing video degrade). Includes a sample-rate/channel sanity check so bogus parameters degrade instead of failing. |
| **F2** | `EnsureSwrMatchesDecodedAudioFrameLocked` — swr input config (fmt/rate/layout) is captured at open and compared against each decoded frame; on mid-stream drift swr is rebuilt from the frame with the **output** kept at the negotiated `Audio.Format` (no downstream renegotiation). Wired into both `ReadInto` and `ConvertAudioFrame`. |
| **F3** | `SyncVideoPixelFormatIfNeeded` now also tracks source geometry (`_vSrcW/_vSrcH`); a mid-stream dimension change drops pass-through and sws-scales the new geometry into the unchanged negotiated canvas. `sws` source dims and `srcSliceH` now follow the tracked geometry. |
| **F6** | Frame-rate normalization cap raised 120 → 300 fps (144/240 fps content keeps its declared rate; timebase noise like 1000/1 still floors to 30). |
| **S1 (ph. 1–2)** | New `MediaStreamInfo` / `MediaStreamProbe.ProbeFile` (no-decoder stream enumeration, language/title metadata, `ContentSignature` for re-mux guards). `VideoDecoderOpenOptions.AudioStreamIndex/VideoStreamIndex` (+ `MediaPlayerOpenOptions` mirror): `null` = auto, `MediaStreamSelection.Disabled` = side off, invalid explicit index warns + falls back to auto. Exposed on `MediaContainerDecoder` (`Streams`, `ActiveAudio/VideoStreamIndex`, `ProbeStreams`). |
| **S1 (ph. 3)** | Cue drawer Audio tab gets an "Audio track" picker (visible only for 2+ decodable audio tracks); persisted on `MediaCueNode.AudioTrackIndex` + `AudioTrackSignature`; engine re-resolves by signature when stream tables shift; included in the prepared-cue key and watched for pre-roll invalidation; preview plays the same track. Playlist side: `FilePlaylistItem.AudioTrackIndex`, an "Audio track" context-menu submenu on the playlist, decoder cache keyed on (path, track). |
| **M1** | Cues with no video placements open their file with `VideoStreamIndex = Disabled` — zero video decode for sound-only cues on video files (no packets demuxed for the video stream at all). |
| **C1** | Cue transport starts moved off the UI thread (`Task.Run`): single fire, group fire (now **parallel** transport starts + the collective audio unpause barrier kept as the sync point), preview play, and `SetPausedAsync` resume (parallel + collective unpause). Pause silences sources inline (volatile flips) before the bounded transport pause. |
| **C2/M2** | `PausableAudioSource` and `AudioSourceFanout.Branch` forward `ICooperativeAudioReadInterrupt` to the inner source so router pause/stop can abort blocking decoder reads through the cue path; `IsExhausted` after dispose now reports exhausted (removable), not stalled. |
| **C3** | `WatchNaturalEndAsync` shrinks its poll as the clip end approaches (150 ms → ~15 ms), so loop wraps and natural ends trigger near the boundary instead of up to a poll late. |
| **C5 (partial)** | Standby `PrepareAsync` seek now honors the caller's cancellation token (aborts in-flight prime via `CancelInFlightSeek`); `StorePrepared`/`ReturnPrepared` sync-over-async disposals converted to proper async. |
| **C4 (docs)** | `StartGroupAsync` XML doc now states its stagger characteristics and points at the paused-wire pattern. |
| **U1** | `HaPlayPlaybackSession.TryCreate`: dead NDI carriers and unavailable PortAudio/local-video lines are skipped with per-line warnings (`Session.OpenWarnings`, surfaced in the player status bar); the open only fails when warnings exist and **zero** lines wired. |
| **U2** | `CuePlaybackEngine` route validation degraded to play-what-you-can: `SanitizeRoutePlan` drops unsatisfiable routes/categories with warnings (shown in the cue status bar), fires whatever remains, and only fails when nothing can be wired. Standby uses the same sanitized plan, and the prepared-cue key is now built from the sanitized plan so standby and Go agree. |
| **UI overhaul (first pass)** | • Cue header: removed the duplicate standalone Load/Save buttons (Files menu is the single home). • GO button styled as the accent/primary action. • Transport buttons got tooltips that double as the keyboard reference (Space/Enter/Backspace/Esc). • "Edit cues" toggle tooltip lists the tree keys (F2/Del/Ctrl+D/Ctrl+↑↓). • View menu now lists the six workspaces with their Ctrl+1…6 gestures (previously invisible shortcuts). • Localized hardcoded strings ("Edit cues", "Hold duration", duration format hint, "Action Targets…"). |

### Implemented in the follow-up pass (same day, later)

| Finding | Change |
|---------|--------|
| **C5 (remainder)** | `RefreshStandbyAsync` now prepares cold window entries with **bounded parallelism** (`ClipStandbyPolicy.PrepareParallelism`, default 2; window order preserved — the next cue to fire opens first) after a cheap sequential pass that marks warm entries Ready / cold ones Preparing up front. Whole refresh passes are serialized behind a `SemaphoreSlim` so overlapping refreshes can't evict each other's freshly prepared decoders. The dispose-vs-store race is closed: `StorePreparedAsync` checks `_disposed` under the gate and disposes the decoder instead of parking it in a drained map, and `DisposeAsync` flips the flag under the same gate. Regression test: `RefreshStandbyAsync_PreparesWindowEntriesInParallel`. |
| **C6** | `ClipCompositionRuntime.PumpOneFrame` fans the composited canvas out as **zero-copy refcounted views** (`VideoFrame.TryCreateCpuFanOutViews`) — all outputs share the CPU canvas backing, which returns to the pool once the last view is disposed. The per-output `DuplicateCpuBacking` deep copies (~8 MB × (outputs−1) per 1080p frame) remain only as the fallback for non-CPU backings (e.g. a GL compositor canvas). Regression test: `ClipCompositionRuntime_MultiOutputPump_SharesCanvasBackingAcrossOutputs` asserts both outputs receive the same plane memory. |

### Deferred (with reasons)

- **F4 — software fallback on mid-stream hardware decode failure.** Needs a reproducing sample to
  validate against (blind implementation risks breaking the healthy HW path); the open-time fallback
  already covers the common case. Suggested next step: capture a failing file/driver combo, then
  implement "demote to software at current position" reusing the seek-prime machinery.
- **F5 — transient network read retry.** Only relevant for http/rtsp inputs; needs a deliberate
  test setup. The interrupt callback plumbing is already in place for it.
- **Subtitle streams.** Enumerated by `MediaStreamProbe` (Kind = Subtitle) but not rendered — a
  separate pipeline.

### Notes for follow-up testing with real content

1. **MKV multi-language**: add a multi-track MKV as a cue → Audio tab shows the track picker;
   switching tracks re-prepares standby; preview follows the choice. Same via playlist right-click.
2. **Broken-audio file**: a video file with a corrupt/unsupported audio stream now opens and plays
   video-only with a warning log line (`audio stream unusable (...)`).
3. **Group fire**: multi-cue groups should now start noticeably faster (parallel opens were already
   there; transport starts are now parallel too) with the same audio-aligned start.
4. **Loop cues**: the wrap gap should be ~10× tighter; for fully gapless loops the pre-armed
   second-decoder swap (doc §5 C3 option 2) is the next step.
