# A/V Sync After Seek — Investigation & Fix

_Date: 2026-06-08_

This document records an investigation into the "audio runs ahead of video after
seeking" problem reported for some files, the root cause, the fix, why the
existing `TransportSyncProbe` did not catch it, and a secondary transport
robustness fix.

## Symptoms (as reported)

| Test file | Codec / type | Behaviour |
|---|---|---|
| `DAY1_EN3rd_Re_0826_2_Edited.mov` | ProRes 1080p30 (all‑intra) | Flawless — pause/resume/seek all fine |
| `おねがいダーリン_0611.mov` | ProRes 4K60 (all‑intra) | Flawless |
| `_MESMERIZER_…mp4` | H.264 High 720p24 (YouTube rip, long GOP) | After seek, **audio ~2–3 s ahead of video**. A few pause/resume cycles eventually resync. |
| `THE IDOLM@STER MOVIE.mkv` | H.264 High 1080p24 (Blu‑ray rip, long GOP) | Same as above |
| `EC - Still Waiting.flac`, `wow war tonight…wav` | audio only | Fine |

The two facts that pinned the cause down:

1. **Only long‑GOP codecs (H.264/H.265) desynced; all‑intra ProRes never did.**
2. **The offset was ~2–3 s — i.e. one GOP / keyframe distance.**

That combination says: after a seek, audio resumes at the requested target while
video resumes at (or is effectively pinned to) the **keyframe before the
target**. ProRes is all‑intra (every frame is a keyframe), so its "keyframe
before target" *is* the target — which is exactly why it never desynced.

## Root cause

Hardware decode (VAAPI on this machine) is on by default. A hardware frame's
pixels live in GPU memory, so the decoder path copies them to a CPU "scratch"
`AVFrame` via `av_hwframe_transfer_data` before conversion/upload:

`MediaFramework/Media/S.Media.FFmpeg/Video/VideoHardwareDecodeContext.cs` →
`TransferToScratch`.

**`av_hwframe_transfer_data` copies only the pixel data — not the frame
properties.** The scratch frame therefore came out with
`best_effort_timestamp == AV_NOPTS_VALUE` and `pts == AV_NOPTS_VALUE`.

Downstream, `ResolveVideoPts` (in both `MediaContainerSharedDemux` and
`VideoFileDecoder`) reads the timestamp **from the scratch frame**:

```csharp
var pts = frame->best_effort_timestamp;
if (pts == AV_NOPTS_VALUE) pts = frame->pts;
if (pts == AV_NOPTS_VALUE)
{
    // fallback: synthesise a PTS from a frame counter
    return TimeSpan.FromSeconds(_vFramesEmitted / fps);
}
```

So on the hardware path **every** video frame fell through to the
`_vFramesEmitted / fps` fallback. During linear playback from the start this is
invisible: the counter and the real content both advance one frame at a time
from zero, so the label happens to match the picture.

After a seek it breaks. `SeekPresentation` re‑anchors the fallback counter to the
seek target:

```csharp
_vFramesEmitted = (long)Math.Round(position.TotalSeconds * fps);
```

The decoder, however, resumes at the keyframe that precedes the target (this is
how `AVSEEK_FLAG_BACKWARD` works). The very first frame out of the decoder is the
**keyframe**, but `ResolveVideoPts` now labels it with the **target** (from the
re‑anchored counter). The seek‑prime logic (`PrimeBothAfterSeekLocked`) then sees
`pts >= target` immediately and "finishes" without skipping anything — leaving
video sitting on the keyframe while believing it is at the target.

Audio has no GPU transfer, so its PTS is always correct and lands exactly on the
target. Net result:

> Video pixels = keyframe (target − one GOP). Audio = target.
> **Audio is ahead of video by the keyframe distance (~2–3 s).**

"Pause/resume a few times eventually fixes it" fits too: each resume re‑seeks the
source to wherever the clock now is. When that position happens to land close to
a keyframe, the keyframe distance is ~0 and the picture looks synced — hence the
"random number of tries."

### Proof

Temporary instrumentation in `TransferToScratch` printed, per frame, the source
HW frame timestamp vs the scratch timestamp:

```
[HWTRACE] hwBET=0     hwPTS=0     scratchBET=-9223372036854775808 scratchPTS=-9223372036854775808
[HWTRACE] hwBET=512   hwPTS=512   scratchBET=-9223372036854775808 scratchPTS=-9223372036854775808
[HWTRACE] hwBET=1024  hwPTS=1024  scratchBET=-9223372036854775808 scratchPTS=-9223372036854775808
```

`-9223372036854775808` is `long.MinValue` = `AV_NOPTS_VALUE`. The source frame
carries the real container PTS; the scratch loses it.

## The fix

Copy the frame properties after the data transfer
(`VideoHardwareDecodeContext.TransferToScratch`):

```csharp
var ret = av_hwframe_transfer_data(_swScratch, hwFrame, 0);
FFmpegException.ThrowIfError(ret, nameof(av_hwframe_transfer_data));
// av_hwframe_transfer_data copies ONLY pixel data, not frame properties.
var propRet = av_frame_copy_props(_swScratch, hwFrame);
FFmpegException.ThrowIfError(propRet, nameof(av_frame_copy_props));
return _swScratch;
```

This is the single transfer point used by **both** `MediaContainerSharedDemux`
and `VideoFileDecoder`, so the one change covers every hardware decode path that
reads back to the CPU. The GPU zero‑copy paths (DRM‑PRIME dma‑buf, D3D11 shared
handle) already use the original `_vFrame` and were never affected.

After the fix, hardware frames carry their real PTS, the seek prime correctly
skips from the keyframe up to the target, and audio/video land together.

## Why `TransportSyncProbe` did not show it

The probe compared each side against the clock:

- video: `RecordingVideoOutput.LastSubmittedPts − clock`
- audio: `Decoder.Audio.Position − clock`

Both of those are **PTS labels**. The bug is precisely that the label was
re‑anchored to look correct while the *pixels* were a GOP behind. The probe was
measuring the lie. (`Decoder.Audio.Position` is also essentially the clock by
construction, so the audio metric was doubly circular.)

A label‑based probe **cannot** catch a "right label, wrong picture" bug. The
probe needs to look at content.

### New `--verify-content` mode

`TransportSyncProbe` now has a content‑aware mode that decodes each seek target
**twice** — once with hardware, once with software (which always carries the true
container PTS) — and compares the **actual displayed luma** a fixed distance past
the seek using a downsampled 8×8 mean‑luma signature:

```
dotnet run --project MediaFramework/Tools/TransportSyncProbe -- <file> --verify-content --targets 17,30,60
```

Before the fix (note identical labels, very different pixels):

```
  target=  17.0s  hwLabel=00:00:17.4166666 swLabel=00:00:17.4166666  lumaDiff=  30.8  *** CONTENT DESYNC ***
  target=  30.0s  hwLabel=00:00:30.4166666 swLabel=00:00:30.4166666  lumaDiff=  38.2  *** CONTENT DESYNC ***
  target=  60.0s  hwLabel=00:01:00.4166666 swLabel=00:01:00.4166666  lumaDiff=  63.0  *** CONTENT DESYNC ***
FAIL: 3 target(s) show hardware video content that does not match the software reference.
```

After the fix:

```
  target=  17.0s  hwLabel=00:00:17.4166666 swLabel=00:00:17.4166666  lumaDiff=   0.0  ok
  target=  30.0s  hwLabel=00:00:30.4166666 swLabel=00:00:30.4166666  lumaDiff=   0.0  ok
  target=  60.0s  hwLabel=00:01:00.4166666 swLabel=00:01:00.4166666  lumaDiff=   0.0  ok
PASS: hardware and software show the same picture at every seek target.
```

(`lumaDiff` is average absolute per‑cell difference, 0–255. HW vs SW decode of
the *same* frame differs by ~0–4; a one‑GOP content gap scores 30–60+. The
threshold is 10.) This is the regression guard for this bug going forward — it
needs a machine with working hardware decode, so keep it out of the headless CI
build (same policy as the other smoke tools).

## Secondary fix: Play/resume could appear to "get stuck"

Reported separately: occasionally a file "got stuck trying to play… the only
recovery was waiting for the UI control timeout to unlock the button, then
trying again."

Cause: a budget mismatch in `MediaPlayerViewModel`.

- The resume/Play arc wrapped `Router.Play(...)` in a **6 s** wall
  (`RunBoundedAsync(..., TimeSpan.FromSeconds(6))`).
- `AvPlaybackCoordinator.Play` internally waits up to **8 s**
  (`WaitForVideoBufferBeforeStartingAudio`, `maxWaitMs = 8000`) for the video
  jitter buffer before starting audio.

When the first post‑seek frame was slow to arrive, the UI wall fired at 6 s,
`RunBoundedAsync` returned `false`, and `IsPlaying` stayed `false` — while the
background `Task.Run` actually finished the Play a moment later. The result was
audio playing under a "Play" button: the user had to wait out the transport lock
and press Play again.

Fix: a named `PlayWallTimeout` (11 s) that comfortably exceeds the framework's
internal 8 s buffer wait plus the sync‑present/hardware‑start tail, used by both
Play/resume paths (`StartPlaybackAsync` and the playlist‑advance resume). The
framework keeps its own internal caps (8 s buffer wait, 12 s decode‑join cap), so
the UI wall remains a genuine last resort rather than a premature abort of a Play
that is about to succeed.

## Files touched

- `MediaFramework/Media/S.Media.FFmpeg/Video/VideoHardwareDecodeContext.cs`
  — `av_frame_copy_props` after `av_hwframe_transfer_data` (the core fix).
- `MediaFramework/Tools/TransportSyncProbe/Program.cs`
  — `--verify-content` mode + luma‑signature content comparison.
- `UI/HaPlay/ViewModels/MediaPlayerViewModel.cs`
  — `PlayWallTimeout` for the Play/resume arcs.

## Verifying

```bash
# build
dotnet build MediaFramework/Media/S.Media.FFmpeg/S.Media.FFmpeg.csproj -c Debug

# decoder/seek tests
dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj \
  --filter "FullyQualifiedName~SharedDemuxSeekAlignment|FullyQualifiedName~MediaContainerDecoder"

# content regression guard (needs working hardware decode)
dotnet run --project MediaFramework/Tools/TransportSyncProbe -- \
  "<some long-GOP H.264/H.265 file>" --verify-content --targets 17,30,60
```

## Notes for future work

- A label‑only sync check is structurally blind to "right label, wrong picture."
  Any future A/V‑sync tooling should fingerprint content, not just timestamps.
- `ResolveVideoPts`'s frame‑counter fallback is fine as a last resort for streams
  with genuinely no timestamps, but it masks missing‑PTS bugs during linear
  playback (the desync only surfaces on seek). Consider logging once per session
  when the fallback is actually used, so a future "no PTS on HW frames"
  regression is loud instead of silent.
