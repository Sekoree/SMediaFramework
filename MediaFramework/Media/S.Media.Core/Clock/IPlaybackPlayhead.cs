namespace S.Media.Core.Clock;

/// <summary>
/// Read-only playhead slice of <see cref="IPlaybackTimeline"/> (strategy B): position, running state, and nominal rate
/// without <see cref="IPlaybackTimeline.Seek"/>. Hosts can depend on this type where cooperative seek must stay on
/// <see cref="Playback.MediaPlaybackSession"/> / <see cref="Playback.AvPlaybackCoordinator"/>.
/// </summary>
public interface IPlaybackPlayhead
{
    TimeSpan CurrentPosition { get; }

    bool IsRunning { get; }

    double PlaybackRate { get; }
}
