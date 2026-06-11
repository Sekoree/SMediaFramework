using S.Media.Core.Playback;

namespace S.Media.Core.Clock;

/// <summary>
/// Helpers for <see cref="IPlayhead"/> and <see cref="IMediaClock"/>.
/// </summary>
public static class PlaybackTimelineClockExtensions
{
    /// <summary>
    /// Returns a seek-free view of <paramref name="playhead"/> (same position / running / rate).
    /// </summary>
    public static IReadOnlyPlayhead AsPlayhead(this IPlayhead playhead)
    {
        ArgumentNullException.ThrowIfNull(playhead);
        return new PlaybackPlayheadAdapter(playhead);
    }

    /// <summary>Obsolete overload — use <see cref="AsPlayhead(IPlayhead)"/>.</summary>
    [Obsolete("Use AsPlayhead(IPlayhead).")]
    public static IReadOnlyPlayhead AsPlayhead(this IPlaybackTimeline timeline) =>
        AsPlayhead((IPlayhead)timeline);

    /// <summary>
    /// Subscribes <paramref name="handler"/> to <see cref="IAvPlaybackSession.Clock"/> <see cref="IMediaClock.PositionChanged"/>.
    /// </summary>
    internal static IDisposable SubscribePositionChanged(this IAvPlaybackSession session, EventHandler<TimeSpan> handler)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Clock.SubscribePositionChanged(handler);
    }

    /// <summary>
    /// Subscribes <paramref name="handler"/> to <see cref="IMediaClock.PositionChanged"/>; disposing the result unsubscribes.
    /// </summary>
    public static IDisposable SubscribePositionChanged(this IMediaClock clock, EventHandler<TimeSpan> handler)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(handler);
        clock.PositionChanged += handler;
        return new PositionChangedSubscription(clock, handler);
    }

    private sealed class PositionChangedSubscription : IDisposable
    {
        private IMediaClock? _clock;
        private readonly EventHandler<TimeSpan> _handler;

        public PositionChangedSubscription(IMediaClock clock, EventHandler<TimeSpan> handler)
        {
            _clock = clock;
            _handler = handler;
        }

        public void Dispose()
        {
            var c = Interlocked.Exchange(ref _clock, null);
            if (c is null) return;
            c.PositionChanged -= _handler;
        }
    }

    private sealed class PlaybackPlayheadAdapter(IPlayhead inner) : IReadOnlyPlayhead
    {
        public TimeSpan CurrentPosition => inner.CurrentPosition;

        public bool IsRunning => inner.IsRunning;

        public double PlaybackRate => inner.PlaybackRate;
    }
}
