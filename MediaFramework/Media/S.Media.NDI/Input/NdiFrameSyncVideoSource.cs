using NDILib;
using S.Media.Core.Video;
using S.Media.NDI.Clock;
using S.Media.NDI.Video;

namespace S.Media.NDI.Input;

/// <summary><see cref="IVideoSource"/> that pulls frames from <see cref="NDIFrameSync"/> (NDI clock path).</summary>
public sealed class NdiFrameSyncVideoSource : IVideoSource, IDisposable
{
    private readonly NDIFrameSync _frameSync;
    private readonly NDIIngestPlaybackClock _ingestClock;
    private VideoFormat _format;
    private PixelFormat[] _native = [];
    private long _ptsOriginTicks;
    private bool _ptsOriginSet;
    private long _syntheticPtsTicks;
    private bool _hasFormat;
    private bool _disposed;

    internal NdiFrameSyncVideoSource(NDIFrameSync frameSync, NDIIngestPlaybackClock ingestClock)
    {
        _frameSync = frameSync ?? throw new ArgumentNullException(nameof(frameSync));
        _ingestClock = ingestClock ?? throw new ArgumentNullException(nameof(ingestClock));
    }

    public bool IsConnected => _hasFormat;

    public VideoFormat Format =>
        _hasFormat
            ? _format
            : throw new InvalidOperationException(
                "NDI frame-sync video has not delivered a frame yet — wait until IsConnected is true");

    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;

    public bool IsExhausted => _disposed;

    public void SelectOutputFormat(PixelFormat format)
    {
        if (!_hasFormat)
            throw new InvalidOperationException("Format is not known until the first frame arrives.");
        if (format != _format.PixelFormat)
            throw new InvalidOperationException(
                $"NDI frame-sync delivers {_format.PixelFormat} only; output requested {format}.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = null!;

        _frameSync.CaptureVideo(out var native);
        try
        {
            if (native.Xres <= 0 || native.Yres <= 0)
                return false;

            _ingestClock.NotifyVideoFrame(ref native);
            var pts = MapPresentationTime(in native);
            if (!NDIVideoFrameUnpack.TryUnpack(native, pts, out var vf) || vf is null)
                return false;

            EnsureFormat(vf.Format);
            frame = vf;
            return true;
        }
        finally
        {
            _frameSync.FreeVideo(in native);
        }
    }

    private TimeSpan MapPresentationTime(in NDIVideoFrameV2 video)
    {
        if (NDIFrameTiming.TryMapPresentationTime(
                video.Timecode,
                video.Timestamp,
                ref _ptsOriginTicks,
                ref _ptsOriginSet,
                out var pts))
            return pts;

        var step = NDIFrameTiming.FrameDurationTicks(video.FrameRateN, video.FrameRateD);
        var synthetic = TimeSpan.FromTicks(_syntheticPtsTicks);
        _syntheticPtsTicks += step;
        return synthetic;
    }

    private void EnsureFormat(VideoFormat format)
    {
        if (_hasFormat && _format.PixelFormat == format.PixelFormat
                       && _format.Width == format.Width && _format.Height == format.Height)
            return;

        _format = format;
        _native = [format.PixelFormat];
        _hasFormat = true;
    }

    public void Dispose() => _disposed = true;
}
