using Microsoft.Extensions.Logging;
using S.Media.Core.Threading;
using S.Media.Core.Video;

namespace S.Media.FFmpeg.Video;

/// <summary>Optional <see cref="VideoSinkPump"/> wrapping when registering an output on <see cref="VideoRouter"/>.</summary>
/// <param name="MaxQueuedFrames">Queue depth before drop-oldest (must be ≥ 1).</param>
/// <param name="ThreadName">Background drainer thread name; defaults from the router output id when omitted.</param>
/// <param name="Logger">Optional logger for pump diagnostics.</param>
/// <param name="DisposeInnerSinkWhenPumpDisposes">
/// When false, the inner sink is not disposed with the pump (use when the inner is owned elsewhere, e.g. an NDI sender).
/// </param>
public readonly record struct VideoSinkPumpAttachOptions(
    int MaxQueuedFrames = 3,
    string? ThreadName = null,
    ILogger? Logger = null,
    bool DisposeInnerSinkWhenPumpDisposes = true);

/// <summary>
/// Bounded asynchronous delivery to an <see cref="IVideoSink"/>: <see cref="Submit"/> returns quickly
/// after enqueueing; a drainer thread calls <see cref="IVideoSink.Submit"/>. When the queue is full,
/// the <strong>oldest</strong> frame is disposed and replaced (drop-late policy).
/// </summary>
/// <remarks>
/// Intended for slow network encoders (for example NDI) so a blocking <see cref="IVideoSink.Submit"/>
/// cannot stall upstream decode. This is the video analogue of audio <c>SinkPump</c> back-pressure,
/// without integrating directly into <see cref="VideoRouter"/> yet.
/// </remarks>
public sealed class VideoSinkPump : IVideoSink, IDisposable
{
    private readonly IVideoSink _inner;
    private readonly bool _disposeInner;
    private readonly int _maxQueued;
    private readonly ILogger? _log;
    private readonly string _name;
    private readonly object _gate = new();
    private readonly Queue<VideoFrame> _queue = new();
    private readonly ManualResetEventSlim _pending = new(false);
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _configured;
    private bool _disposed;
    private long _dropped;
    private long _submitted;

    public VideoSinkPump(IVideoSink inner, int maxQueuedFrames = 3, string name = "VideoSinkPump", ILogger? log = null,
        bool disposeInnerOnDispose = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxQueuedFrames < 1) throw new ArgumentOutOfRangeException(nameof(maxQueuedFrames));
        _inner = inner;
        _disposeInner = disposeInnerOnDispose;
        _maxQueued = maxQueuedFrames;
        _name = name;
        _log = log;
    }

    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long SubmittedFrames => Interlocked.Read(ref _submitted);

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;

    public VideoFormat Format => _inner.Format;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_thread is not null)
                throw new InvalidOperationException("VideoSinkPump.Configure may only be called once.");
            _inner.Configure(format);
            _configured = true;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token))
        {
            IsBackground = true,
            Name = _name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
        {
            frame.Dispose();
            throw new InvalidOperationException("VideoSinkPump.Submit called before Configure");
        }

        lock (_gate)
        {
            while (_queue.Count >= _maxQueued)
            {
                var victim = _queue.Dequeue();
                victim.Dispose();
                Interlocked.Increment(ref _dropped);
                _log?.LogWarning("VideoSinkPump {Name}: queue full — dropped oldest frame (total drops {Drops})", _name,
                    Interlocked.Read(ref _dropped));
            }

            _queue.Enqueue(frame);
        }

        _pending.Set();
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _pending.Wait(250, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (token.IsCancellationRequested) break;
            while (true)
            {
                VideoFrame? next = null;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _pending.Reset();
                        break;
                    }

                    next = _queue.Dequeue();
                }

                if (next is null) break;
                try
                {
                    _inner.Submit(next);
                    Interlocked.Increment(ref _submitted);
                }
                catch (Exception ex)
                {
                    _log?.LogError(ex, "VideoSinkPump {Name}: inner Submit failed", _name);
                    next.Dispose();
                }
            }
        }

        lock (_gate)
        {
            while (_queue.Count > 0)
                _queue.Dequeue().Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts?.Cancel();
            _pending.Set();
            if (_thread is { } t)
                CooperativePlaybackJoin.JoinThread(t, TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        finally
        {
            _cts?.Dispose();
            _pending.Dispose();
            lock (_gate)
            {
                while (_queue.Count > 0)
                    _queue.Dequeue().Dispose();
            }

            if (_disposeInner && _inner is IDisposable d)
                d.Dispose();
        }
    }
}
