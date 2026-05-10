using System.Diagnostics;

namespace S.Media.Core.Clock;

/// <summary>
/// Master playback clock. Free-running by default (backed by
/// <see cref="Stopwatch"/>); call <see cref="SetMaster"/> to slave it to an
/// external <see cref="IPlaybackClock"/> (typically the audio sink) so
/// reported position tracks actual played samples instead of wall time.
/// </summary>
/// <remarks>
/// <para>
/// Tick events (<see cref="AudioTick"/>, <see cref="VideoTick"/>,
/// <see cref="PositionChanged"/>) are driven by an internal wall-clock thread
/// regardless of master attachment — they're "render at this cadence" signals,
/// not "media time advanced by X." Subscribers should marshal to their own
/// context (UI thread, etc.) if needed; one throwing handler will not stop
/// the loop.
/// </para>
/// <para>
/// Coordination caveat: when the master keeps advancing (e.g. PortAudio is
/// still playing buffered audio) but the clock is paused, the apparent
/// position freezes while real audio continues. Pause the audio sink first if
/// you want both to stop together.
/// </para>
/// </remarks>
public sealed class MediaClock : IMediaClock, IDisposable
{
    private static readonly TimeSpan DefaultAudioTickInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DefaultVideoTickInterval = TimeSpan.FromTicks(166_667); // ~60 Hz
    private static readonly TimeSpan PositionChangedInterval  = TimeSpan.FromMilliseconds(33); // ~30 Hz

    private readonly Stopwatch _stopwatch = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _audioTickInterval;
    private readonly TimeSpan _videoTickInterval;

    private TimeSpan _basePosition;
    private IPlaybackClock? _master;
    private TimeSpan _masterAnchor;
    private bool _isRunning;
    private bool _disposed;

    private Thread? _driverThread;
    private CancellationTokenSource? _driverCts;

    public MediaClock() : this(DefaultAudioTickInterval, DefaultVideoTickInterval) { }

    public MediaClock(TimeSpan audioTickInterval, TimeSpan videoTickInterval)
    {
        if (audioTickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(audioTickInterval));
        if (videoTickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(videoTickInterval));
        _audioTickInterval = audioTickInterval;
        _videoTickInterval = videoTickInterval;
    }

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? AudioTick;
    public event EventHandler? VideoTick;

    public TimeSpan CurrentPosition
    {
        get { lock (_gate) return ComputePositionUnlocked(); }
    }

    /// <summary>The currently attached master, or <c>null</c> when the clock is in stopwatch mode.</summary>
    public IPlaybackClock? Master
    {
        get { lock (_gate) return _master; }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _isRunning; }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_isRunning) return;
            _isRunning = true;
            if (_master is not null)
                _masterAnchor = _master.ElapsedSinceStart;
            else
                _stopwatch.Start();
            StartDriver();
        }
    }

    public void Pause()
    {
        Thread? toJoin;
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_isRunning) return;
            _basePosition = ComputePositionUnlocked(); // capture before stopping
            if (_master is null) _stopwatch.Reset();
            _isRunning = false;
            (toJoin, toDispose) = DetachDriver();
        }
        JoinDriver(toJoin, toDispose);
    }

    /// <summary>Same as <see cref="Pause"/> for now — semantics may diverge later.</summary>
    public void Stop() => Pause();

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _basePosition = TimeSpan.Zero;
            if (_master is not null) _masterAnchor = _master.ElapsedSinceStart;
            else if (_isRunning) _stopwatch.Restart();
            else _stopwatch.Reset();
        }
        RaisePositionChanged(TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(position));
        lock (_gate)
        {
            ThrowIfDisposed();
            _basePosition = position;
            if (_master is not null) _masterAnchor = _master.ElapsedSinceStart;
            else if (_isRunning) _stopwatch.Restart();
            else _stopwatch.Reset();
        }
        RaisePositionChanged(position);
    }

    public void SetMaster(IPlaybackClock? master)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            // Capture current apparent position so the swap is continuous.
            var current = ComputePositionUnlocked();
            _master = master;
            _basePosition = current;
            if (master is not null)
            {
                _masterAnchor = master.ElapsedSinceStart;
                _stopwatch.Reset();
            }
            else if (_isRunning)
            {
                _stopwatch.Restart();
            }
        }
    }

    public void Dispose()
    {
        Thread? toJoin;
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopwatch.Stop();
            _isRunning = false;
            (toJoin, toDispose) = DetachDriver();
        }
        JoinDriver(toJoin, toDispose);
    }

    // --- driver thread -----------------------------------------------------

    private void StartDriver()
    {
        // called under _gate
        if (_driverThread is { IsAlive: true }) return;
        _driverCts = new CancellationTokenSource();
        var token = _driverCts.Token;
        _driverThread = new Thread(() => DriverLoop(token))
        {
            IsBackground = true,
            Name = "MediaClock.Driver",
            Priority = ThreadPriority.AboveNormal,
        };
        _driverThread.Start();
    }

    /// <summary>Detaches the current driver and signals cancellation. Joining is the caller's job (must be done outside <see cref="_gate"/>).</summary>
    private (Thread? thread, CancellationTokenSource? cts) DetachDriver()
    {
        var t = _driverThread;
        var cts = _driverCts;
        _driverThread = null;
        _driverCts = null;
        cts?.Cancel();
        return (t, cts);
    }

    private static void JoinDriver(Thread? thread, CancellationTokenSource? cts)
    {
        thread?.Join(TimeSpan.FromSeconds(1));
        cts?.Dispose();
    }

    private void DriverLoop(CancellationToken token)
    {
        var sessionStart  = Stopwatch.GetTimestamp();
        var nextAudio     = _audioTickInterval;
        var nextVideo     = _videoTickInterval;
        var nextPosition  = PositionChangedInterval;

        while (!token.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(sessionStart);
            var nextDeadline = Min(nextAudio, Min(nextVideo, nextPosition));
            var sleep = nextDeadline - elapsed;

            if (sleep > TimeSpan.Zero)
            {
                if (token.WaitHandle.WaitOne(sleep)) break;
            }

            elapsed = Stopwatch.GetElapsedTime(sessionStart);

            if (elapsed >= nextAudio)
            {
                SafeInvoke(AudioTick);
                while (nextAudio <= elapsed) nextAudio += _audioTickInterval;
            }
            if (elapsed >= nextVideo)
            {
                SafeInvoke(VideoTick);
                while (nextVideo <= elapsed) nextVideo += _videoTickInterval;
            }
            if (elapsed >= nextPosition)
            {
                RaisePositionChanged(CurrentPosition);
                while (nextPosition <= elapsed) nextPosition += PositionChangedInterval;
            }
        }
    }

    private void SafeInvoke(EventHandler? handler)
    {
        if (handler is null) return;
        try { handler.Invoke(this, EventArgs.Empty); }
        catch { /* a misbehaving subscriber must not kill the driver */ }
    }

    private void RaisePositionChanged(TimeSpan position)
    {
        var handler = PositionChanged;
        if (handler is null) return;
        try { handler.Invoke(this, position); }
        catch { /* see SafeInvoke */ }
    }

    private TimeSpan ComputePositionUnlocked()
    {
        if (!_isRunning) return _basePosition;
        if (_master is not null)
        {
            var delta = _master.ElapsedSinceStart - _masterAnchor;
            // Defensive clamp: a misbehaving master that goes backwards
            // shouldn't make our position go backwards either.
            if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
            return _basePosition + delta;
        }
        return _basePosition + _stopwatch.Elapsed;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MediaClock));
    }
}
