namespace S.Media.Core.Clock;

/// <summary>
/// Read-only playhead slice without <see cref="IPlayhead.Seek"/>.
/// Prefer <see cref="IPlayhead"/> or <see cref="PlaybackTimelineClockExtensions.AsPlayhead"/> for new code.
/// </summary>
[Obsolete("Use IPlayhead for the full surface, or AsPlayhead() for a seek-free view. This alias will be removed in a future release.")]
public interface IPlaybackPlayhead
{
    TimeSpan CurrentPosition { get; }

    bool IsRunning { get; }

    double PlaybackRate { get; }
}
