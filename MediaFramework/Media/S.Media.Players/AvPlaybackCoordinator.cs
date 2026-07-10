using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Time;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.Players;

/// <summary>
/// Coordinates start/stop/seek order for combined <see cref="AudioRouter"/> +
/// <see cref="MediaClock"/> and <see cref="VideoPlayer"/> sessions.
/// </summary>
internal static class AvPlaybackCoordinator
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Playback.AvPlaybackCoordinator");
    private static readonly TimeSpan SyncStartVideoOutputTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AudioRealignTolerance = TimeSpan.FromMilliseconds(1);

    public static void Play(
        VideoPlayer video,
        AudioRouter? audioRouter = null,
        MediaClock? audioClock = null,
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null,
        string? audioSourceId = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        Trace.LogDebug("Play: hasAudio={HasAudio} hasPrefill={HasPrefill} hasStartHw={HasStartHw} hasVoMaster={HasVoMaster}",
            audioRouter is not null, prefillBeforeHardware is not null, startHardware is not null, videoOnlyMaster is not null);

        if (prefillBeforeHardware is not null)
            prefillBeforeHardware.Invoke();

        if (startHardware is not null)
            startHardware.Invoke();

        if (audioRouter is not null && audioClock is not null)
        {
            // Realign audio before video.Play() - video starts the decode thread and may start
            // the shared clock; audio Position tracks emitted samples (≈ clock at pause) so a
            // drift threshold would skip realign even when video decode is ~700ms ahead.
            if (!audioClock.IsRunning)
                RealignAudioSourceBeforeStart(audioRouter, audioClock, audioSourceId);

            video.Play();

            WaitForVideoBufferBeforeStartingAudio(video, video.Clock, verifyPrebufferAfterPrefill);
            var syncFramePresented = video.TryPresentBufferedFrameForSync(
                video.Clock.CurrentPosition,
                SyncStartVideoOutputTimeout);
            if (!syncFramePresented)
            {
                Trace.LogDebug(
                    "Play: sync video presentation did not complete before audio start (timeout={Timeout}, queued={Queued}, latestDecoded={Latest})",
                    SyncStartVideoOutputTimeout, video.QueuedFrameCount, video.LatestDecodedPresentationTime);
            }
            audioRouter.Start();
            audioClock.Start();
        }
        else
        {
            video.Play();

            if (verifyPrebufferAfterPrefill is not null && !verifyPrebufferAfterPrefill())
                throw new InvalidOperationException(
                    "AvPlaybackCoordinator.Play: verifyPrebufferAfterPrefill returned false.");

            if (videoOnlyMaster is not null)
                video.Clock.SetMaster(videoOnlyMaster);
            if (!video.Clock.IsRunning)
                video.Clock.Start();
        }
    }

    /// <summary>
    /// After a seek/resume, the compositor/scaler path can decode far slower than realtime. Hold audio
    /// until the video jitter buffer has enough frames stamped at/after the sync target.
    /// </summary>
    internal static bool IsVideoBufferReadyForSync(VideoPlayer video, TimeSpan target)
    {
        const int minQueued = 1;
        var playhead = target - video.PlayheadOffset;
        if (video.QueuedFrameCount < minQueued)
            return false;

        var lead = video.SyncStartupLead;
        if (video.HasFrameWithinLeadOf(playhead, lead))
            return video.LatestDecodedPresentationTime + lead >= playhead;

        // The earliest queued frame sits beyond the lead window of the sync target. Frames are buffered
        // in increasing-PTS order, so that earliest frame is the lowest PTS the source will ever deliver
        // here - waiting cannot lower it, and once the jitter buffer saturates the decode thread just
        // blocks on a slot it can never get (the queue only drains once the clock runs, which is gated on
        // this very check). This happens for streams whose first presentable frame starts a frame period
        // or two after the clock origin: e.g. an MP4 with video start_time=0.04 where demux priming
        // consumes the 0.04 keyframe, leaving the first delivered frame at ~0.08 against a 60 ms lead.
        // Treat a saturated buffer as sync-ready so audio starts against the earliest available frame
        // instead of spinning the full pre-audio timeout.
        return video.IsJitterBufferSaturated;
    }

    /// <summary>
    /// True when the video source will never deliver a frame to wait for - an audio-only file's stub video
    /// (exhausted from the start with nothing queued or in flight). Without this, audio playback of WAV/MP3
    /// or any video-less file blocks for the full pre-audio wait before a single sample is heard.
    /// </summary>
    internal static bool NoVideoToAwait(VideoPlayer video) =>
        video.IsSourceExhausted && video.QueuedFrameCount == 0 && video.PendingBufferedCount == 0;

    private static void WaitForVideoBufferBeforeStartingAudio(
        VideoPlayer video,
        IMediaClock clock,
        Func<bool>? verify)
    {
        const int maxWaitMs = 8000;
        var deadline = Environment.TickCount64 + maxWaitMs;
        var target = clock.CurrentPosition;
        var waitStart = Environment.TickCount64;

        while (Environment.TickCount64 < deadline)
        {
            if (verify is not null)
            {
                if (verify()) goto Done;
            }
            else if (IsVideoBufferReadyForSync(video, target) || NoVideoToAwait(video))
            {
                goto Done;
            }

            Thread.Sleep(5);
        }

        if (verify is not null && !verify())
            throw new InvalidOperationException(
                "AvPlaybackCoordinator.Play: verifyPrebufferAfterPrefill returned false after waiting for the video buffer.");

        Done:
        if (Trace.IsEnabled(LogLevel.Debug))
        {
            var playhead = target - video.PlayheadOffset;
            var lead = video.SyncStartupLead;
            TimeSpan? masterElapsed = null;
            if (clock is MediaClock mc && mc.Master is { } master)
                masterElapsed = master.ElapsedSinceStart;
            Trace.LogDebug(
                "WaitForVideoBuffer: waitedMs={WaitMs} target={Target} queued={Queued} lifetimePending={LifetimePending} latestDecoded={Latest} clock={Clock} syncReady={SyncReady} leadMs={LeadMs} masterElapsed={MasterElapsed}",
                Environment.TickCount64 - waitStart, target, video.QueuedFrameCount, video.PendingBufferedCount,
                video.LatestDecodedPresentationTime, clock.CurrentPosition,
                video.HasFrameWithinLeadOf(playhead, lead), lead.TotalMilliseconds,
                masterElapsed);
        }
    }

    /// <summary>
    /// After pause/resume the shared demux can leave audio decode/resampler state out of step with
    /// the frozen clock even when <see cref="ISeekableSource.Position"/> still reports emitted
    /// samples (≈ clock). Realign when there is measurable drift, but never let that recovery step
    /// abort transport startup.
    /// </summary>
    private static void RealignAudioSourceBeforeStart(
        AudioRouter audioRouter,
        MediaClock audioClock,
        string? audioSourceId)
    {
        var target = audioClock.CurrentPosition;
        if (!string.IsNullOrEmpty(audioSourceId))
        {
            TimeSpan pos;
            try
            {
                if (!audioRouter.TryGetSeekableSourcePosition(audioSourceId, out pos))
                    return;
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex,
                    "RealignAudio: could not read source {Id} position before start; continuing without realign",
                    audioSourceId);
                return;
            }

            var drift = (pos - target).Duration();
            if (drift <= AudioRealignTolerance)
            {
                Trace.LogDebug(
                    "RealignAudio: source={Id} already aligned at {Src} (clock={Clock}, driftMs={DriftMs})",
                    audioSourceId, pos, target, drift.TotalMilliseconds);
                return;
            }

            Trace.LogDebug(
                "RealignAudio: source={Id} from {Src} to clock {Clock} (driftMs={DriftMs})",
                audioSourceId, pos, target, drift.TotalMilliseconds);
            try
            {
                audioRouter.SeekSource(audioSourceId, target);
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex,
                    "RealignAudio: source {Id} seek to clock {Clock} failed before start; continuing",
                    audioSourceId, target);
            }
            return;
        }

        try
        {
            audioRouter.Seek(target);
        }
        catch (InvalidOperationException ex)
        {
            // Multiple sources - host must pass audioSourceId.
            Trace.LogDebug(ex, "RealignAudio: skipped unqualified seek before start");
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex,
                "RealignAudio: unqualified seek to clock {Clock} failed before start; continuing",
                target);
        }
    }

    public static void Pause(
        VideoPlayer video,
        AudioRouter? audioRouter = null,
        MediaClock? audioClock = null,
        CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null)
    {
        ArgumentNullException.ThrowIfNull(video);

        // Freeze the media clock while PortAudio's master clock is still calibrated, then stop
        // audio production (which Flush()es PortAudio and resets stream-time smoothing). The old
        // router-first order flushed before clock.Pause(), so ElapsedSinceStart jumped backward and
        // the UI playhead snapped to an earlier position on pause.
        //
        // Video.Pause can still block on the decode-thread join cap; clock and audio silence happen
        // first so pause feels immediate and the stored position stays stable.
        try
        {
            if (audioRouter is not null && audioClock is not null)
            {
                audioClock.Pause(cancellationToken);
                audioRouter.Pause();
            }
            else
            {
                video.Clock.Pause(cancellationToken);
            }
        }
        finally
        {
            try
            {
                video.Pause(cancellationToken);
            }
            finally
            {
                flushSharedMuxAfterPause?.Invoke();
            }
        }
    }

    public static void Stop(
        VideoPlayer video,
        AudioRouter? audioRouter = null,
        MediaClock? audioClock = null,
        CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null) =>
        Pause(video, audioRouter, audioClock, cancellationToken, flushSharedMuxAfterPause);

    public static void Seek(
        VideoPlayer video,
        AudioRouter? audioRouter,
        MediaClock? audioClock,
        string? audioSourceId,
        TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (audioRouter is not null && audioClock is not null)
        {
            if (!string.IsNullOrEmpty(audioSourceId))
                audioRouter.SeekSource(audioSourceId, position);
            else
                audioRouter.Seek(position);
            audioClock.Seek(position);
        }
        else
        {
            video.Clock.Seek(position);
        }

        // Seek the video only when its source is seekable - an audio clip's cover-art/stub video (and live
        // video) can't seek, but the audio seek above is what matters for those.
        if (video.CanSeek)
            video.Seek(position);
    }

    public static void SeekCoordinated(
        VideoPlayer video,
        AudioRouter? audioRouter,
        MediaClock? audioClock,
        string? audioSourceId,
        TimeSpan position,
        CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null)
    {
        Pause(video, audioRouter, audioClock, cancellationToken, flushSharedMuxAfterPause);
        Seek(video, audioRouter, audioClock, audioSourceId, position);
    }

    /// <summary>Rare hosts that need a custom flush delegate after pause.</summary>
    public static void PauseWithFlushAction(
        VideoPlayer video,
        AudioRouter? audioRouter,
        MediaClock? audioClock,
        Action flushAction,
        CancellationToken cancellationToken = default) =>
        Pause(video, audioRouter, audioClock, cancellationToken, flushAction);

    public static void SeekCoordinatedWithFlushAction(
        VideoPlayer video,
        AudioRouter? audioRouter,
        MediaClock? audioClock,
        string? audioSourceId,
        TimeSpan position,
        Action flushAction,
        CancellationToken cancellationToken = default)
    {
        Pause(video, audioRouter, audioClock, cancellationToken, flushAction);
        Seek(video, audioRouter, audioClock, audioSourceId, position);
    }
}
