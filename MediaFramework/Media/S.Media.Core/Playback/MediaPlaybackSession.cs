using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

/// <summary>
/// Holds the usual <see cref="VideoPlayer"/> + <see cref="IMediaClock"/> (+ optional
/// <see cref="AudioPlayer"/>) and forwards <see cref="Play"/>, <see cref="Pause"/>,
/// <see cref="Seek"/>, and <see cref="SeekCoordinated"/> to <see cref="AvPlaybackCoordinator"/> with consistent ordering.
/// </summary>
/// <remarks>
/// <para>
/// This type does not own disposal of the players — the host keeps existing
/// <c>using</c> patterns on <see cref="VideoPlayer"/> and <see cref="AudioPlayer"/>.
/// Tools such as <c>VideoPlaybackSmoke</c> construct a session after wiring PortAudio prefill,
/// then call <see cref="Play"/> / <see cref="Pause"/> (optionally with a shared-mux flush delegate).
/// </para>
/// <para>
/// <strong>Lock order</strong>: avoid holding unrelated host mutexes across these calls.
/// If your host mutates <see cref="AudioRouter"/> routes on the same thread as playback
/// control, follow the synchronization assumptions documented on <see cref="AudioRouter.Pause"/>.
/// </para>
/// </remarks>
public sealed class MediaPlaybackSession
{
    public MediaPlaybackSession(VideoPlayer video, IMediaClock clock, AudioPlayer? audio = null)
    {
        Video = video ?? throw new ArgumentNullException(nameof(video));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Audio = audio;
    }

    public VideoPlayer Video { get; }
    public IMediaClock Clock { get; }
    public AudioPlayer? Audio { get; }

    /// <inheritdoc cref="AvPlaybackCoordinator.Play(VideoPlayer, AudioPlayer?, Action?, Action?, IPlaybackClock?, Func{bool}?)"/>
    public void Play(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null) =>
        AvPlaybackCoordinator.Play(Video, Audio, prefillBeforeHardware, startHardware, videoOnlyMaster,
            verifyPrebufferAfterPrefill);

    /// <inheritdoc cref="AvPlaybackCoordinator.Pause(VideoPlayer, AudioPlayer?, CancellationToken, Action?)"/>
    public void Pause(CancellationToken cancellationToken = default, Action? flushSharedMuxAfterPause = null) =>
        AvPlaybackCoordinator.Pause(Video, Audio, cancellationToken, flushSharedMuxAfterPause);

    /// <inheritdoc cref="AvPlaybackCoordinator.Seek(VideoPlayer, AudioPlayer, TimeSpan)"/>
    public void Seek(TimeSpan position) => AvPlaybackCoordinator.Seek(Video, Audio, position);

    /// <inheritdoc cref="AvPlaybackCoordinator.SeekCoordinated(VideoPlayer, AudioPlayer?, TimeSpan, CancellationToken, Action?)"/>
    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null) =>
        AvPlaybackCoordinator.SeekCoordinated(Video, Audio, position, cancellationToken, flushSharedMuxAfterPause);
}
