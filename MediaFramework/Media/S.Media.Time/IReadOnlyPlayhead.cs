namespace S.Media.Time;

/// <summary>
/// Read-only playhead slice without <see cref="IPlayhead.Seek"/> — what
/// <see cref="PlaybackTimelineClockExtensions.AsPlayhead"/> returns for consumers that observe
/// the timeline but must not drive it.
/// </summary>
public interface IReadOnlyPlayhead
{
    /// <inheritdoc cref="IPlayhead.CurrentPosition"/>
    TimeSpan CurrentPosition { get; }

    bool IsRunning { get; }

    /// <summary>Effective speed relative to real time (1.0 = normal).</summary>
    double PlaybackRate { get; }
}
