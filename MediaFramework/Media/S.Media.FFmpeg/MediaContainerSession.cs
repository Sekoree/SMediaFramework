using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Pairs one <see cref="MediaContainerDecoder"/> with playback controls and shared-mux flush coordination.
/// </summary>
public sealed class MediaContainerSession
{
    private readonly Action _defaultFlushSharedMuxAfterPause;

    internal MediaContainerSession(MediaContainerDecoder container, IAvPlaybackSession session)
    {
        Container = container ?? throw new ArgumentNullException(nameof(container));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _defaultFlushSharedMuxAfterPause = Container.FlushCodecPipelines;
    }

    internal MediaContainerSession(IAvPlaybackSession session, Action defaultFlushSharedMuxAfterPause)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _defaultFlushSharedMuxAfterPause = defaultFlushSharedMuxAfterPause ?? throw new ArgumentNullException(nameof(defaultFlushSharedMuxAfterPause));
        Container = null!;
    }

    public static MediaContainerSession Create(
        MediaContainerDecoder decoder,
        VideoPlayer video,
        IMediaClock clock,
        AudioRouter? audioRouter = null,
        MediaClock? audioClock = null,
        string? audioSourceId = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(clock);
        return new MediaContainerSession(decoder,
            new MediaPlaybackSession(video, clock, audioRouter, audioClock, audioSourceId));
    }

    public MediaContainerDecoder Container { get; }
    internal IAvPlaybackSession Session { get; }

    public void Play(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null) =>
        Session.Play(prefillBeforeHardware, startHardware, videoOnlyMaster, verifyPrebufferAfterPrefill);

    public void Pause(CancellationToken cancellationToken = default, Action? flushSharedMuxAfterPause = null) =>
        Session.Pause(cancellationToken, flushSharedMuxAfterPause ?? _defaultFlushSharedMuxAfterPause);

    public void Pause(CancellationToken cancellationToken, PauseFlushPolicy flushPolicy) =>
        Session.Pause(cancellationToken, ResolveFlush(flushPolicy));

    public void PauseSkippingSharedMuxFlush(CancellationToken cancellationToken = default) =>
        Session.Pause(cancellationToken, flushSharedMuxAfterPause: null);

    public void SeekCoordinatedSkippingSharedMuxFlush(TimeSpan position, CancellationToken cancellationToken = default) =>
        AvPlaybackCoordinator.SeekCoordinated(Session.Video, Session.AudioRouter, Session.AudioClock, Session.AudioSourceId,
            position, cancellationToken, flushSharedMuxAfterPause: null);

    public void Seek(TimeSpan position) =>
        SeekSharedDemuxPreservingPlaybackState(position, CancellationToken.None, ResolveFlush(PauseFlushPolicy.FlushCodecPipelines));

    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null) =>
        AvPlaybackCoordinator.SeekCoordinated(Session.Video, Session.AudioRouter, Session.AudioClock, Session.AudioSourceId,
            position, cancellationToken, flushSharedMuxAfterPause ?? _defaultFlushSharedMuxAfterPause);

    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken, PauseFlushPolicy flushPolicy) =>
        AvPlaybackCoordinator.SeekCoordinated(Session.Video, Session.AudioRouter, Session.AudioClock, Session.AudioSourceId,
            position, cancellationToken, ResolveFlush(flushPolicy));

    public void SeekPresentation(TimeSpan position) => Container.SeekPresentation(position);

    private void SeekSharedDemuxPreservingPlaybackState(
        TimeSpan position,
        CancellationToken cancellationToken,
        Action? flushSharedMuxAfterPause)
    {
        var resume = Session.Clock.IsRunning
                     || Session.Video.IsRunning
                     || Session.AudioRouter?.IsRunning == true;

        Session.Pause(cancellationToken, flushSharedMuxAfterPause);
        Container.SeekPresentation(position);
        Session.Clock.Seek(position);
        if (Session.AudioClock is not null && !ReferenceEquals(Session.Clock, Session.AudioClock))
            Session.AudioClock.Seek(position);

        if (resume)
            Session.Play();
    }

    private Action? ResolveFlush(PauseFlushPolicy policy) =>
        policy == PauseFlushPolicy.FlushCodecPipelines ? _defaultFlushSharedMuxAfterPause : null;
}
