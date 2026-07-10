using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;

namespace S.Media.Interop;

/// <summary>
/// A bitmap-subtitle overlay (PGS / DVB / DVD-VobSub) aligned to the master timeline - the non-libass counterpart
/// to the ASS path. Given a master time it composites the placed images of the cue active then onto a Bgra32-
/// premultiplied overlay frame at the subtitle's authored resolution (the compositor scales the layer to the
/// canvas), or <c>null</c> when nothing shows. The decoded images are already premultiplied BGRA, so this is a
/// straight source-over blit - no rendering engine.
/// </summary>
/// <remarks>
/// The returned frame is owned by this source and re-rendered in place (valid until the next call); composite it
/// within the tick and do not dispose it. Single-threaded.
/// </remarks>
public sealed class BitmapSubtitleLayerSource : IVideoOverlaySource
{
    private readonly DecodedBitmapSubtitle _data;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly byte[] _buffer;
    private readonly VideoFrame _frame;
    private BitmapSubtitleCue? _activeCue;
    private bool _disposed;

    public BitmapSubtitleLayerSource(DecodedBitmapSubtitle data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _width = Math.Max(1, data.Width);
        _height = Math.Max(1, data.Height);
        _stride = _width * 4;
        _buffer = new byte[_stride * _height];
        var format = new VideoFormat(_width, _height, PixelFormat.Bgra32, new Rational(25, 1));
        _frame = new VideoFrame(TimeSpan.Zero, format, _buffer, _stride,
            new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
    }

    /// <summary>Authored overlay width (the subtitle's frame; the compositor scales the layer to the canvas).</summary>
    public int Width => _width;

    /// <summary>Authored overlay height.</summary>
    public int Height => _height;

    public VideoFrame? RenderAt(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ms = (long)position.TotalMilliseconds;
        var cue = FindCue(ms);

        if (cue is null)
        {
            _activeCue = null;
            return null;
        }

        if (ReferenceEquals(cue, _activeCue))
            return _frame; // same cue still showing - reuse

        Array.Clear(_buffer);
        foreach (var image in cue.Images)
            Blit(image);
        _activeCue = cue;
        return _frame;
    }

    private BitmapSubtitleCue? FindCue(long ms)
    {
        // Cues are in presentation order (sorted by start); the first whose window contains ms is active.
        foreach (var cue in _data.Cues)
        {
            if (cue.StartMs > ms)
                break;
            if (ms < cue.EndMs)
                return cue;
        }

        return null;
    }

    private void Blit(BitmapSubtitleImage image)
    {
        var src = image.Bgra;
        for (var y = 0; y < image.H; y++)
        {
            var dstY = image.Y + y;
            if ((uint)dstY >= (uint)_height)
                continue;

            var srcRow = y * image.W * 4;
            var dstRow = dstY * _stride;
            for (var x = 0; x < image.W; x++)
            {
                var dstX = image.X + x;
                if ((uint)dstX >= (uint)_width)
                    continue;

                var si = srcRow + x * 4;
                int a = src[si + 3];
                if (a == 0)
                    continue;

                var di = dstRow + dstX * 4;
                var inv = 255 - a;
                // Premultiplied source-over (src is already premultiplied).
                _buffer[di + 0] = (byte)(src[si + 0] + _buffer[di + 0] * inv / 255);
                _buffer[di + 1] = (byte)(src[si + 1] + _buffer[di + 1] * inv / 255);
                _buffer[di + 2] = (byte)(src[si + 2] + _buffer[di + 2] * inv / 255);
                _buffer[di + 3] = (byte)(a + _buffer[di + 3] * inv / 255);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _frame.Dispose();
    }
}
