namespace S.Media.Core.Video;

/// <summary>
/// Plays <see cref="SourceA"/> until the first emitted frame whose PTS is at or past
/// <see cref="CutAt"/>, then switches to <see cref="SourceB"/>. Hard cut — no blending.
/// </summary>
/// <remarks>
/// <para>
/// Output format equals A's format (verified at construction to equal B's format — same dimensions,
/// pixel format, frame rate). A's frame at the cut boundary is disposed; B's first frame's PTS is
/// rewritten to <see cref="CutAt"/> for continuity (downstream players compare PTS to a clock and
/// expect monotonic progress).
/// </para>
/// <para>
/// Ownership: both sources are disposed when this source is disposed (controllable via
/// <paramref name="disposeSourcesOnDispose"/>).
/// </para>
/// </remarks>
public sealed class CutVideoSource : IVideoSource, IDisposable
{
    private readonly IVideoSource _a;
    private readonly IVideoSource _b;
    private readonly bool _disposeSources;
    private readonly TimeSpan _cutAt;
    private bool _onB;
    private bool _bFirstEmitted;
    private bool _disposed;

    public CutVideoSource(IVideoSource sourceA, IVideoSource sourceB, TimeSpan cutAt, bool disposeSourcesOnDispose = true)
    {
        ArgumentNullException.ThrowIfNull(sourceA);
        ArgumentNullException.ThrowIfNull(sourceB);
        if (sourceA.Format != sourceB.Format)
            throw new ArgumentException(
                $"CutVideoSource requires matching formats — sourceA={sourceA.Format} sourceB={sourceB.Format}.",
                nameof(sourceB));
        if (cutAt < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cutAt), "must be >= 0");

        _a = sourceA;
        _b = sourceB;
        _cutAt = cutAt;
        _disposeSources = disposeSourcesOnDispose;
    }

    public TimeSpan CutAt => _cutAt;
    public IVideoSource SourceA => _a;
    public IVideoSource SourceB => _b;

    public VideoFormat Format => _a.Format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _a.NativePixelFormats;
    public bool IsExhausted => _disposed || (_onB ? _b.IsExhausted : _a.IsExhausted);

    public void SelectOutputFormat(PixelFormat format)
    {
        _a.SelectOutputFormat(format);
        _b.SelectOutputFormat(format);
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        if (_disposed)
        {
            frame = null!;
            return false;
        }

        if (!_onB)
        {
            if (!_a.TryReadNextFrame(out var aFrame))
            {
                _onB = true;
                return ReadFromB(out frame);
            }
            if (aFrame.PresentationTime < _cutAt)
            {
                frame = aFrame;
                return true;
            }
            // A's frame reached the cut boundary; drop it and switch.
            aFrame.Dispose();
            _onB = true;
        }
        return ReadFromB(out frame);
    }

    private bool ReadFromB(out VideoFrame frame)
    {
        if (!_b.TryReadNextFrame(out var bFrame))
        {
            frame = null!;
            return false;
        }

        if (!_bFirstEmitted)
        {
            _bFirstEmitted = true;
            // Rewrite the first B frame's PTS to the cut boundary for clean continuity.
            // Construct a new VideoFrame sharing the same backing but with adjusted PTS. The original
            // frame's release callback is transferred — the wrapping VideoFrame owns disposal now.
            var rewritten = RewritePts(bFrame, _cutAt);
            frame = rewritten;
            return true;
        }

        frame = bFrame;
        return true;
    }

    private static VideoFrame RewritePts(VideoFrame original, TimeSpan newPts)
    {
        // We need a frame with the same backing + release semantics but a different PresentationTime.
        // For CPU frames we can build a new VideoFrame wrapping the original's planes/strides plus a
        // closure that disposes the original (which fires the original release). For hardware-backed
        // frames, the same trick works because VideoFrame's hardware-backing accessors are read-only;
        // we wrap with the appropriate factory.
        if (original.DmabufNv12 is not null)
        {
            var dup = VideoFrame.CreateNv12DmabufSharedReference(original);
            // The duplicate adds a ref; once we return the wrapped frame the caller will Dispose it,
            // which decrements; original needs an explicit dispose here.
            original.Dispose();
            return new VideoFrame(
                newPts,
                dup.Format,
                dup.Planes,
                dup.Strides,
                dup.ColorTransferHint,
                release: dup.Dispose,
                dmaBufNv12: dup.DmabufNv12);
        }
        if (original.DmabufP010 is not null)
        {
            var dup = VideoFrame.CreateP010DmabufSharedReference(original);
            original.Dispose();
            return new VideoFrame(
                newPts,
                dup.Format,
                dup.Planes,
                dup.Strides,
                dup.ColorTransferHint,
                release: dup.Dispose,
                dmaBufP010: dup.DmabufP010);
        }
        if (original.DmabufP016 is not null)
        {
            var dup = VideoFrame.CreateP016DmabufSharedReference(original);
            original.Dispose();
            return new VideoFrame(
                newPts,
                dup.Format,
                dup.Planes,
                dup.Strides,
                dup.ColorTransferHint,
                release: dup.Dispose,
                dmaBufP016: dup.DmabufP016);
        }
        if (original.Win32Nv12 is not null && OperatingSystem.IsWindows())
        {
            var dup = VideoFrame.CreateNv12Win32SharedReference(original);
            original.Dispose();
            return new VideoFrame(
                newPts,
                dup.Format,
                dup.Planes,
                dup.Strides,
                dup.ColorTransferHint,
                release: dup.Dispose,
                win32Nv12: dup.Win32Nv12);
        }

        // CPU path — share the planes and forward dispose to the original.
        var capturedOriginal = original;
        return new VideoFrame(
            newPts,
            original.Format,
            original.Planes,
            original.Strides,
            original.ColorTransferHint,
            release: capturedOriginal.Dispose);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_disposeSources) return;
        if (_a is IDisposable da) da.Dispose();
        if (_b is IDisposable db) db.Dispose();
    }
}
