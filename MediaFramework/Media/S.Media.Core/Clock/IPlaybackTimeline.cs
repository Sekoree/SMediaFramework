namespace S.Media.Core.Clock;

/// <summary>
/// Minimal playhead surface (architecture roadmap <b>strategy B</b>): position, running state, nominal rate, and seek.
/// <see cref="IMediaClock"/> extends this with tick events, cooperative stop/pause, optional <see cref="IPlaybackClock"/> mastering,
/// and <see cref="IMediaClock.PositionChanged"/> for playhead updates (~30 Hz from the driver — see <see cref="MediaClock"/> remarks).
/// </summary>
public interface IPlaybackTimeline
{
    TimeSpan CurrentPosition { get; }

    bool IsRunning { get; }

    /// <summary>Effective speed relative to real time (1.0 = normal). <see cref="MediaClock"/> is fixed at 1.0 until variable-speed playback exists.</summary>
    double PlaybackRate { get; }

    void Seek(TimeSpan position);
}
