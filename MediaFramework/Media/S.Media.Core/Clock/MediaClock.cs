using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Core.Clock;

/// <summary>
/// Master playback clock. Free-running by default (backed by
/// <see cref="Stopwatch"/>); call <see cref="SetMaster"/> (or
/// <see cref="MediaClockExtensions.SetMasterChain"/> for <see cref="IMediaClock"/>) to slave it to an external
/// <see cref="IPlaybackClock"/> (typically the audio output) so reported position
/// tracks actual played samples instead of wall time.
/// </summary>
/// <remarks>
/// <para>
/// Tick events (<see cref="AudioTick"/>, <see cref="VideoTick"/>,
/// <see cref="PositionChanged"/>) are driven by an internal wall-clock thread
/// regardless of master attachment — they're "render at this cadence" signals,
/// not "media time advanced by X." When a tick handler runs long or the thread
/// wakes late, the driver <strong>bursts</strong> missed deadlines (capped) and
/// then fast-forwards the schedule so a long stall does not freeze the process.
/// </para>
/// <para>
/// <see cref="PositionChanged"/> is usually raised from the driver thread at
/// ~30 Hz. <see cref="Reset"/> and <see cref="Seek"/> raise it synchronously
/// on the caller's thread immediately after updating the stored position —
/// marshal if your UI requires a single context.
/// </para>
/// <para>
/// When a master clock is attached, <see cref="Pause"/> snapshots how far the
/// master had advanced; <see cref="Start"/> adds any additional master
/// elapsed time that accrued while paused (e.g. PortAudio still draining) so
/// the playhead stays aligned with heard audio.
/// </para>
/// <para>
/// Graph-wide coordinated master pitch (PPM), synchronized drop/repeat across multiple outputs, or other
/// timing policy beyond what individual <see cref="IPlaybackClock"/> instances report is <strong>host-owned</strong>
/// (see <see cref="Audio.AudioRouter"/> remarks, <see cref="Audio.PumpPressurePlaybackHintMonitor"/> for queue-drop hints,
/// and the FFmpeg <c>AdaptiveRateAudioOutput</c> adapter for optional per-output resampling).
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
    private readonly ILogger? _log;

    private TimeSpan _basePosition;
    private IPlaybackClock? _master;
    private TimeSpan _masterAnchor;
    /// <summary>Master elapsed at last <see cref="Pause"/> — used to fold in audio that played during pause.</summary>
    private TimeSpan? _masterElapsedWhenPaused;

    private bool _isRunning;
    private bool _disposed;

    private Thread? _driverThread;
    private CancellationTokenSource? _driverCts;

    private static readonly ILogger TraceLog = MediaDiagnostics.CreateLogger("S.Media.Core.Clock.MediaClock");

    public MediaClock() : this(DefaultAudioTickInterval, DefaultVideoTickInterval, logger: null) { }

    public MediaClock(ILogger? logger)
        : this(DefaultAudioTickInterval, DefaultVideoTickInterval, logger) { }

    public MediaClock(TimeSpan audioTickInterval, TimeSpan videoTickInterval, ILogger? logger = null)
    {
        if (audioTickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(audioTickInterval));
        if (videoTickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(videoTickInterval));
        _audioTickInterval = audioTickInterval;
        _videoTickInterval = videoTickInterval;
        _log = logger;
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

    /// <inheritdoc cref="IPlayhead.PlaybackRate"/>
    public double PlaybackRate => 1.0;

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_isRunning)
            {
                TraceLog.LogTrace("Start: already running (position={Position})", ComputePositionUnlocked());
                return;
            }
            TraceLog.LogDebug("Start: master={Master} position={Position}",
                _master?.GetType().Name ?? "(stopwatch)", _basePosition);
            _isRunning = true;
            if (_master is not null)
            {
                if (_masterElapsedWhenPaused is { } pausedAt)
                {
                    var now = _master.ElapsedSinceStart;
                    var drift = now - pausedAt;
                    if (drift > TimeSpan.Zero)
                    {
                        _basePosition += drift;
                        TraceLog.LogDebug(
                            "Start: folded master drift while paused pausedAt={PausedAt} now={Now} driftMs={DriftMs} position={Position}",
                            pausedAt, now, drift.TotalMilliseconds, _basePosition);
                    }
                    else if (drift < TimeSpan.Zero)
                    {
                        TraceLog.LogDebug(
                            "Start: master elapsed regressed during pause (flush/segment reset?) pausedAt={PausedAt} now={Now} driftMs={DriftMs} — not folding",
                            pausedAt, now, drift.TotalMilliseconds);
                    }
                    _masterElapsedWhenPaused = null;
                }
                _masterAnchor = _master.ElapsedSinceStart;
                TraceLog.LogDebug("Start: master anchor={Anchor} position={Position}",
                    _masterAnchor, ComputePositionUnlocked());
            }
            else
            {
                _stopwatch.Start();
            }
            StartDriver();
        }
    }

    public void Pause(CancellationToken cancellationToken = default)
    {
        Thread? toJoin;
        CancellationTokenSource? toDispose;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_isRunning) return;
            _basePosition = ComputePositionUnlocked();
            TraceLog.LogDebug("Pause: position={Position} master={Master}",
                _basePosition, _master?.GetType().Name ?? "(stopwatch)");
            if (_master is not null)
                _masterElapsedWhenPaused = _master.ElapsedSinceStart;
            else
                _stopwatch.Reset();
            _isRunning = false;
            (toJoin, toDispose) = DetachDriver();
        }
        JoinDriver(toJoin, toDispose, cancellationToken);
    }

    /// <summary>Same as <see cref="Pause"/> for now — semantics may diverge later.</summary>
    public void Stop(CancellationToken cancellationToken = default) => Pause(cancellationToken);

    public void Reset()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _basePosition = TimeSpan.Zero;
            _masterElapsedWhenPaused = null;
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
            TraceLog.LogDebug("Seek: from={From} to={To}", _basePosition, position);
            _basePosition = position;
            _masterElapsedWhenPaused = null;
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
            var current = ComputePositionUnlocked();
            TraceLog.LogDebug("SetMaster: prev={Prev} next={Next} positionAtSwap={Position}",
                _master?.GetType().Name ?? "(stopwatch)", master?.GetType().Name ?? "(stopwatch)", current);
            _master = master;
            _basePosition = current;
            _masterElapsedWhenPaused = null;
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
        JoinDriver(toJoin, toDispose, CancellationToken.None);
    }

    // --- driver thread -----------------------------------------------------

    private void StartDriver()
    {
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

    private (Thread? thread, CancellationTokenSource? cts) DetachDriver()
    {
        var t = _driverThread;
        var cts = _driverCts;
        _driverThread = null;
        _driverCts = null;
        cts?.Cancel();
        return (t, cts);
    }

    private static void JoinDriver(Thread? thread, CancellationTokenSource? cts, CancellationToken cancellationToken)
    {
        try
        {
            CooperativePlaybackJoin.JoinThreadWhileCancelable(thread, cancellationToken);
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private void DriverLoop(CancellationToken token)
    {
        var sessionStart  = Stopwatch.GetTimestamp();
        var nextAudio     = _audioTickInterval;
        var nextVideo     = _videoTickInterval;
        var nextPosition  = PositionChangedInterval;

        var waitHandle = token.WaitHandle;

        while (!token.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(sessionStart);
            var nextDeadline = Min(nextAudio, Min(nextVideo, nextPosition));
            var sleep = nextDeadline - elapsed;

            if (sleep > TimeSpan.Zero)
            {
                if (waitHandle.WaitOne(sleep)) break;
            }

            elapsed = Stopwatch.GetElapsedTime(sessionStart);

            if (elapsed >= nextAudio)
            {
                var audioBurst = 0;
                while (elapsed >= nextAudio && audioBurst++ < 64)
                {
                    SafeInvoke(AudioTick);
                    nextAudio += _audioTickInterval;
                    elapsed = Stopwatch.GetElapsedTime(sessionStart);
                }

                while (nextAudio <= elapsed)
                    nextAudio += _audioTickInterval;
            }

            if (elapsed >= nextVideo)
            {
                var videoBurst = 0;
                while (elapsed >= nextVideo && videoBurst++ < 64)
                {
                    SafeInvoke(VideoTick);
                    nextVideo += _videoTickInterval;
                    elapsed = Stopwatch.GetElapsedTime(sessionStart);
                }

                while (nextVideo <= elapsed)
                    nextVideo += _videoTickInterval;
            }

            if (elapsed >= nextPosition)
            {
                var posBurst = 0;
                while (elapsed >= nextPosition && posBurst++ < 8)
                {
                    RaisePositionChanged(CurrentPosition);
                    nextPosition += PositionChangedInterval;
                    elapsed = Stopwatch.GetElapsedTime(sessionStart);
                }

                while (nextPosition <= elapsed)
                    nextPosition += PositionChangedInterval;
            }
        }
    }

    private void SafeInvoke(EventHandler? handler)
    {
        if (handler is null) return;
        try { handler.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            if (_log is { } l)
                l.LogError(ex, "MediaClock.{Event} subscriber threw", handler.Method.Name);
            else
                MediaDiagnostics.LogError(ex, $"MediaClock subscriber ({handler.Method.Name})");
        }
    }

    private void RaisePositionChanged(TimeSpan position)
    {
        var handler = PositionChanged;
        if (handler is null) return;
        try { handler.Invoke(this, position); }
        catch (Exception ex)
        {
            if (_log is { } l)
                l.LogError(ex, "MediaClock.PositionChanged subscriber threw");
            else
                MediaDiagnostics.LogError(ex, "MediaClock.PositionChanged subscriber");
        }
    }

    private TimeSpan ComputePositionUnlocked()
    {
        if (!_isRunning) return _basePosition;
        if (_master is not null)
        {
            var delta = _master.ElapsedSinceStart - _masterAnchor;
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
