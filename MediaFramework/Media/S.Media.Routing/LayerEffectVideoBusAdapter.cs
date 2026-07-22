using System.Buffers;
using S.Media.Core;
using S.Media.Core.Buses;
using S.Media.Core.Video;
using S.Media.Core.Video.Effects;

namespace S.Media.Routing;

/// <summary>
/// Runs a <see cref="VideoLayerEffect"/> chain's CPU kernels as an <see cref="IVideoBusEffect"/>,
/// so the same effect definitions the compositor applies per layer on the GPU (chroma key, plugin
/// effects) can also be inserted per OUTPUT line via <see cref="VideoEffectBusOutput"/>. This is
/// the CPU-priced bridge between the two effect systems: per-pixel scalar processing on the pump
/// drain thread, same cost class as <see cref="GrayscaleVideoEffect"/>. GPU-only effects (no CPU
/// kernel) are skipped, matching the CPU compositor's contract; hardware-backed or non-RGBA frames
/// pass through untouched.
/// </summary>
public sealed class LayerEffectVideoBusAdapter : IVideoBusEffect
{
    private readonly IVideoLayerCpuEffect[] _kernels;
    private bool _applicable;

    public LayerEffectVideoBusAdapter(IReadOnlyList<VideoLayerEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        _kernels = effects
            .Select(static e => e.CpuKernel)
            .Where(static k => k is not null)
            .Select(static k => k!)
            .ToArray();
    }

    public void Configure(VideoFormat format) =>
        _applicable = format.PixelFormat is PixelFormat.Bgra32 or PixelFormat.Rgba32;

    public VideoFrame Process(VideoFrame frame, TimeSpan presentationTime)
    {
        if (_kernels.Length == 0 || !_applicable || frame.Planes.Length == 0
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
        var premultiplied = frame.Metadata.AlphaMode == VideoAlphaMode.Premultiplied;
        var opaque = frame.Metadata.AlphaMode == VideoAlphaMode.Opaque;

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
                var a = srcRow[x + 3];
                var (rByte, bByte) = bgra ? (c2, c0) : (c0, c2);

                // Kernels see straight-alpha [0,1] RGBA, matching the compositor's contract.
                var af = opaque ? 1f : a / 255f;
                float rf, gf, bf;
                if (premultiplied && a > 0 && a < 255)
                {
                    var inv = 1f / a;
                    rf = Math.Min(1f, rByte * inv);
                    gf = Math.Min(1f, c1 * inv);
                    bf = Math.Min(1f, bByte * inv);
                }
                else
                {
                    rf = rByte / 255f;
                    gf = c1 / 255f;
                    bf = bByte / 255f;
                }

                foreach (var kernel in _kernels)
                    kernel.Apply(ref rf, ref gf, ref bf, ref af);

                af = Math.Clamp(af, 0f, 1f);
                rf = Math.Clamp(rf, 0f, 1f);
                gf = Math.Clamp(gf, 0f, 1f);
                bf = Math.Clamp(bf, 0f, 1f);
                // Preserve the frame's alpha-mode convention on the way out.
                var scale = premultiplied ? af * 255f : 255f;
                var outR = (byte)(rf * scale + 0.5f);
                var outG = (byte)(gf * scale + 0.5f);
                var outB = (byte)(bf * scale + 0.5f);
                (dstRow[x], dstRow[x + 2]) = bgra ? (outB, outR) : (outR, outB);
                dstRow[x + 1] = outG;
                dstRow[x + 3] = (byte)(af * 255f + 0.5f);
            }
        }

        // Effects can introduce transparency into an opaque frame; report straight alpha so
        // downstream consumers don't assume a=255.
        var metadata = opaque ? frame.Metadata with { AlphaMode = VideoAlphaMode.Straight } : frame.Metadata;
        var result = new VideoFrame(
            frame.PresentationTime,
            frame.Format,
            [new ReadOnlyMemory<byte>(buffer, 0, length)],
            [dstStride],
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(buffer)),
            metadata: metadata);
        frame.Dispose();
        return result;
    }

    public void Dispose()
    {
    }
}
