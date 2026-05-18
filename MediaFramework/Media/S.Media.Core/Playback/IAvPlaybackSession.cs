using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

/// <summary>
/// Typed façade for combined <see cref="VideoPlayer"/> + <see cref="IMediaClock"/> (+ optional
/// <see cref="AudioPlayer"/>) playback. This is the stable dependency surface for hosts; pair with
/// <see cref="S.Media.FFmpeg.MediaContainerSession"/> when using <see cref="S.Media.FFmpeg.MediaContainerDecoder"/> for shared-mux flush defaults.
/// A future container session graph (single owner of demux + audio routes + dynamic video) can implement the same contract.
/// Use <see cref="PlaybackTimelineClockExtensions.SubscribePositionChanged(S.Media.Core.Playback.IAvPlaybackSession, EventHandler{TimeSpan})"/> to subscribe to <see cref="IMediaClock.PositionChanged"/> without holding a separate <see cref="IMediaClock"/> reference.
/// Use <see cref="PlaybackTimelineClockExtensions.AsPlayhead"/> on <see cref="IPlaybackTimeline"/> for a seek-free read model (strategy B).
/// </summary>
/// <remarks>
/// <para>
/// Playback control delegates to <see cref="AvPlaybackCoordinator"/>; graph-wide clock / drift policy beyond
/// <see cref="IMediaClock.SetMaster"/> remains host-owned — see <see cref="MediaClock"/> and <see cref="Audio.AudioRouter"/>.
/// </para>
/// </remarks>
public interface IAvPlaybackSession
{
    VideoPlayer Video { get; }

    IMediaClock Clock { get; }

    AudioPlayer? Audio { get; }

    /// <summary>Same object as <see cref="Clock"/> — strategy‑B <see cref="IPlaybackTimeline"/> view.</summary>
    IPlaybackTimeline Timeline => Clock;

    /// <inheritdoc cref="AvPlaybackCoordinator.Play(VideoPlayer, AudioPlayer?, Action?, Action?, IPlaybackClock?, Func{bool}?)"/>
    void Play(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null);

    /// <inheritdoc cref="AvPlaybackCoordinator.Pause(VideoPlayer, AudioPlayer?, CancellationToken, Action?)"/>
    void Pause(CancellationToken cancellationToken = default, Action? flushSharedMuxAfterPause = null);

    /// <inheritdoc cref="AvPlaybackCoordinator.Seek(VideoPlayer, AudioPlayer, TimeSpan)"/>
    void Seek(TimeSpan position);

    /// <inheritdoc cref="AvPlaybackCoordinator.SeekCoordinated(VideoPlayer, AudioPlayer?, TimeSpan, CancellationToken, Action?)"/>
    void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null);
}
