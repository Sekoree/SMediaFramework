using System.Buffers;

namespace S.Media.Core.Video;

/// <summary>
/// Software <see cref="IVideoCompositor"/> reference implementation. BGRA32 layers in, BGRA32 out.
/// Always available — no GPU context required. Used by tests, headless server scenarios, and as the
/// fallback when an <c>S.Media.OpenGL.GlVideoCompositor</c> isn't wired up.
/// </summary>
/// <remarks>
/// <para>
/// Per-layer transform is applied via inverse-affine mapping with nearest-neighbor sampling. Source
/// pixels outside the destination canvas are clipped; destination pixels whose inverse-mapped source
/// lies outside the layer frame are skipped (preserving whatever is already there).
/// </para>
/// <para>
/// Premultiplied-alpha math is used internally. SkiaSharp's <c>SKAlphaType.Premul</c> output matches
/// (see <c>S.Media.SkiaSharp.ImageFileSource</c>); BGRA32 frames produced by FFmpeg's swscale path
/// are also treated as premultiplied here — alpha is typically 0xFF in that path so the distinction
/// is moot.
/// </para>
/// <para>
/// Output buffer is rented from <see cref="ArrayPool{T}.Shared"/> and returned via the emitted
/// <see cref="VideoFrame"/>'s <c>release</c> callback. Bilinear / bicubic sampling are deliberately
/// out of scope for the first cut — see Phase 4 plan.
/// </para>
/// </remarks>
public sealed class CpuVideoCompositor : IVideoCompositor
{
    private static readonly PixelFormat[] AcceptedFormatsArr = [PixelFormat.Bgra32];

    private VideoFormat _output;
    private int _outputStride;
    private int _outputByteCount;
    private bool _configured;
    private bool _disposed;

    public CpuVideoCompositor(VideoFormat output)
    {
        Configure(output);
    }

    public VideoFormat OutputFormat => _output;
    public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => AcceptedFormatsArr;

    public void Configure(VideoFormat output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (output.PixelFormat != PixelFormat.Bgra32)
            throw new ArgumentException(
                $"CpuVideoCompositor only outputs BGRA32; got {output.PixelFormat}.", nameof(output));
        if (output.Width <= 0 || output.Height <= 0)
            throw new ArgumentException(
                $"output dimensions must be positive (got {output.Width}x{output.Height}).", nameof(output));

        _output = output;
        _outputStride = output.Width * 4;
        _outputByteCount = _outputStride * output.Height;
        _configured = true;
    }

    public VideoFrame Composite(IReadOnlyList<CompositorLayer> layersBackToFront, TimeSpan presentationTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layersBackToFront);
        if (!_configured)
            throw new InvalidOperationException("CpuVideoCompositor must be Configure()d before Composite.");

        var buffer = ArrayPool<byte>.Shared.Rent(_outputByteCount);
        // Clear to transparent black so empty regions stay see-through for downstream consumers.
        Array.Clear(buffer, 0, _outputByteCount);

        for (var i = 0; i < layersBackToFront.Count; i++)
        {
            var layer = layersBackToFront[i];
            if (layer.Frame.Format.PixelFormat != PixelFormat.Bgra32)
                throw new InvalidOperationException(
                    $"CpuVideoCompositor layer {i}: only BGRA32 accepted, got {layer.Frame.Format.PixelFormat}.");
            var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
            if (opacity <= 0f) continue;
            DrawLayer(buffer, layer, opacity);
        }

        var plane = new ReadOnlyMemory<byte>(buffer, 0, _outputByteCount);
        var owned = buffer;
        return new VideoFrame(
            presentationTime,
            _output,
            plane,
            _outputStride,
            release: () => ArrayPool<byte>.Shared.Return(owned, clearArray: false));
    }

    private void DrawLayer(byte[] dst, CompositorLayer layer, float opacity)
    {
        var src = layer.Frame;
        var srcStride = src.Strides[0];
        var srcW = src.Format.Width;
        var srcH = src.Format.Height;
        var srcPlane = src.Planes[0];

        // Compute destination AABB from the four transformed corners, clipped to canvas.
        var (x0, y0) = layer.Transform.Apply(0, 0);
        var (x1, y1) = layer.Transform.Apply(srcW, 0);
        var (x2, y2) = layer.Transform.Apply(0, srcH);
        var (x3, y3) = layer.Transform.Apply(srcW, srcH);
        var minX = (int)MathF.Floor(MathF.Min(MathF.Min(x0, x1), MathF.Min(x2, x3)));
        var minY = (int)MathF.Floor(MathF.Min(MathF.Min(y0, y1), MathF.Min(y2, y3)));
        var maxX = (int)MathF.Ceiling(MathF.Max(MathF.Max(x0, x1), MathF.Max(x2, x3)));
        var maxY = (int)MathF.Ceiling(MathF.Max(MathF.Max(y0, y1), MathF.Max(y2, y3)));
        minX = Math.Max(minX, 0);
        minY = Math.Max(minY, 0);
        maxX = Math.Min(maxX, _output.Width);
        maxY = Math.Min(maxY, _output.Height);
        if (minX >= maxX || minY >= maxY) return;

        LayerTransform2D inv;
        try { inv = layer.Transform.Invert(); }
        catch (InvalidOperationException) { return; }

        var srcSpan = srcPlane.Span;
        for (var dy = minY; dy < maxY; dy++)
        {
            // Sample at pixel centers for nearest-neighbor: dx + 0.5, dy + 0.5.
            var dyCenter = dy + 0.5f;
            var rowOffset = dy * _outputStride;
            for (var dx = minX; dx < maxX; dx++)
            {
                var dxCenter = dx + 0.5f;
                var (sxf, syf) = inv.Apply(dxCenter, dyCenter);
                var sx = (int)MathF.Floor(sxf);
                var sy = (int)MathF.Floor(syf);
                if ((uint)sx >= (uint)srcW || (uint)sy >= (uint)srcH) continue;

                var srcIdx = sy * srcStride + sx * 4;
                var b = srcSpan[srcIdx + 0];
                var g = srcSpan[srcIdx + 1];
                var r = srcSpan[srcIdx + 2];
                var a = srcSpan[srcIdx + 3];

                // Effective layer alpha = source.alpha × opacity (both in 0..1).
                // We keep math in 0..255 for speed; final alpha rounds via /255.
                var effA = (int)(a * opacity + 0.5f);
                if (effA <= 0) continue;

                var dstIdx = rowOffset + dx * 4;
                switch (layer.BlendMode)
                {
                    case BlendMode.Source:
                    {
                        // Replace destination with source × opacity. Source is already premultiplied
                        // (SkiaSharp convention), so RGB carry the alpha-scaling — applying opacity
                        // once preserves the premultiplied relationship.
                        dst[dstIdx + 0] = (byte)(b * opacity + 0.5f);
                        dst[dstIdx + 1] = (byte)(g * opacity + 0.5f);
                        dst[dstIdx + 2] = (byte)(r * opacity + 0.5f);
                        dst[dstIdx + 3] = (byte)effA;
                        break;
                    }
                    case BlendMode.SourceOver:
                    {
                        // Premultiplied source-over: dst.rgb = src.rgb*opacity + dst.rgb*(1 - effA/255).
                        // Source pixel is premultiplied already (SkiaSharp convention) so RGB are
                        // pre-scaled by source alpha; we only need to apply opacity on top.
                        var sB = (b * opacity);
                        var sG = (g * opacity);
                        var sR = (r * opacity);
                        var oneMinusA = 1f - (effA / 255f);
                        var dB = dst[dstIdx + 0];
                        var dG = dst[dstIdx + 1];
                        var dR = dst[dstIdx + 2];
                        var dA = dst[dstIdx + 3];
                        dst[dstIdx + 0] = (byte)Math.Clamp((int)(sB + dB * oneMinusA + 0.5f), 0, 255);
                        dst[dstIdx + 1] = (byte)Math.Clamp((int)(sG + dG * oneMinusA + 0.5f), 0, 255);
                        dst[dstIdx + 2] = (byte)Math.Clamp((int)(sR + dR * oneMinusA + 0.5f), 0, 255);
                        dst[dstIdx + 3] = (byte)Math.Clamp((int)(effA + dA * oneMinusA + 0.5f), 0, 255);
                        break;
                    }
                    case BlendMode.Multiply:
                    {
                        // dst.rgb = (src.rgb * dst.rgb / 255), weighted by effA/255 against the
                        // previous destination so Opacity 0 leaves dst untouched.
                        var dB = dst[dstIdx + 0];
                        var dG = dst[dstIdx + 1];
                        var dR = dst[dstIdx + 2];
                        var mulB = (b * dB + 127) / 255;
                        var mulG = (g * dG + 127) / 255;
                        var mulR = (r * dR + 127) / 255;
                        var w = effA / 255f;
                        var oneMinusW = 1f - w;
                        dst[dstIdx + 0] = (byte)Math.Clamp((int)(mulB * w + dB * oneMinusW + 0.5f), 0, 255);
                        dst[dstIdx + 1] = (byte)Math.Clamp((int)(mulG * w + dG * oneMinusW + 0.5f), 0, 255);
                        dst[dstIdx + 2] = (byte)Math.Clamp((int)(mulR * w + dR * oneMinusW + 0.5f), 0, 255);
                        // Alpha unchanged for Multiply.
                        break;
                    }
                    default:
                        throw new NotSupportedException($"BlendMode {layer.BlendMode} not supported.");
                }
            }
        }
    }

    public void Dispose() => _disposed = true;
}
