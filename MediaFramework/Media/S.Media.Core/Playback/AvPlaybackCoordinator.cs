using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

/// <summary>
/// Coordinates start/stop/seek order for combined <see cref="AudioRouter"/> +
/// <see cref="MediaClock"/> and <see cref="VideoPlayer"/> sessions.
/// </summary>
internal static class AvPlaybackCoordinator
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Playback.AvPlaybackCoordinator");

    public static void Play(
        VideoPlayer video,
        AudioRouter? audioRouter = null,
        MediaClock? audioClock = null,
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        Trace.LogDebug("Play: hasAudio={HasAudio} hasPrefill={HasPrefill} hasStartHw={HasStartHw} hasVoMaster={HasVoMaster}",
            audioRouter is not null, prefillBeforeHardware is not null, startHardware is not null, videoOnlyMaster is not null);

        if (prefillBeforeHardware is not null)
            prefillBeforeHardware.Invoke();

        if (verifyPrebufferAfterPrefill is not null && !verifyPrebufferAfterPrefill())
            throw new InvalidOperationException(
                "AvPlaybackCoordinator.Play: verifyPrebufferAfterPrefill returned false.");

        if (startHardware is not null)
            startHardware.Invoke();

        if (audioRouter is not null && audioClock is not null)
        {
            audioRouter.Start();
            audioClock.Start();
        }
        else
        {
            if (videoOnlyMaster is not null)
                video.Clock.SetMaster(videoOnlyMaster);
            if (!video.Clock.IsRunning)
                video.Clock.Start();
        }

        video.Play();
    }

    public static void Pause(
        VideoPlayer video,
        AudioRouter? audioRouter = null,
        MediaClock? audioClock = null,
        CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null)
    {
        ArgumentNullException.ThrowIfNull(video);

        // Silence/stop audio FIRST. video.Pause can block up to the decode-thread join cap while a
        // native read unwinds; if we paused video first (the old order), the audio router + clock
        // would keep running for that whole window — audible pause latency and A/V divergence. The
        // audio pause is quick (stop the router thread + clock), so do it up front.
        try
        {
            if (audioRouter is not null && audioClock is not null)
            {
                audioRouter.Pause();
                audioClock.Pause(cancellationToken);
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
