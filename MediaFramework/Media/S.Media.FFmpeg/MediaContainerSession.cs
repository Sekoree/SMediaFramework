using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Pairs one <see cref="MediaContainerDecoder"/> with an <see cref="IAvPlaybackSession"/> and adds shared-mux flush
/// coordination at pause / coordinated-seek boundaries (the usual wiring for file sources built on
/// <see cref="MediaContainerSharedDemux"/>). Distinct from the generic <see cref="MediaPlaybackSession"/> in core:
/// this type understands the FFmpeg-specific container and calls <see cref="MediaContainerDecoder.FlushCodecPipelines"/>
/// at the right moments.
/// </summary>
/// <remarks>
/// <para>
/// This type does <strong>not</strong> own the decoder or the underlying session — keep existing <c>using</c>
/// scopes on <see cref="MediaContainerDecoder"/> and on the <see cref="VideoPlayer"/> / <see cref="AudioPlayer"/>
/// held by the session. It is <strong>not</strong> <see cref="IDisposable"/>. For one-shot mux-safe teardown of the whole
/// graph with per-step <strong>Debug</strong> logging via <see cref="S.Media.Core.Diagnostics.MediaDiagnostics"/>,
/// see <see cref="MediaContainerPlaybackBundle"/>.
/// </para>
/// <para>
/// <see cref="Pause(CancellationToken, Action?)"/> and <see cref="SeekCoordinated(TimeSpan, CancellationToken, Action?)"/>
/// default to running <see cref="MediaContainerDecoder.FlushCodecPipelines"/> after A/V quiesces; that default can
/// deadlock if the decode thread is still inside libav. Use <see cref="PauseSkippingSharedMuxFlush"/> /
/// <see cref="SeekCoordinatedSkippingSharedMuxFlush"/> in that case. Full discussion in
/// <c>Doc/MediaFramework-Architecture.md</c>.
/// </para>
/// <para>
/// Graph-wide master-clock PPM correction and synchronized multi-output drop/repeat are <strong>not</strong> implemented
/// here — see <see cref="MediaClock"/> / <see cref="MediaClockExtensions.SetMasterChain"/>,
/// <see cref="S.Media.Core.Audio.AudioRouter"/>, and per-output resampling hints
/// (<see cref="Audio.AdaptiveRateAudioOutput"/>).
/// </para>
/// </remarks>
public sealed class MediaContainerSession
{
    public MediaContainerSession(MediaContainerDecoder container, IAvPlaybackSession session)
    {
        Container = container ?? throw new ArgumentNullException(nameof(container));
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Pairs <paramref name="decoder"/> with a new <see cref="MediaPlaybackSession"/>. The simplest way to build a
    /// <see cref="MediaContainerSession"/> when callers don't already have an <see cref="IAvPlaybackSession"/> handle.
    /// </summary>
    public static MediaContainerSession Create(MediaContainerDecoder decoder, VideoPlayer video, IMediaClock clock, AudioPlayer? audio = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(clock);
        return new MediaContainerSession(decoder, new MediaPlaybackSession(video, clock, audio));
    }

    public MediaContainerDecoder Container { get; }

    public IAvPlaybackSession Session { get; }

    /// <inheritdoc cref="IAvPlaybackSession.Play"/>
    public void Play(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null) =>
        Session.Play(prefillBeforeHardware, startHardware, videoOnlyMaster, verifyPrebufferAfterPrefill);

    /// <summary>
    /// Pauses the session; after A/V pause, runs <paramref name="flushSharedMuxAfterPause"/> if supplied,
    /// otherwise <see cref="MediaContainerDecoder.FlushCodecPipelines"/>.
    /// </summary>
    public void Pause(CancellationToken cancellationToken = default, Action? flushSharedMuxAfterPause = null) =>
        Session.Pause(cancellationToken, flushSharedMuxAfterPause ?? Container.FlushCodecPipelines);

    /// <summary>
    /// Pauses A/V without running <see cref="MediaContainerDecoder.FlushCodecPipelines"/> — avoids demux/decoder
    /// re-entrancy deadlocks when the video decode thread may still be inside libav.
    /// </summary>
    public void PauseSkippingSharedMuxFlush(CancellationToken cancellationToken = default) =>
        Session.Pause(cancellationToken, flushSharedMuxAfterPause: null);

    /// <summary>
    /// Like <see cref="SeekCoordinated(TimeSpan, CancellationToken, Action?)"/> but skips the default mux flush.
    /// </summary>
    public void SeekCoordinatedSkippingSharedMuxFlush(TimeSpan position, CancellationToken cancellationToken = default) =>
        AvPlaybackCoordinator.SeekCoordinated(Session.Video, Session.Audio, position, cancellationToken,
            flushSharedMuxAfterPause: null);

    /// <inheritdoc cref="IAvPlaybackSession.Seek"/>
    public void Seek(TimeSpan position) =>
        AvPlaybackCoordinator.Seek(Session.Video, Session.Audio, position);

    /// <summary>
    /// <see cref="AvPlaybackCoordinator.SeekCoordinated(VideoPlayer, AudioPlayer?, TimeSpan, CancellationToken, Action?)"/>
    /// with the same flush default as <see cref="Pause(CancellationToken, Action?)"/>.
    /// </summary>
    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null) =>
        AvPlaybackCoordinator.SeekCoordinated(Session.Video, Session.Audio, position, cancellationToken,
            flushSharedMuxAfterPause ?? Container.FlushCodecPipelines);

    /// <summary>Forwarded <see cref="MediaContainerDecoder.SeekPresentation"/> — for live graphs prefer <see cref="SeekCoordinated"/> after pausing.</summary>
    public void SeekPresentation(TimeSpan position) => Container.SeekPresentation(position);
}
