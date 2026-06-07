using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Playback;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Pairs one <see cref="MediaContainerDecoder"/> with playback controls and shared-mux flush coordination.
/// </summary>
public sealed class MediaContainerSession
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.FFmpeg.MediaContainerSession");

    private readonly Action _defaultFlushSharedMuxAfterPause;
    private readonly bool _hasContainer;

    internal MediaContainerSession(MediaContainerDecoder container, IAvPlaybackSession session)
    {
        Container = container ?? throw new ArgumentNullException(nameof(container));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _defaultFlushSharedMuxAfterPause = Container.FlushCodecPipelines;
        _hasContainer = true;
    }

    internal MediaContainerSession(IAvPlaybackSession session, Action defaultFlushSharedMuxAfterPause)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _defaultFlushSharedMuxAfterPause = defaultFlushSharedMuxAfterPause ?? throw new ArgumentNullException(nameof(defaultFlushSharedMuxAfterPause));
        Container = null!;
        _hasContainer = false;
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
        SeekSharedDemuxPreservingPlaybackState(position, cancellationToken, flushSharedMuxAfterPause: null, resumeIfWasRunning: false);

    public void Seek(TimeSpan position) =>
        SeekSharedDemuxPreservingPlaybackState(position, CancellationToken.None, ResolveFlush(PauseFlushPolicy.FlushCodecPipelines), resumeIfWasRunning: true);

    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null) =>
        SeekSharedDemuxPreservingPlaybackState(position, cancellationToken, flushSharedMuxAfterPause ?? _defaultFlushSharedMuxAfterPause, resumeIfWasRunning: false);

    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken, PauseFlushPolicy flushPolicy) =>
        SeekSharedDemuxPreservingPlaybackState(position, cancellationToken, ResolveFlush(flushPolicy), resumeIfWasRunning: false);

    public void SeekPresentation(TimeSpan position) => Container.SeekPresentation(position);

    /// <summary>
    /// Bridges a host seek <see cref="CancellationToken"/> to the shared demux so a UI seek timeout aborts
    /// the (otherwise uninterruptible) decode-to-target prime instead of letting it run on for seconds and
    /// race the next transport command. No-op for the container-less session ctor or a token that can never
    /// be cancelled.
    /// </summary>
    private CancellationTokenRegistration RegisterSeekCancellation(CancellationToken cancellationToken) =>
        Container is { } container && cancellationToken.CanBeCanceled
            ? cancellationToken.Register(container.CancelInFlightSeek)
            : default;

    private void SeekSharedDemuxPreservingPlaybackState(
        TimeSpan position,
        CancellationToken cancellationToken,
        Action? flushSharedMuxAfterPause,
        bool resumeIfWasRunning)
    {
        var resume = resumeIfWasRunning
                     && (Session.Clock.IsRunning
                         || Session.Video.IsRunning
                         || Session.AudioRouter?.IsRunning == true);

        Session.Pause(cancellationToken, flushSharedMuxAfterPause);
        if (!_hasContainer)
        {
            Session.Seek(position);
            Session.Clock.Seek(position);
            if (Session.AudioClock is not null && !ReferenceEquals(Session.Clock, Session.AudioClock))
                Session.AudioClock.Seek(position);
            if (resume)
                Session.Play();
            return;
        }

        using (RegisterSeekCancellation(cancellationToken))
            Container.SeekPresentation(position);

        var syncPos = ResolvePresentationClockPosition(position);
        if (Trace.IsEnabled(LogLevel.Debug) && _hasContainer)
        {
            TimeSpan? audioPos = Container.HasAudio && Container.Audio is ISeekableSource a ? a.Position : null;
            TimeSpan? videoPos = Container.HasVideo && Container.Video is ISeekableSource v ? v.Position : null;
            Trace.LogDebug(
                "SeekSharedDemux: requested={Requested} sync={Sync} audioPos={Audio} videoPos={Video} spreadMs={SpreadMs}",
                position, syncPos, audioPos, videoPos,
                audioPos is { } ap && videoPos is { } vp ? Math.Abs((ap - vp).TotalMilliseconds) : (double?)null);
        }

        Session.Clock.Seek(syncPos);
        if (Session.AudioClock is not null && !ReferenceEquals(Session.Clock, Session.AudioClock))
            Session.AudioClock.Seek(syncPos);

        if (resume)
        {
            PrewarmVideoAfterSeek();
            Session.Play();
        }
    }

    /// <summary>
    /// After <see cref="MediaContainerDecoder.SeekPresentation"/>, prefer the decoder's own playhead
    /// (post keyframe prime) over the raw UI target so the visible clock matches audible content.
    /// </summary>
    private TimeSpan ResolvePresentationClockPosition(TimeSpan requested) =>
        _hasContainer ? Container.GetAlignedPresentationPosition(requested) : requested;

    /// <summary>
    /// Briefly spins up video decode after a seek so the jitter buffer has frames before audio resumes.
    /// Call after <see cref="SeekCoordinated"/> when the host will <see cref="Play"/> immediately.
    /// </summary>
    public void PrewarmVideoAfterSeek() => PrewarmVideoAfterSeekCore();

    private void PrewarmVideoAfterSeekCore()
    {
        if (!_hasContainer || !Container.HasVideo)
            return;

        var video = Session.Video;
        var target = Session.Clock.CurrentPosition;
        if (!video.IsRunning)
            video.Play();

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (AvPlaybackCoordinator.IsVideoBufferReadyForSync(video, target))
                return;
            Thread.Sleep(5);
        }
    }

    private Action? ResolveFlush(PauseFlushPolicy policy) =>
        policy == PauseFlushPolicy.FlushCodecPipelines ? _defaultFlushSharedMuxAfterPause : null;
}
