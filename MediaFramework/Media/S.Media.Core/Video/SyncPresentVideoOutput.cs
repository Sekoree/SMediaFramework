using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Video;

/// <summary>
/// An <see cref="IVideoOutput"/> that buffers submitted frames and presents them only when a
/// <see cref="VideoPresentSyncGroup"/> tells it to, so several physical outputs present the frame for one
/// shared reference timestamp in lock-step. The concrete member of the video genlock domain (issues-doc #2,
/// Option&nbsp;B, Phase&nbsp;2b — see <c>Doc/HaPlay-MultiOutput-Sync.md</c>); the audio analogue is
/// <c>AdaptiveRateAudioOutput</c>.
/// </summary>
/// <remarks>
/// <para>
/// Wrap the real device output (an SDL/GL output, an NDI sender, …) in one of these, add it to a
/// <see cref="VideoPresentSyncGroup"/>, and feed frames via <see cref="Submit"/> as usual — the router /
/// player keeps producing, this just <em>defers the present</em> to the group's tick. The inner output
/// should present promptly on <see cref="IVideoOutput.Submit"/> (a directly-presenting display, not another
/// async <see cref="VideoOutputPump"/> — that would re-introduce the independent cadence this removes).
/// </para>
/// <para>
/// Frame ownership: this output takes ownership of every submitted frame. Frames dropped for capacity or
/// skipped during a coordinated catch-up are disposed here; the one frame handed to the inner output on a
/// present transfers ownership to the inner output (per <see cref="IVideoOutput.Submit"/>).
/// </para>
/// </remarks>
public sealed class SyncPresentVideoOutput : IVideoOutput, ISyncPresentableVideoOutput, IDisposable
{
    private readonly IVideoOutput _inner;
    private readonly bool _disposeInner;
    private readonly int _maxBuffered;
    private readonly string _name;
    private readonly object _gate = new();
    private readonly List<VideoFrame> _buffer = new();
    private bool _configured;
    private bool _disposed;
    private TimeSpan? _lastPresentedPts;
    private long _dropped;
    private long _presented;
    private long _lastDropLogTicks;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Video.SyncPresentVideoOutput");

    /// <param name="inner">The directly-presenting device output this wraps.</param>
    /// <param name="maxBufferedFrames">Unpresented-frame capacity before drop-oldest (must be ≥ 1). Keep small — buffering adds latency.</param>
    /// <param name="name">Diagnostic name.</param>
    /// <param name="disposeInnerOnDispose">Dispose the inner output when this is disposed.</param>
    public SyncPresentVideoOutput(IVideoOutput inner, int maxBufferedFrames = 4, string name = "SyncPresentVideoOutput",
        bool disposeInnerOnDispose = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (maxBufferedFrames < 1) throw new ArgumentOutOfRangeException(nameof(maxBufferedFrames));
        _inner = inner;
        _maxBuffered = maxBufferedFrames;
        _name = name;
        _disposeInner = disposeInnerOnDispose;
    }

    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long PresentedFrames => Interlocked.Read(ref _presented);

    /// <summary>Unpresented frames currently buffered (snapshot).</summary>
    public int BufferedFrameCount
    {
        get { lock (_gate) return _buffer.Count; }
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;

    public VideoFormat Format => _inner.Format;

    public bool IsPresentable
    {
        get { lock (_gate) return _configured && !_disposed; }
    }

    public void Configure(VideoFormat format)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A real format change must not leave old-format frames buffered for the reconfigured inner.
            if (_configured && format != _inner.Format)
                DrainBufferLocked();
            _inner.Configure(format);
            _configured = true;
        }
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VideoFrame? victim = null;
        var droppedStale = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_configured)
            {
                frame.Dispose();
                throw new InvalidOperationException("SyncPresentVideoOutput.Submit called before Configure");
            }

            // Drop a frame that is not newer than what we already presented — it can never be selected and
            // would otherwise let a coordinated catch-up "present backwards".
            if (_lastPresentedPts is { } last && frame.PresentationTime <= last)
            {
                droppedStale = true;
            }
            else
            {
                _buffer.Add(frame);
                if (_buffer.Count > _maxBuffered)
                {
                    victim = _buffer[0];
                    _buffer.RemoveAt(0);
                }
            }
        }

        if (droppedStale)
        {
            frame.Dispose();
            RecordDrop();
            return;
        }

        if (victim is not null)
        {
            victim.Dispose();
            RecordDrop();
            ThrottledWarnDrop();
        }
    }

    public bool TryPeekReadyPts(TimeSpan target, out TimeSpan readyPts)
    {
        readyPts = default;
        var found = false;
        lock (_gate)
        {
            if (_disposed || !_configured) return false;
            foreach (var f in _buffer)
            {
                var pts = f.PresentationTime;
                if (pts <= target && (!found || pts > readyPts))
                {
                    readyPts = pts;
                    found = true;
                }
            }
        }
        return found;
    }

    public VideoSyncPresentOutcome PresentUpTo(TimeSpan target)
    {
        VideoFrame? toPresent = null;
        List<VideoFrame>? toDrop = null;
        lock (_gate)
        {
            if (_disposed || !_configured || _buffer.Count == 0)
                return VideoSyncPresentOutcome.NoChange;

            // Pick the newest buffered frame at or before target.
            var chosen = -1;
            var chosenPts = TimeSpan.MinValue;
            for (var i = 0; i < _buffer.Count; i++)
            {
                var pts = _buffer[i].PresentationTime;
                if (pts <= target && (chosen < 0 || pts >= chosenPts))
                {
                    chosen = i;
                    chosenPts = pts;
                }
            }

            if (chosen < 0)
                return VideoSyncPresentOutcome.NoChange;

            // Keep frames strictly newer than the chosen one; drop the rest (the skipped older frames).
            var keep = new List<VideoFrame>(_buffer.Count);
            for (var i = 0; i < _buffer.Count; i++)
            {
                if (i == chosen) { toPresent = _buffer[i]; continue; }
                if (_buffer[i].PresentationTime > chosenPts) keep.Add(_buffer[i]);
                else (toDrop ??= []).Add(_buffer[i]);
            }
            _buffer.Clear();
            _buffer.AddRange(keep);
            _lastPresentedPts = chosenPts;
        }

        if (toDrop is not null)
        {
            foreach (var f in toDrop)
            {
                f.Dispose();
                Interlocked.Increment(ref _dropped);
            }
        }

        try
        {
            _inner.Submit(toPresent!);
            Interlocked.Increment(ref _presented);
        }
        catch (Exception ex)
        {
            toPresent!.Dispose();
            Trace.LogError(ex, "{Name}: inner Submit failed — frame dropped", _name);
            Interlocked.Increment(ref _dropped);
        }

        return VideoSyncPresentOutcome.Presented;
    }

    private void RecordDrop() => Interlocked.Increment(ref _dropped);

    private void ThrottledWarnDrop()
    {
        var now = Environment.TickCount64;
        var prev = Volatile.Read(ref _lastDropLogTicks);
        if ((now - prev < 2000 && prev != 0) ||
            Interlocked.CompareExchange(ref _lastDropLogTicks, now, prev) != prev)
            return;
        MediaDiagnostics.LogWarning(
            $"SyncPresentVideoOutput '{_name}': buffer full — dropped oldest frame(s); total DroppedFrames={Interlocked.Read(ref _dropped)}. " +
            "The present scheduler is not draining fast enough, or this member is over-fed; raise maxBufferedFrames or check the group tick rate.");
    }

    private void DrainBufferLocked()
    {
        foreach (var f in _buffer)
        {
            f.Dispose();
            Interlocked.Increment(ref _dropped);
        }
        _buffer.Clear();
    }

    public void Dispose()
    {
        bool disposeInner;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DrainBufferLocked();
            disposeInner = _disposeInner;
        }

        if (disposeInner && _inner is IDisposable d)
            MediaDiagnostics.SwallowDisposeErrors(d.Dispose, $"SyncPresentVideoOutput.Dispose: inner output ({_name})");
    }
}
