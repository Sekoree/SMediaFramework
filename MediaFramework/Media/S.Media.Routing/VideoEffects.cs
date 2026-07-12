using System.Buffers;
using S.Media.Core.Buses;

namespace S.Media.Routing;

/// <summary>
/// The proof-of-concept video effect: Rec.709-weighted grayscale on BGRA/RGBA frames (other layouts
/// pass through untouched). COPY-on-write, never in place: the router's multi-output fan-out hands
/// branches refcounted VIEWS over the same pixels, so mutating the input would gray every other
/// output too. Output buffers come from the shared ArrayPool (released with the frame); also the
/// template for further CPU frame effects.
/// </summary>
public sealed class GrayscaleVideoEffect : IVideoBusEffect
{
    private bool _applicable;

    public void Configure(VideoFormat format) =>
        _applicable = format.PixelFormat is PixelFormat.Bgra32 or PixelFormat.Rgba32;

    public VideoFrame Process(VideoFrame frame, TimeSpan presentationTime)
    {
        if (!_applicable || frame.Planes.Length == 0
            || frame.Format.PixelFormat is not (PixelFormat.Bgra32 or PixelFormat.Rgba32))
        {
            return frame;
        }

        var bgra = frame.Format.PixelFormat == PixelFormat.Bgra32;
        var srcStride = frame.Strides[0];
        var width = frame.Format.Width;
        var height = frame.Format.Height;
        var dstStride = width * 4;
        var length = dstStride * height;

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        var src = frame.Planes[0].Span;
        var dst = buffer.AsSpan(0, length);
        for (var y = 0; y < height; y++)
        {
            var srcRow = src.Slice(y * srcStride, Math.Min(srcStride, dstStride));
            var dstRow = dst.Slice(y * dstStride, dstStride);
            for (var x = 0; x + 4 <= srcRow.Length; x += 4)
            {
                var c0 = srcRow[x];
                var c1 = srcRow[x + 1];
                var c2 = srcRow[x + 2];
                var (r, b) = bgra ? ((int)c2, (int)c0) : ((int)c0, (int)c2);
                var luma = (byte)((r * 54 + c1 * 183 + b * 19) >> 8); // Rec.709 integer weights
                dstRow[x] = luma;
                dstRow[x + 1] = luma;
                dstRow[x + 2] = luma;
                dstRow[x + 3] = srcRow[x + 3];
            }
        }

        var result = new VideoFrame(
            frame.PresentationTime,
            frame.Format,
            [new ReadOnlyMemory<byte>(buffer, 0, length)],
            [dstStride],
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(buffer)),
            metadata: frame.Metadata);
        frame.Dispose();
        return result;
    }

    public void Dispose()
    {
    }
}
