namespace S.Media.Core;

/// <summary>Helpers for unified <see cref="IDisposable"/> release callbacks on frames.</summary>
public static class DisposableRelease
{
    /// <summary>Wraps an <see cref="Action"/> as an <see cref="IDisposable"/> (idempotent dispose).</summary>
    public static IDisposable Wrap(Action? release) =>
        release is null ? NoopDisposable.Instance : new ActionDisposable(release);

    /// <summary>Invokes <paramref name="primary"/> then <paramref name="secondary"/> on dispose.</summary>
    public static IDisposable Chain(IDisposable? primary, IDisposable? secondary)
    {
        if (primary is null) return secondary ?? NoopDisposable.Instance;
        if (secondary is null) return primary;
        return new ChainedDisposable(primary, secondary);
    }

    /// <summary>Wraps an action and an <see cref="IDisposable"/> - both run on dispose.</summary>
    public static IDisposable Combine(Action? action, IDisposable? disposable) =>
        Chain(disposable, Wrap(action));

    /// <summary>Disposes <paramref name="inner"/> after <paramref name="count"/> dispose calls.</summary>
    public static IDisposable SharedCountdown(IDisposable inner, int count) =>
        new SharedCountdownDisposable(inner, count);

    /// <summary>Adjusts a <see cref="SharedCountdown"/> when fan-out construction fails partway.</summary>
    internal static void AdjustSharedCountdown(IDisposable countdown, int delta)
    {
        if (countdown is SharedCountdownDisposable shared)
            shared.AddRemaining(delta);
    }

    private sealed class ActionDisposable(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            release();
        }
    }

    private sealed class ChainedDisposable(IDisposable first, IDisposable second) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            try { first.Dispose(); }
            finally { second.Dispose(); }
        }
    }

    private sealed class SharedCountdownDisposable(IDisposable inner, int count) : IDisposable
    {
        private int _remaining = count;

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
                inner.Dispose();
        }

        /// <summary>Adjusts the countdown when fan-out construction fails partway (see <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/>).</summary>
        internal void AddRemaining(int delta) => Interlocked.Add(ref _remaining, delta);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
