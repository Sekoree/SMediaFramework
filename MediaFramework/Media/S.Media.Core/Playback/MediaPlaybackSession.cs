using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

/// <summary>
/// Holds <see cref="VideoPlayer"/> + <see cref="IMediaClock"/> (+ optional audio router/clock) for coordinated transport.
/// </summary>
internal sealed class MediaPlaybackSession : IAvPlaybackSession
{
    public MediaPlaybackSession(
        VideoPlayer video,
        IMediaClock clock,
        AudioRouter? audioRouter = null,
        MediaClock? audioClock = null,
        string? audioSourceId = null)
    {
        Video = video ?? throw new ArgumentNullException(nameof(video));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        AudioRouter = audioRouter;
        AudioClock = audioClock;
        AudioSourceId = audioSourceId;
    }

    public VideoPlayer Video { get; }
    public IMediaClock Clock { get; }
    public AudioRouter? AudioRouter { get; }
    public MediaClock? AudioClock { get; }
    public string? AudioSourceId { get; }

    public void Play(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null) =>
        AvPlaybackCoordinator.Play(Video, AudioRouter, AudioClock, prefillBeforeHardware, startHardware, videoOnlyMaster,
            verifyPrebufferAfterPrefill, AudioSourceId);

    public void Pause(CancellationToken cancellationToken = default, Action? flushSharedMuxAfterPause = null) =>
        AvPlaybackCoordinator.Pause(Video, AudioRouter, AudioClock, cancellationToken, flushSharedMuxAfterPause);

    public void Seek(TimeSpan position) =>
        AvPlaybackCoordinator.Seek(Video, AudioRouter, AudioClock, AudioSourceId, position);

    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null) =>
        AvPlaybackCoordinator.SeekCoordinated(Video, AudioRouter, AudioClock, AudioSourceId, position, cancellationToken,
            flushSharedMuxAfterPause);
}
