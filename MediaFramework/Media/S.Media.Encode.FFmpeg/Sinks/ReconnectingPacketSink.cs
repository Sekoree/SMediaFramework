using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace S.Media.Encode.FFmpeg.Sinks;

/// <summary>
/// Keeps a live network destination attached across transient connect/write failures. Reconnection
/// runs on the surrounding <see cref="AsyncPacketSink"/> drain thread, never on the encoder thread,
/// and resumes at a video keyframe so the new MPEG-TS/FLV/RTSP session starts decodably. Audio-only
/// streams may reconnect on any packet.
/// </summary>
internal sealed unsafe class ReconnectingPacketSink : IEncodedPacketSink, IEncodedPacketSinkHealth
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Encode.FFmpeg.ReconnectingPacketSink");

    private readonly string _name;
    private readonly Func<IEncodedPacketSink> _factory;
    private readonly TimeSpan _minimumRetryDelay;
    private readonly TimeSpan _maximumRetryDelay;
    private IReadOnlyList<EncodedStreamInfo>? _streams;
    private IEncodedPacketSink? _inner;
    private long _completedBytes;
    private long _nextAttempt;
    private int _consecutiveFailures;
    private bool _hasVideo;
    private bool _finished;
    private bool _disposed;
    private volatile bool _healthy;
    private volatile string? _error;

    public ReconnectingPacketSink(
        string name,
        Func<IEncodedPacketSink> factory,
        TimeSpan? minimumRetryDelay = null,
        TimeSpan? maximumRetryDelay = null)
    {
        _name = string.IsNullOrWhiteSpace(name) ? "network push" : name;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _minimumRetryDelay = minimumRetryDelay ?? TimeSpan.FromSeconds(1);
        _maximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(30);
        if (_minimumRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumRetryDelay));
        if (_maximumRetryDelay < _minimumRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(maximumRetryDelay));
    }

    public string Name => _name;

    public long BytesWritten
    {
        get
        {
            var current = _inner;
            return Interlocked.Read(ref _completedBytes) + (current?.BytesWritten ?? 0);
        }
    }

    public bool Healthy => _healthy;

    public string? Error => _error;

    public void OnStreamsReady(IReadOnlyList<EncodedStreamInfo> streams)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_streams is not null)
            throw new InvalidOperationException("OnStreamsReady called twice.");

        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _hasVideo = streams.Any(s => s.Kind == EncodedStreamKind.Video);
        TryConnect();
    }

    public void OnPacket(AVPacket* packet, bool keyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished || _streams is null)
            return;

        if (_inner is null)
        {
            if (Stopwatch.GetTimestamp() < _nextAttempt || !IsReconnectBoundary(packet, keyframe))
                return;
            if (!TryConnect())
                return;
        }

        try
        {
            _inner!.OnPacket(packet, keyframe);
        }
        catch (Exception ex)
        {
            Disconnect(ex);
        }
    }

    public void Finish()
    {
        if (_disposed || _finished)
            return;
        _finished = true;

        if (_inner is null)
            return;
        try
        {
            _inner.Finish();
        }
        catch (Exception ex)
        {
            Disconnect(ex, scheduleRetry: false);
        }
    }

    private bool IsReconnectBoundary(AVPacket* packet, bool keyframe)
    {
        if (!_hasVideo)
            return true;
        if (!keyframe || packet is null || packet->stream_index < 0 || packet->stream_index >= _streams!.Count)
            return false;
        return _streams[packet->stream_index].Kind == EncodedStreamKind.Video;
    }

    private bool TryConnect()
    {
        IEncodedPacketSink? candidate = null;
        try
        {
            candidate = _factory();
            candidate.OnStreamsReady(_streams!);
            _inner = candidate;
            _consecutiveFailures = 0;
            _healthy = true;
            _error = null;
            Trace.LogInformation("push sink '{Name}' connected", Name);
            return true;
        }
        catch (Exception ex)
        {
            if (candidate is not null)
                DisposeInner(candidate);
            RegisterFailure(ex);
            return false;
        }
    }

    private void Disconnect(Exception error, bool scheduleRetry = true)
    {
        var failed = _inner;
        _inner = null;
        if (failed is not null)
        {
            Interlocked.Add(ref _completedBytes, failed.BytesWritten);
            DisposeInner(failed);
        }

        if (scheduleRetry)
            RegisterFailure(error);
        else
        {
            _healthy = false;
            _error = error.Message;
        }
    }

    private void RegisterFailure(Exception error)
    {
        _healthy = false;
        _error = error.Message;
        _consecutiveFailures++;

        var exponent = Math.Min(10, _consecutiveFailures - 1);
        var delayMs = Math.Min(
            _maximumRetryDelay.TotalMilliseconds,
            _minimumRetryDelay.TotalMilliseconds * (1L << exponent));
        var retryDelay = TimeSpan.FromMilliseconds(delayMs);
        _nextAttempt = Stopwatch.GetTimestamp() + (long)Math.Ceiling(retryDelay.TotalSeconds * Stopwatch.Frequency);
        Trace.LogWarning(error,
            "push sink '{Name}' disconnected; retrying on a keyframe in {Delay:F1}s",
            Name, retryDelay.TotalSeconds);
    }

    private static void DisposeInner(IEncodedPacketSink sink) =>
        MediaDiagnostics.SwallowDisposeErrors(sink.Dispose, $"ReconnectingPacketSink.Dispose: {sink.Name}");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _healthy = false;

        var inner = _inner;
        _inner = null;
        if (inner is not null)
        {
            Interlocked.Add(ref _completedBytes, inner.BytesWritten);
            DisposeInner(inner);
        }
    }
}
