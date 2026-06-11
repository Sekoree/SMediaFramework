namespace S.Media.Core.Clock;

/// <summary>
/// Read-only playhead slice without <see cref="IPlayhead.Seek"/>.
/// Prefer <see cref="IPlayhead"/> for the full surface, or <see cref="IReadOnlyPlayhead"/>
/// (via <see cref="PlaybackTimelineClockExtensions.AsPlayhead"/>) for the seek-free view.
/// </summary>
[Obsolete("Use IPlayhead for the full surface, or IReadOnlyPlayhead / AsPlayhead() for a seek-free view. This alias will be removed in a future release.")]
public interface IPlaybackPlayhead : IReadOnlyPlayhead;
