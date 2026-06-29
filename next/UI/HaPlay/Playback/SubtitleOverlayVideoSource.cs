using System.Diagnostics;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// Adapts an <see cref="IVideoOverlaySource"/> (a libass subtitle layer) into an <see cref="IVideoSource"/> for
/// a standalone caption cue: each read renders the subtitle at the elapsed playback time into a fresh owned
/// BGRA frame — transparent where no cue is active — so the cue's composition layer shows timed captions with no
/// media clip. The player presents on its own tick (LatestOnTick), so a wall-clock elapsed position suffices.
/// </summary>
internal sealed class SubtitleOverlayVideoSource : IVideoSource, IDisposable
{
    private readonly IVideoOverlaySource _overlay;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly Stopwatch _clock = new();
    private int _disposed;

    public SubtitleOverlayVideoSource(IVideoOverlaySource overlay, int width, int height)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "width and height must be positive.");
        _width = width;
        _height = height;
        _stride = width * 4;
    }

    public VideoFormat Format => new(_width, _height, PixelFormat.Bgra32, new Rational(25, 1));

    public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];

    public bool IsExhausted => Volatile.Read(ref _disposed) != 0;

    // BGRA-only; the cue layer pipeline (BgraConvertingVideoOutput / GL compositor) converts where needed.
    public void SelectOutputFormat(PixelFormat format) { }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        if (IsExhausted)
        {
            frame = null!;
            return false;
        }

        if (!_clock.IsRunning)
            _clock.Start();

        var pos = _clock.Elapsed;
        var buffer = new byte[_stride * _height];
        // RenderAt hands back a borrowed/reused frame (or null when no cue is active) — copy the pixels out into
        // our freshly-owned buffer; a null render leaves the buffer zeroed (fully transparent).
        var rendered = _overlay.RenderAt(pos);
        if (rendered is { } r && r.PlaneCount > 0)
        {
            var src = r.Planes[0].Span;
            src[..Math.Min(src.Length, buffer.Length)].CopyTo(buffer);
        }

        frame = new VideoFrame(pos, Format, buffer, _stride,
            new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        (_overlay as IDisposable)?.Dispose();
    }
}
