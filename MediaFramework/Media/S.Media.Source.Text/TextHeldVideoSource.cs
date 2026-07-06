using S.Media.Core;
using S.Media.Core.Audio; // ISeekableSource
using S.Media.Core.Video;

namespace S.Media.Source.Text;

/// <summary>
/// A single rendered text frame held for the cue's duration. Wraps <see cref="HeldFrameVideoSource"/> (format
/// negotiation + per-read owned clones) and adds a finite, seekable duration so the session can bound it like a
/// normal clip: it emits <c>duration × fps</c> identical frames then exhausts. A zero/absent duration leaves it
/// unbounded (holds until the cue is stopped or the next one fires — the same as a plain held source).
/// </summary>
internal sealed class TextHeldVideoSource : IVideoSource, ISeekableSource, IReplaceableFrameSource, IDisposable
{
    private readonly object _gate = new();
    private readonly Rational _frameRate;
    private readonly TimeSpan _duration; // reported so the session's end-monitor can stop the cue at its duration
    private HeldFrameVideoSource _inner;  // swapped by ReplaceFrame under _gate for a live text/style edit
    private PixelFormat _selectedFormat;
    private long _next;
    private bool _disposed;

    public TextHeldVideoSource(VideoFrame template, Rational frameRate, TimeSpan duration)
    {
        _inner = new HeldFrameVideoSource(template);
        _selectedFormat = _inner.Format.PixelFormat;
        _frameRate = frameRate;
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    public VideoFormat Format { get { lock (_gate) return _inner.Format; } }

    public IReadOnlyList<PixelFormat> NativePixelFormats { get { lock (_gate) return _inner.NativePixelFormats; } }

    // Deliberately UNBOUNDED — a held text frame never runs out. Ending is driven by the session's time-based
    // end-monitor (against the reported Duration), NOT by read count: a resize or live-edit re-primes the pipeline
    // and re-reads a burst of frames, and a read-count bound would then hit EOF and stop the cue mid-playback.
    public bool IsExhausted => InnerExhausted();

    public TimeSpan Duration => _duration;

    public TimeSpan Position => FramesToTime(Volatile.Read(ref _next));

    public void SelectOutputFormat(PixelFormat format)
    {
        lock (_gate)
        {
            _selectedFormat = format;
            _inner.SelectOutputFormat(format);
        }
    }

    public void Seek(TimeSpan position)
    {
        var fps = _frameRate.ToDouble();
        Volatile.Write(ref _next, fps > 0 ? Math.Max(0, (long)(position.TotalSeconds * fps)) : 0);
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        var index = Interlocked.Increment(ref _next) - 1;
        VideoFrame held;
        lock (_gate)
        {
            if (!_inner.TryReadNextFrame(out held))
            {
                frame = null!;
                return false;
            }
        }

        // Re-stamp the identical held frame with an ADVANCING presentation time. Otherwise every emitted frame
        // carries the template's single timestamp, so the player sees them all as "due now", drains its queue in
        // one go, and the source exhausts almost immediately. With a per-frame PTS the player paces the identical
        // frames across the cue's duration. Zero-copy: the emitted frame owns `held` via its release.
        frame = new VideoFrame(FramesToTime(index), held.Format, held.Planes, held.Strides, held.Metadata, release: held);
        return true;
    }

    /// <summary>Live-swap the rendered frame (a text/style edit on the playing cue). Re-selects the previously
    /// negotiated output format on the new frame so the pump keeps pulling in the same format, then disposes the
    /// old one. Never advances the playhead — the cue keeps its duration/position, only its pixels change.</summary>
    public void ReplaceFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }

            var next = new HeldFrameVideoSource(frame);
            next.SelectOutputFormat(_selectedFormat);
            var old = _inner;
            _inner = next;
            old.Dispose();
        }
    }

    private bool InnerExhausted()
    {
        lock (_gate)
            return _inner.IsExhausted;
    }

    private TimeSpan FramesToTime(long frames)
    {
        var fps = _frameRate.ToDouble();
        return fps > 0 ? TimeSpan.FromSeconds(frames / fps) : TimeSpan.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _inner.Dispose();
        }
    }
}
