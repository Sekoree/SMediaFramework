namespace S.Media.Core.Threading;

/// <summary>
/// Short-slice <see cref="Thread.Join(TimeSpan)"/> helpers so callers can honor
/// <see cref="CancellationToken"/> and avoid an unbounded block on shutdown.
/// </summary>
public static class CooperativePlaybackJoin
{
    /// <summary>Join until <paramref name="thread"/> exits or <paramref name="timeout"/> elapses.</summary>
    public static void JoinThread(Thread? thread, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (thread is null) return;

        var deadlineMs = Environment.TickCount64 + (long)Math.Ceiling(timeout.TotalMilliseconds);
        while (thread.IsAlive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadlineMs - Environment.TickCount64;
            if (remaining <= 0)
                break;
            var sliceMs = remaining > 32 ? 32 : remaining;
            thread.Join(TimeSpan.FromMilliseconds(sliceMs));
        }
    }

    /// <summary>Join until <paramref name="thread"/> exits or <paramref name="cancellationToken"/> is canceled.</summary>
    public static void JoinThreadWhileCancelable(Thread? thread, CancellationToken cancellationToken)
    {
        while (thread is { IsAlive: true })
        {
            cancellationToken.ThrowIfCancellationRequested();
            thread.Join(TimeSpan.FromMilliseconds(50));
        }
    }
}
