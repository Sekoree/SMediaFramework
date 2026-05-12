using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

/// <summary>
/// Coordinates start/stop/seek order for combined <see cref="AudioPlayer"/> and
/// <see cref="VideoPlayer"/> sessions (hardware prefill, PortAudio stream open,
/// router/clock, then video decode).
/// </summary>
public static class AvPlaybackCoordinator
{
    /// <summary>
    /// Typical play order: optional decoder prefill → optional hardware
    /// <c>Start()</c> → <see cref="AudioPlayer.Play"/> (router + media clock) →
    /// <see cref="VideoPlayer.Play"/>.
    /// </summary>
    public static void Play(
        VideoPlayer video,
        AudioPlayer? audio = null,
        Action? prefillBeforeHardware = null,
        Action? startHardware = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        prefillBeforeHardware?.Invoke();
        startHardware?.Invoke();
        audio?.Play();
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
        video.Pause(cancellationToken);
        audio?.Pause();
    }

    /// <summary>Alias for <see cref="Pause"/>.</summary>
    public static void Stop(
        VideoPlayer video,
        AudioPlayer? audio = null,
        CancellationToken cancellationToken = default) =>
        Pause(video, audio, cancellationToken);

    /// <summary>Seek audio (and media clock) first, then video.</summary>
    public static void Seek(VideoPlayer video, AudioPlayer? audio, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(video);
        audio?.Seek(position);
        video.Seek(position);
    }
}
