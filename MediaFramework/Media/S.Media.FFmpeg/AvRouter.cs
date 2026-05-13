using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Thin façade pairing one <see cref="MediaContainerDecoder"/> with an <see cref="IAvPlaybackSession"/>.
/// Centralizes shared-demux <see cref="MediaContainerDecoder.FlushCodecPipelines"/> at pause boundaries and
/// on coordinated seeks — the usual wiring for file sources built on <see cref="MediaContainerSharedDemux"/>.
/// Prefer constructing via <see cref="MediaContainerAvRouter.Create"/> when the session is a plain <see cref="MediaPlaybackSession"/>,
/// or <see cref="MediaContainerPlaybackGraph"/> when you want decoder + clock + players grouped with the same router.
/// </summary>
/// <remarks>
/// <para>
/// This type does <strong>not</strong> own the decoder or the session: keep existing <c>using</c> / host
/// disposal order on <see cref="MediaContainerDecoder"/> and on <see cref="VideoPlayer"/> /
/// <see cref="AudioPlayer"/> held by the session.
/// </para>
/// <para>
/// For <see cref="Pause(CancellationToken, Action?)"/> and <see cref="SeekCoordinated(TimeSpan, CancellationToken, Action?)"/>,
/// when <c>flushSharedMuxAfterPause</c> is <c>null</c>, <see cref="MediaContainerDecoder.FlushCodecPipelines"/> is used.
/// Pass a no-op delegate when you intentionally skip that flush.
/// </para>
/// <para>
/// Graph-wide master-clock PPM, synchronized multi-sink drop/repeat, or other coordinated timing policy is
/// <strong>not</strong> implemented here — see <see cref="MediaClock"/> / <see cref="MediaClockExtensions.SetMasterChain"/>,
/// <see cref="S.Media.Core.Audio.AudioRouter"/>, and checklist Tier E **18** (per-sink resampling hints: <see cref="Audio.AdaptiveRateAudioSink"/>).
/// </para>
/// </remarks>
public sealed class AvRouter
{
    public AvRouter(MediaContainerDecoder container, IAvPlaybackSession session)
    {
        Container = container ?? throw new ArgumentNullException(nameof(container));
        Session = session ?? throw new ArgumentNullException(nameof(session));
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
