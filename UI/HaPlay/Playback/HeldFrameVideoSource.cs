using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace HaPlay.Playback;

/// <summary>
/// An <see cref="IVideoSource"/> that emits copies of a single static BGRA frame indefinitely. Used to
/// show a still image or a rendered text card for a cue's custom duration — the cue's clip window ends
/// playback. The player presents frames on its own tick (<c>LatestOnTick</c>), so frame timing here is a
/// no-op; each read just hands out a fresh copy of the held frame.
/// </summary>
internal sealed class HeldFrameVideoSource : IVideoSource, IDisposable
{
    private readonly VideoFrame _bgraTemplate;
    private VideoFrame _emit; // the template in the currently selected output format (always owned)
    private int _disposed;

    public HeldFrameVideoSource(VideoFrame bgraTemplate)
    {
        _bgraTemplate = bgraTemplate ?? throw new ArgumentNullException(nameof(bgraTemplate));
        _emit = FallbackImageLoader.CloneHoldTemplate(bgraTemplate);
    }

    public VideoFormat Format => _emit.Format;

    public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];

    public bool IsExhausted => Volatile.Read(ref _disposed) != 0;

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format == _emit.Format.PixelFormat)
            return;

        VideoFrame? next = null;
        if (format == PixelFormat.Bgra32)
        {
            next = FallbackImageLoader.CloneHoldTemplate(_bgraTemplate);
        }
        else if (VideoCpuFrameConverter.CanConvert(PixelFormat.Bgra32, format, _bgraTemplate.Format.Width, _bgraTemplate.Format.Height))
        {
            using var conv = new VideoCpuFrameConverter();
            conv.Configure(PixelFormat.Bgra32, format, _bgraTemplate.Format.Width, _bgraTemplate.Format.Height);
            using var converted = conv.Convert(_bgraTemplate, default);
            next = VideoCpuFrameConverter.DuplicateCpuBacking(converted, converted.ColorTransferHint);
        }

        if (next is null)
            return; // leave the current format; the pipeline will convert if needed

        _emit.Dispose();
        _emit = next;
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        if (IsExhausted)
        {
            frame = null!;
            return false;
        }

        frame = FallbackImageLoader.CloneHoldTemplate(_emit);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _emit.Dispose();
        _bgraTemplate.Dispose();
    }
}
