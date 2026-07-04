namespace S.Media.Core.Video;

/// <summary>
/// A time-addressed overlay source: given a master timeline position, produces the overlay frame to composite on
/// top of the canvas at that instant, or <c>null</c> when nothing shows. Unlike <see cref="IVideoSource"/>
/// (pull-based, monotonic), it is queried by absolute time.
/// </summary>
/// <remarks>
/// The returned frame is <strong>borrowed</strong> — owned by the source and potentially re-rendered in place, so
/// it is valid only for the current composition tick. Composite it immediately and do <em>not</em> dispose it.
/// Subtitle layers (text via Skia, ASS via libass) are the primary implementers.
/// </remarks>
public interface IVideoOverlaySource : IDisposable
{
    /// <summary>The overlay frame active at <paramref name="position"/>, or <c>null</c> when nothing shows. Borrowed.</summary>
    VideoFrame? RenderAt(TimeSpan position);
}
