using S.Media.Core.Buses;

namespace S.Media.Routing;

/// <summary>
/// A pass-through video effect that publishes cheap per-frame color statistics to a
/// <see cref="BusMetadataHub"/> - the "frame data for color matching" feed visualizers subscribe to.
/// Subsampled (every Nth pixel of every Nth frame) so the cost is negligible on the pump thread;
/// BGRA/RGBA frames only (the common canvas formats) - other layouts pass through unprobed.
/// </summary>
public sealed class FrameStatsProbeEffect(BusMetadataHub hub, int frameStride = 5, int pixelStride = 16) : IVideoBusEffect
{
    private readonly BusMetadataHub _hub = hub ?? throw new ArgumentNullException(nameof(hub));
    private readonly int _frameStride = Math.Max(1, frameStride);
    private readonly int _pixelStride = Math.Max(1, pixelStride);
    private long _frameIndex;
    private bool _bgra;
    private bool _probeable;

    public void Configure(VideoFormat format)
    {
        _probeable = format.PixelFormat is PixelFormat.Bgra32 or PixelFormat.Rgba32;
        _bgra = format.PixelFormat == PixelFormat.Bgra32;
    }

    public VideoFrame Process(VideoFrame frame, TimeSpan presentationTime)
    {
        if (!_probeable || frame.Planes.Length == 0 || _frameIndex++ % _frameStride != 0)
            return frame;

        var pixels = frame.Planes[0].Span;
        var stride = frame.Strides[0];
        var width = frame.Format.Width;
        var height = frame.Format.Height;

        long sumR = 0, sumG = 0, sumB = 0, count = 0;
        // Coarse dominant-color estimate: 4-bit-per-channel histogram over the sampled pixels.
        Span<int> histogram = stackalloc int[4096];
        var step = _pixelStride;
        for (var y = 0; y < height; y += step)
        {
            var row = pixels.Slice(y * stride, Math.Min(stride, width * 4));
            for (var x = 0; x + 4 <= row.Length; x += 4 * step)
            {
                byte r, g, b;
                if (_bgra)
                {
                    b = row[x];
                    g = row[x + 1];
                    r = row[x + 2];
                }
                else
                {
                    r = row[x];
                    g = row[x + 1];
                    b = row[x + 2];
                }

                sumR += r;
                sumG += g;
                sumB += b;
                count++;
                histogram[((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4)]++;
            }
        }

        if (count > 0)
        {
            var avgR = (byte)(sumR / count);
            var avgG = (byte)(sumG / count);
            var avgB = (byte)(sumB / count);

            var best = 0;
            var bestBin = 0;
            for (var i = 0; i < histogram.Length; i++)
            {
                if (histogram[i] > best)
                {
                    best = histogram[i];
                    bestBin = i;
                }
            }

            var domR = (byte)(((bestBin >> 8) & 0xF) * 17);
            var domG = (byte)(((bestBin >> 4) & 0xF) * 17);
            var domB = (byte)((bestBin & 0xF) * 17);

            var luma = (0.2126 * avgR + 0.7152 * avgG + 0.0722 * avgB) / 255.0;
            _hub.Publish(new FrameStatsMetadata(
                0xFF000000u | ((uint)avgR << 16) | ((uint)avgG << 8) | avgB,
                0xFF000000u | ((uint)domR << 16) | ((uint)domG << 8) | domB,
                luma,
                presentationTime));
        }

        return frame;
    }

    public void Dispose()
    {
    }
}
