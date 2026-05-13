using System.Collections.Concurrent;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Core.Video;

/// <summary>
/// Glues an <see cref="IVideoSource"/>, an <see cref="IVideoSink"/>, and an
/// <see cref="IMediaClock"/> together. A background decode thread keeps a
/// small presentation queue full; on each <see cref="IMediaClock.VideoTick"/>
/// the player picks the most recent frame whose PTS is at or before the
/// playhead, drops anything stale, and submits to the sink.
/// </summary>
/// <remarks>
/// <para>
/// Pacing model: the master clock decides when frames display; the source
/// just provides them in PTS order. For audio-mastered playback this means
/// the audio sink's <see cref="IPlaybackClock"/> drives the
/// <see cref="MediaClock"/>, and the video player follows along. With no
/// master attached (free-running stopwatch), wall time paces the playback.
/// </para>
/// <para>
/// Thread safety / latency: <see cref="IVideoSink.Submit"/> runs on the
/// <see cref="IMediaClock.VideoTick"/> thread (the clock's driver thread),
/// so sinks must return promptly — typically by handing the frame to a
/// render thread of their own. A slow Submit delays subsequent ticks for
/// every other subscriber on the same clock.
/// </para>
/// <para>
/// Frame negotiation runs at construction via
/// <see cref="VideoFormatNegotiator.Connect"/> (optional
/// <c>negotiatePixelFormats</c> predicate excludes pixel formats Core would
/// otherwise pick). The source and sink agree on a format before <see cref="Play"/>.
/// </para>
/// </remarks>
public sealed class VideoPlayer : IDisposable
{
    private readonly IVideoSource _source;
    private readonly IVideoSink _sink;
    private readonly IMediaClock _clock;
    private readonly ConcurrentQueue<VideoFrame> _queue = new();
    private SemaphoreSlim _slotsAvailable;
    private readonly int _queueCapacity;
    private readonly TimeSpan _decodePollInterval;
    private readonly Lock _gate = new();

    private Thread? _decodeThread;
    private CancellationTokenSource? _cts;
    private EventHandler? _tickHandler;
    private bool _isRunning;
    private bool _disposed;

    private long _decoded;
    private long _displayed;
    private long _droppedLate;
    private long _droppedQueueDrain;

    /// <summary>
    /// Raised after a frame is successfully submitted to the sink with its
    /// <see cref="VideoFrame.PresentationTime"/> (for example to feed a
    /// <see cref="VideoPtsClock"/>).
    /// </summary>
    /// <remarks>Runs on the <see cref="IMediaClock"/> driver thread (same thread as <see cref="IMediaClock.VideoTick"/>). Keep handlers lightweight or marshal work elsewhere.</remarks>
    public event Action<TimeSpan>? FramePresentationTimePresented;

    /// <summary>The clock that paces <see cref="IMediaClock.VideoTick"/> and <see cref="Play"/>.</summary>
    public IMediaClock Clock => _clock;

    public VideoFormat Format => _sink.Format;
    public bool IsRunning { get { lock (_gate) return _isRunning; } }

    /// <summary>True after the source signalled <see cref="IVideoSource.IsExhausted"/> AND every queued frame has been displayed.</summary>
    public bool CompletedNaturally { get; private set; }

    /// <summary>Total frames pulled from the source.</summary>
    public long DecodedCount => Volatile.Read(ref _decoded);
    /// <summary>Total frames handed to the sink.</summary>
    public long DisplayedCount => Volatile.Read(ref _displayed);
    /// <summary>Frames dropped because the playhead had advanced past them by more than <see cref="LateThreshold"/>.</summary>
    public long DroppedLate => Volatile.Read(ref _droppedLate);
    /// <summary>Frames dropped on Stop/Pause/Seek (queue drain) before they could be displayed.</summary>
    public long DroppedDrain => Volatile.Read(ref _droppedQueueDrain);

    /// <summary>
    /// A frame whose PTS is more than this far behind the playhead is
    /// dropped instead of submitted. Default 150 ms.
    /// </summary>
    public TimeSpan LateThreshold { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// A frame whose PTS is at most this far ahead of the playhead is still
    /// considered ready to display. Default 8 ms (~half a 60 Hz tick).
    /// </summary>
    public TimeSpan EarlyTolerance { get; set; } = TimeSpan.FromMilliseconds(8);

    public VideoPlayer(IVideoSource source, IVideoSink sink, IMediaClock clock,
                       int queueCapacity = 4, TimeSpan? decodePollInterval = null,
                       Func<PixelFormat, bool>? negotiatePixelFormats = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity), "must be >= 1");

        _source = source;
        _sink = sink;
        _clock = clock;
        _queueCapacity = queueCapacity;
        _slotsAvailable = new SemaphoreSlim(queueCapacity, queueCapacity);
        _decodePollInterval = decodePollInterval ?? TimeSpan.FromMilliseconds(5);

        // Negotiate format up front so the sink is configured before any
        // frame arrives. Either side throws here if no compatible format.
        VideoFormatNegotiator.Connect(_source, _sink, negotiatePixelFormats);
    }

    /// <summary>Start decoding and scheduling. Idempotent.</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_isRunning) return;
            CompletedNaturally = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _tickHandler = (_, _) => OnVideoTick();
            _clock.VideoTick += _tickHandler;

            _decodeThread = new Thread(() => DecodeLoop(token))
            {
                IsBackground = true,
                Name = "VideoPlayer.Decode",
            };
            _isRunning = true;
            _decodeThread.Start();
        }
    }

    /// <summary>
    /// Stop scheduling and decoding, drop any queued frames. Use this for
    /// both pause and stop semantics — when the master clock is also paused,
    /// the displayed frame just freezes anyway.
    /// </summary>
    /// <param name="cancellationToken">Cooperative cancel while joining the decode thread.</param>
    public void Pause(CancellationToken cancellationToken = default) => StopInternal(cancellationToken);

    /// <summary>Alias for <see cref="Pause(CancellationToken)"/>.</summary>
    public void Stop(CancellationToken cancellationToken = default) => StopInternal(cancellationToken);

    /// <summary>
    /// Coordinated seek: pauses, calls <see cref="ISeekableSource.Seek"/> on
    /// the source (must implement it — throws otherwise), and resumes if
    /// the player was running.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_source is not ISeekableSource seekable)
            throw new InvalidOperationException("source does not implement ISeekableSource");

        var wasRunning = IsRunning;
        if (wasRunning) StopInternal(CancellationToken.None);
        seekable.Seek(position);
        if (wasRunning) Play();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopInternal(CancellationToken.None); }
#if DEBUG
        catch (Exception ex) { MediaDiagnostics.LogError(ex, "VideoPlayer.Dispose: StopInternal"); }
#else
        catch { /* best effort */ }
#endif
        _slotsAvailable.Dispose();
    }

    // --- internals ---------------------------------------------------------

    private void StopInternal(CancellationToken cancellationToken = default)
    {
        Thread? toJoin;
        CancellationTokenSource? toDispose;
        EventHandler? handler;
        lock (_gate)
        {
            if (!_isRunning) return;
            _isRunning = false;
            handler = _tickHandler;
            _tickHandler = null;
            toJoin = _decodeThread;
            toDispose = _cts;
            _decodeThread = null;
            _cts = null;
        }

        if (handler is not null)
            _clock.VideoTick -= handler;
        toDispose?.Cancel();
        try
        {
            CooperativePlaybackJoin.JoinThreadWhileCancelable(toJoin, cancellationToken);
        }
        finally
        {
            toDispose?.Dispose();
        }

        DrainQueue();
    }

    private void DrainQueue()
    {
        // Reset the slot semaphore wholesale: anything we drop here returns
        // a slot, and the next Play wants the full capacity available.
        var dropped = 0;
        while (_queue.TryDequeue(out var f))
        {
            f.Dispose();
            dropped++;
        }
        if (dropped > 0) Interlocked.Add(ref _droppedQueueDrain, dropped);

        // Reissue the semaphore so the new run's decode loop has a clean
        // tally — simpler than counting precisely how many tokens to release.
        _slotsAvailable.Dispose();
        _slotsAvailable = new SemaphoreSlim(_queueCapacity, _queueCapacity);
    }

    private void DecodeLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_source.IsExhausted) return;

                if (!_source.TryReadNextFrame(out var frame))
                {
                    // Live source not ready (or end-of-stream lag). Brief sleep
                    // so we don't spin; cancellation is honoured promptly.
                    if (token.WaitHandle.WaitOne(_decodePollInterval)) return;
                    continue;
                }

                Interlocked.Increment(ref _decoded);

                try
                {
                    _slotsAvailable.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    frame.Dispose();
                    return;
                }
                _queue.Enqueue(frame);
            }
        }
        catch (ObjectDisposedException) { /* StopInternal disposed semaphore mid-wait */ }
    }

    private void OnVideoTick()
    {
        if (!IsRunning) return;

        var playhead = _clock.CurrentPosition;
        var early = playhead + EarlyTolerance;
        var lateCutoff = playhead - LateThreshold;

        VideoFrame? toShow = null;

        // Walk the queue forward as long as the next frame's PTS is in the
        // past (or within early tolerance). We always prefer the latest such
        // frame — older candidates are dropped as "skipped" when we find a
        // newer one. Frames more than LateThreshold behind are discarded
        // outright.
        while (_queue.TryPeek(out var head))
        {
            if (head.PresentationTime > early) break;

            if (!_queue.TryDequeue(out var frame))
                break;
            _slotsAvailable.Release();

            if (frame.PresentationTime < lateCutoff)
            {
                frame.Dispose();
                Interlocked.Increment(ref _droppedLate);
                continue;
            }

            if (toShow is not null)
            {
                toShow.Dispose();
                Interlocked.Increment(ref _droppedLate);
            }
            toShow = frame;
        }

        if (toShow is not null)
        {
            try
            {
                _sink.Submit(toShow);
                Interlocked.Increment(ref _displayed);
                FramePresentationTimePresented?.Invoke(toShow.PresentationTime);
            }
            catch (Exception ex)
            {
#if DEBUG
                MediaDiagnostics.LogError(ex, "VideoPlayer.OnVideoTick sink Submit");
#endif
                // Sink threw — make sure the frame is released to avoid a
                // native buffer leak; rethrow would kill MediaClock's driver.
                try { toShow.Dispose(); }
#if DEBUG
                catch (Exception dex) { MediaDiagnostics.LogError(dex, "VideoPlayer.OnVideoTick frame Dispose after Submit failure"); }
#else
                catch { /* best effort */ }
#endif
                _ = ex;
            }
        }
        else if (_source.IsExhausted && _queue.IsEmpty)
        {
            CompletedNaturally = true;
        }
    }
}
