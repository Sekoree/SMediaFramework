using S.Media.Core.Audio;
using S.Media.Core.Clock;
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
/// </remarks>
public static class AvPlaybackCoordinator
{
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
        prefillBeforeHardware?.Invoke();
        if (verifyPrebufferAfterPrefill is not null && !verifyPrebufferAfterPrefill())
        {
            throw new InvalidOperationException(
                "AvPlaybackCoordinator.Play: verifyPrebufferAfterPrefill returned false.");
        }

        startHardware?.Invoke();
        if (audio is not null)
            audio.Play();
        else
        {
            if (videoOnlyMaster is not null)
                video.Clock.SetMaster(videoOnlyMaster);
            if (!video.Clock.IsRunning)
                video.Clock.Start();
        }

        video.Play();
    }

    /// <summary>
    /// Pause/stop scheduling: <see cref="VideoPlayer"/> first, then
    /// <see cref="AudioPlayer"/> (so the clock driver can wind down before
    /// audio teardown).
    /// </summary>
    public static void Pause(
        VideoPlayer video,
        AudioPlayer? audio = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        try
        {
            video.Pause(cancellationToken);
        }
        finally
        {
            audio?.Pause();
        }
    }

    /// <summary>Alias for <see cref="Pause"/>.</summary>
    public static void Stop(
        VideoPlayer video,
        AudioPlayer? audio = null,
        CancellationToken cancellationToken = default) =>
        Pause(video, audio, cancellationToken);

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
    public static void SeekCoordinated(VideoPlayer video, AudioPlayer? audio, TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        Pause(video, audio, cancellationToken);
        Seek(video, audio, position);
    }
}
