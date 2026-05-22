using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

/// <summary>
/// Coordinates start/stop/seek order for combined <see cref="AudioPlayer"/> and
/// <see cref="VideoPlayer"/> sessions (hardware prefill, PortAudio stream open,
/// router/clock, then video decode).
/// </summary>
/// <remarks>
/// <para>
/// For file playback where audio and video share one libav demuxer, use
/// <c>S.Media.FFmpeg.MediaContainerDecoder.SeekPresentation</c> once after <see cref="SeekCoordinated"/>
/// (or after <see cref="Pause"/> and before <see cref="Play"/>) so both streams jump together, and
/// call <see cref="MediaClock.Seek(TimeSpan)"/> (or <see cref="AudioPlayer.Seek"/>, which updates the clock)
/// so the visible playhead matches.
/// </para>
/// <para>
/// Coordinated graph-wide master PPM, synchronized multi-output drop/repeat, or other timing policy beyond
/// per-output hints is <strong>not</strong> implemented here — see <see cref="MediaClock"/> and <see cref="Audio.AudioRouter"/>.
/// </para>
/// <para>
/// When both streams use <c>MediaContainerDecoder</c>, pass <c>decoder.FlushCodecPipelines</c> (or a lambda
/// that calls it) as <c>flushSharedMuxAfterPause</c> on <see cref="Pause"/> / <see cref="SeekCoordinated"/>
/// so libav delay is cleared after pumps stop — same contract as calling <c>FlushCodecPipelines</c> with no
/// concurrent reads (see that API's remarks).
/// </para>
/// </remarks>
public static class AvPlaybackCoordinator
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Playback.AvPlaybackCoordinator");


    /// <summary>
    /// Typical play order: optional decoder prefill → optional
    /// <paramref name="verifyPrebufferAfterPrefill"/> → optional hardware
    /// <c>Start()</c> → <see cref="AudioPlayer.Play"/> (router + media clock) →
    /// <see cref="VideoPlayer.Play"/>.
    /// When <paramref name="audio"/> is <c>null</c> and <paramref name="videoOnlyMaster"/> is not,
    /// assigns that master to <see cref="VideoPlayer.Clock"/> and starts the clock if needed
    /// before starting video.
    /// </summary>
    /// <param name="verifyPrebufferAfterPrefill">
    /// When not <c>null</c>, invoked after <paramref name="prefillBeforeHardware"/> and before
    /// <paramref name="startHardware"/> (for example to assert the hardware ring reached a target
    /// fill). If it returns <c>false</c>, throws <see cref="InvalidOperationException"/> and does
    /// not open hardware or start playback.
    /// </param>
    public static void Play(
        VideoPlayer video,
        AudioPlayer? audio = null,
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        Trace.LogDebug("Play: hasAudio={HasAudio} hasPrefill={HasPrefill} hasStartHw={HasStartHw} hasVoMaster={HasVoMaster}",
            audio is not null, prefillBeforeHardware is not null, startHardware is not null, videoOnlyMaster is not null);

        if (prefillBeforeHardware is not null)
        {
            Trace.LogTrace("Play: invoking prefill");
            prefillBeforeHardware.Invoke();
        }

        if (verifyPrebufferAfterPrefill is not null && !verifyPrebufferAfterPrefill())
        {
            Trace.LogWarning("Play: verifyPrebufferAfterPrefill returned false — aborting");
            throw new InvalidOperationException(
                "AvPlaybackCoordinator.Play: verifyPrebufferAfterPrefill returned false.");
        }

        if (startHardware is not null)
        {
            Trace.LogTrace("Play: invoking startHardware");
            startHardware.Invoke();
        }

        if (audio is not null)
        {
            Trace.LogTrace("Play: starting audio player");
            audio.Play();
        }
        else
        {
            if (videoOnlyMaster is not null)
                video.Clock.SetMaster(videoOnlyMaster);
            if (!video.Clock.IsRunning)
            {
                Trace.LogTrace("Play: starting video-only clock");
                video.Clock.Start();
            }
        }

        Trace.LogTrace("Play: starting video player");
        video.Play();
        Trace.LogDebug("Play: complete (audioRunning={AudioRunning} videoRunning={VideoRunning} clockRunning={ClockRunning})",
            audio?.IsPlaying ?? false, video.IsRunning, video.Clock.IsRunning);
    }

    /// <summary>
    /// Pause/stop scheduling: <see cref="VideoPlayer"/> first, then
    /// <see cref="AudioPlayer"/> when present (so the clock driver can wind down before
    /// audio teardown). When <paramref name="audio"/> is <c>null</c>, pauses <see cref="VideoPlayer.Clock"/>
    /// here so the media-clock driver cannot tick during the optional shared-mux flush hook
    /// (audio-less hosts such as <c>VideoPlaybackSmoke</c> with <see cref="VideoPtsClock"/> otherwise left the driver running).
    /// Finally runs <paramref name="flushSharedMuxAfterPause"/> when supplied.
    /// </summary>
    /// <param name="flushSharedMuxAfterPause">
    /// When using <c>S.Media.FFmpeg.MediaContainerDecoder</c>, pass <c>decoder.FlushCodecPipelines</c>
    /// (or an equivalent delegate) so both libav codecs and the demuxer are re-synced at the current mux
    /// playhead after both sides are paused. Omit when decoders are separate or the container is not shared.
    /// </param>
    public static void Pause(
        VideoPlayer video,
        AudioPlayer? audio = null,
        CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        Trace.LogDebug("Pause: hasAudio={HasAudio} hasFlush={HasFlush}",
            audio is not null, flushSharedMuxAfterPause is not null);
        try
        {
            video.Pause(cancellationToken);
        }
        finally
        {
            if (audio is not null)
                audio.Pause();
            else
                video.Clock.Pause(cancellationToken);

            if (flushSharedMuxAfterPause is not null)
            {
                Trace.LogTrace("Pause: invoking shared-mux flush");
                flushSharedMuxAfterPause.Invoke();
            }
            Trace.LogDebug("Pause: complete");
        }
    }

    /// <summary>Alias for <see cref="Pause"/>.</summary>
    public static void Stop(
        VideoPlayer video,
        AudioPlayer? audio = null,
        CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null) =>
        Pause(video, audio, cancellationToken, flushSharedMuxAfterPause);

    /// <summary>
    /// Seeks audio (and its media clock) when present, otherwise seeks the video player's clock,
    /// then seeks the video source. Keeps the visible playhead aligned on audio-less paths.
    /// </summary>
    /// <remarks>
    /// Call while paused (for example via <see cref="SeekCoordinated"/>) when decoders must not
    /// run during the jump. When audio and video come from <c>MediaContainerDecoder</c>, seek that
    /// container once before or after this call so both <see cref="IAudioSource"/> and
    /// <see cref="IVideoSource"/> stay aligned on the same demuxer timeline.
    /// </remarks>
    public static void Seek(VideoPlayer video, AudioPlayer? audio, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(video);
        if (audio is not null)
            audio.Seek(position);
        else
            video.Clock.Seek(position);
        video.Seek(position);
    }

    /// <summary>
    /// Pauses both sides first (so decoders and the audio router are quiescent), then performs
    /// <see cref="Seek"/>. Does <strong>not</strong> resume — call <see cref="Play"/> afterwards if
    /// you want playback to continue.
    /// </summary>
    /// <remarks>
    /// This is the recommended bracket for A/V jumps: no router chunks are mixed while sources seek.
    /// Pair with a single <c>MediaContainerDecoder.SeekPresentation</c> when both streams share one
    /// <c>AVFormatContext</c>, then <see cref="MediaClock.Seek(TimeSpan)"/> (or <see cref="AudioPlayer.Seek"/>)
    /// before <see cref="Play"/>.
    /// </remarks>
    /// <param name="flushSharedMuxAfterPause">
    /// Forwarded to <see cref="Pause"/> — use <c>MediaContainerDecoder.FlushCodecPipelines</c> when audio and
    /// video share one <c>AVFormatContext</c>.
    /// </param>
    public static void SeekCoordinated(VideoPlayer video, AudioPlayer? audio, TimeSpan position,
        CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null)
    {
        Pause(video, audio, cancellationToken, flushSharedMuxAfterPause);
        Seek(video, audio, position);
    }
}
