using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;

namespace S.Media.Core.Playback;

/// <summary>
/// Internal playback contract used by <see cref="S.Media.FFmpeg.MediaContainerSession"/> and
/// <see cref="S.Media.Playback.MediaPlayer"/>.
/// </summary>
internal interface IAvPlaybackSession
{
    VideoPlayer Video { get; }
    IMediaClock Clock { get; }
    AudioRouter? AudioRouter { get; }
    MediaClock? AudioClock { get; }
    string? AudioSourceId { get; }

    void Play(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null);

    void Pause(CancellationToken cancellationToken = default, Action? flushSharedMuxAfterPause = null);
    void Seek(TimeSpan position);
    void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        Action? flushSharedMuxAfterPause = null);
}
