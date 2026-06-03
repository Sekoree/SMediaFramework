using S.Media.Core;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// Rewrites submitted frame PTS from source timeline to cue-relative timeline before the frame
/// reaches the composition slot. A cue that starts 80 minutes into a file should still enter the
/// compositor at t=0 so master-aligned layers can compare frames from multiple cues.
/// </summary>
internal sealed class PtsRebasingVideoOutput : IVideoOutput, IDisposable
{
    private readonly IVideoOutput _inner;
    private readonly TimeSpan _offset;
    private bool _disposed;

    public PtsRebasingVideoOutput(IVideoOutput inner, TimeSpan offset)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _offset = offset > TimeSpan.Zero ? offset : TimeSpan.Zero;
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;

    public VideoFormat Format => _inner.Format;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Configure(format);
    }

    public void Submit(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        if (_offset <= TimeSpan.Zero)
        {
            _inner.Submit(frame);
            return;
        }

        var rebasedPts = frame.PresentationTime - _offset;
        if (rebasedPts < TimeSpan.Zero)
            rebasedPts = TimeSpan.Zero;

        var rebased = RewritePts(frame, rebasedPts);
        try
        {
            _inner.Submit(rebased);
        }
        catch
        {
            rebased.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static VideoFrame RewritePts(VideoFrame original, TimeSpan newPts)
    {
        if (original.DmabufNv12 is not null)
        {
            var dup = original.DmabufNv12.CreateSharedReference(newPts, original.Format, original.Metadata);
            original.Dispose();
            return dup;
        }
        if (original.DmabufP010 is not null)
        {
            var dup = original.DmabufP010.CreateSharedReference(newPts, original.Format, original.Metadata);
            original.Dispose();
            return dup;
        }
        if (original.DmabufP016 is not null)
        {
            var dup = original.DmabufP016.CreateSharedReference(newPts, original.Format, original.Metadata);
            original.Dispose();
            return dup;
        }
        if (original.Win32Nv12 is not null && OperatingSystem.IsWindows())
        {
            var dup = original.Win32Nv12.CreateSharedReference(newPts, original.Format, original.Metadata);
            original.Dispose();
            return dup;
        }

        return new VideoFrame(
            newPts,
            original.Format,
            original.Planes,
            original.Strides,
            original.Metadata,
            release: DisposableRelease.Wrap(original.Dispose));
    }
}
