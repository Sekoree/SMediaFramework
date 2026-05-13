using S.Media.Core.Playback;

namespace S.Media.Core.Clock;

/// <summary>
/// Strategy‑B helpers: <see cref="IPlaybackTimeline"/> does not carry tick events — use <see cref="IMediaClock"/>
/// (or this type's extensions) to subscribe to <see cref="IMediaClock.PositionChanged"/>.
/// </summary>
public static class PlaybackTimelineClockExtensions
{
    /// <summary>
    /// Subscribes <paramref name="handler"/> to <see cref="IAvPlaybackSession.Clock"/> <see cref="IMediaClock.PositionChanged"/>.
    /// </summary>
    public static IDisposable SubscribePositionChanged(this IAvPlaybackSession session, EventHandler<TimeSpan> handler)
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
}
