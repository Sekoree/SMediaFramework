using S.Media.FFmpeg.Video;

namespace S.Media.Effects;

/// <summary>
/// <see cref="IVideoSource"/> that holds a single pre-built frame and re-emits it on every read.
/// Foundation for static image cues, "logo card" outputs, and tests that need a stable visual.
/// </summary>
/// <remarks>
/// <para>
/// The constructor takes the plane data as <see cref="ReadOnlyMemory{T}"/>; every
/// <see cref="TryReadNextFrame"/> wraps the same memory references in a fresh
/// <see cref="VideoFrame"/> (zero-copy). The frames carry <c>release: null</c> — the source owns the
/// underlying buffers and releases them once on <see cref="Dispose"/>.
/// </para>
/// <para>
/// PTS spacing matches <see cref="VideoFormat.FrameRate"/> (falls back to 30 FPS when the rate is
/// zero/invalid). Without spacing the player's late-frame drop heuristic would flag the constant
/// frame as stale every tick, so the source emits monotonically increasing PTSes even though the
/// pixel contents never change.
/// </para>
/// <para>
/// Pixel formats are fixed to whatever <see cref="VideoFormat.PixelFormat"/> was passed in;
/// <see cref="SelectOutputFormat"/> throws on mismatch. Callers wanting a different layout should
/// pre-build the planes in that format or compose with an external converter — the source
/// intentionally has no swscale dependency so it can live in Core.
/// </para>
/// </remarks>
public sealed class StaticFrameSource : IVideoSource, IDisposable
{
    private readonly VideoFormat _format;
    private readonly ReadOnlyMemory<byte>[] _planes;
    private readonly int[] _strides;
    private readonly Action? _bufferRelease;
    private readonly VideoTransferHint _transferHint;
    private readonly PixelFormat[] _native;
    private readonly TimeSpan _ptsStep;
    // Refcount guarding the shared backing when a release hook is present (pooled/native buffers via
    // releaseBuffersOnDispose / FromFrame's ArrayPool clone). The source holds one ref; each emitted
    // frame holds one while alive. The hook fires only once the source is disposed AND every emitted
    // frame is — so a still-queued frame can't read buffers Dispose has already returned to the pool.
    // No refs / no per-frame allocation when the backing is plain managed arrays (hook null): the GC
    // keeps those alive behind any queued frame, so there's nothing to defer.
    private readonly Lock _gate = new();
    private int _backingRefs = 1;
    private TimeSpan _nextPts;
    private bool _disposed;

    /// <param name="format">Frame format: dimensions, pixel layout, and frame rate (drives PTS spacing).</param>
    /// <param name="planes">Plane data — wrapped (not copied) into every returned frame.</param>
    /// <param name="strides">Per-plane stride in bytes; same length as <paramref name="planes"/>.</param>
    /// <param name="ptsCadence">Override PTS spacing per frame. Defaults to <c>format.FrameRate</c>'s period (or 33 ms when invalid).</param>
    /// <param name="colorTransferHint">Optional transfer hint stamped onto every frame.</param>
    /// <param name="releaseBuffersOnDispose">Fires once on <see cref="Dispose"/> — return arrays to a pool, free a refcount, etc.</param>
    public StaticFrameSource(
        VideoFormat format,
        ReadOnlyMemory<byte>[] planes,
        int[] strides,
        TimeSpan? ptsCadence = null,
        VideoTransferHint colorTransferHint = default,
        Action? releaseBuffersOnDispose = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(strides);
        if (planes.Length != strides.Length)
            throw new ArgumentException(
                $"planes.Length ({planes.Length}) must equal strides.Length ({strides.Length}).",
                nameof(strides));

        _format = format;
        _planes = planes;
        _strides = strides;
        _transferHint = colorTransferHint;
        _bufferRelease = releaseBuffersOnDispose;
        _native = [format.PixelFormat];
        _ptsStep = ptsCadence ?? DerivePeriod(format.FrameRate);
        _nextPts = TimeSpan.Zero;
    }

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;
    public bool IsExhausted => _disposed;

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != _format.PixelFormat)
            throw new InvalidOperationException(
                $"StaticFrameSource only delivers {_format.PixelFormat}; output requested {format}. " +
                "Pre-build the planes in the target format or wrap with a converter.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                frame = null!;
                return false;
            }

            // Only refcount when there's a release hook to defer; otherwise emit a release-free frame.
            var release = _bufferRelease is not null ? DisposableRelease.Wrap(ReleaseBackingRef) : null;
            frame = new VideoFrame(_nextPts, _format, _planes, _strides,
                release: release,
                metadata: new VideoFrameMetadata(ColorTransferHint: _transferHint));
            // Commit the ref only after the frame was constructed successfully (ctor validates).
            if (release is not null)
                _backingRefs++;
            _nextPts += _ptsStep;
            return true;
        }
    }

    public void Dispose()
    {
        bool fire;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_bufferRelease is null) return;
            // Drop the source's ref; the hook fires here only if no emitted frame is still alive.
            fire = --_backingRefs == 0;
        }
        if (fire)
            InvokeBufferRelease();
    }

    private void ReleaseBackingRef()
    {
        bool fire;
        lock (_gate)
            fire = --_backingRefs == 0;
        if (fire)
            InvokeBufferRelease();
    }

    private void InvokeBufferRelease()
    {
        try { _bufferRelease!(); }
        catch { /* best effort — buffer release is the consumer's hook */ }
    }

    /// <summary>
    /// Builds a static source from one frame. The frame's CPU planes are <strong>copied</strong> into
    /// source-owned backing, so the caller may dispose <paramref name="frame"/> immediately afterwards.
    /// (The previous non-copying mode aliased the caller's planes with no ownership — a use-after-free
    /// footgun if the caller disposed the original — and is removed.)
    /// </summary>
    public static StaticFrameSource FromFrame(VideoFrame frame, TimeSpan? ptsCadence = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var owned = VideoCpuFrameConverter.DuplicateCpuBacking(frame, frame.ColorTransferHint);

        var planes = new ReadOnlyMemory<byte>[owned.Planes.Length];
        var strides = new int[owned.Strides.Length];
        for (var i = 0; i < planes.Length; i++)
        {
            planes[i] = owned.Planes[i];
            strides[i] = owned.Strides[i];
        }

        return new StaticFrameSource(
            owned.Format,
            planes,
            strides,
            ptsCadence,
            owned.ColorTransferHint,
            owned.Dispose);
    }

    private static TimeSpan DerivePeriod(Rational frameRate)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            return TimeSpan.FromMilliseconds(33);
        return TimeSpan.FromSeconds((double)frameRate.Denominator / frameRate.Numerator);
    }
}
