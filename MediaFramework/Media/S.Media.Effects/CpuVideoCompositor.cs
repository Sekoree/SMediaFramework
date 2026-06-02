using System.Buffers;

namespace S.Media.Effects;

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
/// <see cref="VideoFrame"/>'s <c>release</c> callback. <see cref="CompositorSamplingMode.Bilinear"/>
/// is supported via the <see cref="SamplingMode"/> property (default
/// <see cref="CompositorSamplingMode.Nearest"/> for back-compat); bicubic stays out of scope
/// — the OpenGL compositor already has it.
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

    public CpuVideoCompositor(VideoFormat output, CompositorSamplingMode samplingMode = CompositorSamplingMode.Nearest)
    {
        SamplingMode = samplingMode;
        Configure(output);
    }

    /// <summary>
    /// Source-sampling kernel for inverse-affine layer mapping. Set once at construction or
    /// mutated between <see cref="Composite"/> calls. <see cref="CompositorSamplingMode.Nearest"/>
    /// stays the default to preserve byte-exact output for existing consumers.
    /// </summary>
    public CompositorSamplingMode SamplingMode { get; set; }

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
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)),
            metadata: new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
    }

    private void DrawLayer(byte[] dst, CompositorLayer layer, float opacity)
    {
        var src = layer.Frame;
        var srcStride = src.Strides[0];
        var srcW = src.Format.Width;
        var srcH = src.Format.Height;
        var srcPlane = src.Planes[0];
        var alphaMode = ResolveAlphaMode(src.AlphaMode);

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
        var mode = SamplingMode;
        for (var dy = minY; dy < maxY; dy++)
        {
            // Sample at pixel centers: dx + 0.5, dy + 0.5.
            var dyCenter = dy + 0.5f;
            var rowOffset = dy * _outputStride;
            for (var dx = minX; dx < maxX; dx++)
            {
                var dxCenter = dx + 0.5f;
                var (sxf, syf) = inv.Apply(dxCenter, dyCenter);

                byte b, g, r, a;
                switch (mode)
                {
                    case CompositorSamplingMode.Bilinear:
                        if (!SampleBilinear(srcSpan, srcStride, srcW, srcH, sxf, syf, out b, out g, out r, out a))
                            continue;
                        break;
                    case CompositorSamplingMode.Bicubic:
                        if (!SampleBicubic(srcSpan, srcStride, srcW, srcH, sxf, syf, out b, out g, out r, out a))
                            continue;
                        break;
                    default:
                    {
                        var sx = (int)MathF.Floor(sxf);
                        var sy = (int)MathF.Floor(syf);
                        if ((uint)sx >= (uint)srcW || (uint)sy >= (uint)srcH) continue;
                        var srcIdx = sy * srcStride + sx * 4;
                        b = srcSpan[srcIdx + 0];
                        g = srcSpan[srcIdx + 1];
                        r = srcSpan[srcIdx + 2];
                        a = srcSpan[srcIdx + 3];
                        break;
                    }
                }

                NormalizeForPremultipliedBlend(b, g, r, a, opacity, alphaMode,
                    out var premulB, out var premulG, out var premulR, out var effA,
                    out var multiplyB, out var multiplyG, out var multiplyR);
                if (effA <= 0) continue;

                var dstIdx = rowOffset + dx * 4;
                switch (layer.BlendMode)
                {
                    case BlendMode.Source:
                    {
                        // Replace destination with source × opacity in premultiplied form.
                        dst[dstIdx + 0] = ToByte(premulB);
                        dst[dstIdx + 1] = ToByte(premulG);
                        dst[dstIdx + 2] = ToByte(premulR);
                        dst[dstIdx + 3] = (byte)effA;
                        break;
                    }
                    case BlendMode.SourceOver:
                    {
                        // Premultiplied source-over: dst.rgb = src.rgb + dst.rgb*(1 - effA/255).
                        var oneMinusA = 1f - (effA / 255f);
                        var dB = dst[dstIdx + 0];
                        var dG = dst[dstIdx + 1];
                        var dR = dst[dstIdx + 2];
                        var dA = dst[dstIdx + 3];
                        dst[dstIdx + 0] = ToByte(premulB + dB * oneMinusA);
                        dst[dstIdx + 1] = ToByte(premulG + dG * oneMinusA);
                        dst[dstIdx + 2] = ToByte(premulR + dR * oneMinusA);
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
                        var mulB = (multiplyB * dB + 127) / 255;
                        var mulG = (multiplyG * dG + 127) / 255;
                        var mulR = (multiplyR * dR + 127) / 255;
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

    private static VideoAlphaMode ResolveAlphaMode(VideoAlphaMode alphaMode) => alphaMode switch
    {
        VideoAlphaMode.Straight => VideoAlphaMode.Straight,
        VideoAlphaMode.Opaque => VideoAlphaMode.Opaque,
        _ => VideoAlphaMode.Premultiplied,
    };

    private static void NormalizeForPremultipliedBlend(
        byte b, byte g, byte r, byte a, float opacity, VideoAlphaMode alphaMode,
        out float premulB, out float premulG, out float premulR, out int effectiveAlpha,
        out byte multiplyB, out byte multiplyG, out byte multiplyR)
    {
        if (alphaMode == VideoAlphaMode.Opaque)
        {
            effectiveAlpha = (int)(255 * opacity + 0.5f);
            premulB = b * opacity;
            premulG = g * opacity;
            premulR = r * opacity;
            multiplyB = b;
            multiplyG = g;
            multiplyR = r;
            return;
        }

        effectiveAlpha = (int)(a * opacity + 0.5f);
        if (alphaMode == VideoAlphaMode.Straight)
        {
            var alpha = a / 255f;
            premulB = b * alpha * opacity;
            premulG = g * alpha * opacity;
            premulR = r * alpha * opacity;
            multiplyB = b;
            multiplyG = g;
            multiplyR = r;
            return;
        }

        premulB = b * opacity;
        premulG = g * opacity;
        premulR = r * opacity;
        multiplyB = b;
        multiplyG = g;
        multiplyR = r;
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)(value + 0.5f), 0, 255);

    /// <summary>
    /// 4-tap bilinear sample at fractional <paramref name="sxf"/>/<paramref name="syf"/> source coords (pixel-center convention).
    /// Edge clamping: out-of-range neighbors snap to the nearest valid pixel so layer edges stay sharp instead of fading
    /// to transparent. Returns <see langword="false"/> when every neighbor is fully outside <paramref name="srcW"/> ×
    /// <paramref name="srcH"/> — equivalent to the nearest-path's "skip" branch.
    /// </summary>
    private static bool SampleBilinear(
        ReadOnlySpan<byte> srcSpan, int srcStride, int srcW, int srcH,
        float sxf, float syf,
        out byte b, out byte g, out byte r, out byte a)
    {
        // Move from pixel-center coords (0.5-shifted) to pixel-index coords for neighbor lookup.
        var fx = sxf - 0.5f;
        var fy = syf - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var x1 = x0 + 1;
        var y1 = y0 + 1;

        // Cull when the entire 4-pixel kernel lies outside the source bounds.
        if (x1 < 0 || y1 < 0 || x0 >= srcW || y0 >= srcH)
        {
            b = g = r = a = 0;
            return false;
        }

        var tx = fx - x0;
        var ty = fy - y0;
        var w00 = (1f - tx) * (1f - ty);
        var w10 = tx * (1f - ty);
        var w01 = (1f - tx) * ty;
        var w11 = tx * ty;

        x0 = Math.Clamp(x0, 0, srcW - 1);
        x1 = Math.Clamp(x1, 0, srcW - 1);
        y0 = Math.Clamp(y0, 0, srcH - 1);
        y1 = Math.Clamp(y1, 0, srcH - 1);

        var i00 = y0 * srcStride + x0 * 4;
        var i10 = y0 * srcStride + x1 * 4;
        var i01 = y1 * srcStride + x0 * 4;
        var i11 = y1 * srcStride + x1 * 4;

        var bb = srcSpan[i00 + 0] * w00 + srcSpan[i10 + 0] * w10 + srcSpan[i01 + 0] * w01 + srcSpan[i11 + 0] * w11;
        var gg = srcSpan[i00 + 1] * w00 + srcSpan[i10 + 1] * w10 + srcSpan[i01 + 1] * w01 + srcSpan[i11 + 1] * w11;
        var rr = srcSpan[i00 + 2] * w00 + srcSpan[i10 + 2] * w10 + srcSpan[i01 + 2] * w01 + srcSpan[i11 + 2] * w11;
        var aa = srcSpan[i00 + 3] * w00 + srcSpan[i10 + 3] * w10 + srcSpan[i01 + 3] * w01 + srcSpan[i11 + 3] * w11;
        b = (byte)Math.Clamp((int)(bb + 0.5f), 0, 255);
        g = (byte)Math.Clamp((int)(gg + 0.5f), 0, 255);
        r = (byte)Math.Clamp((int)(rr + 0.5f), 0, 255);
        a = (byte)Math.Clamp((int)(aa + 0.5f), 0, 255);
        return true;
    }

    /// <summary>
    /// 4×4 Catmull-Rom bicubic sample at fractional <paramref name="sxf"/>/<paramref name="syf"/>
    /// source coords (pixel-center convention). Catmull-Rom is a sharp interpolating spline
    /// (passes through every input sample) — natural for image upscaling. Out-of-range neighbors
    /// in the 4×4 footprint clamp to the source edge; per-channel intermediate overshoot is
    /// clamped to <c>[0, 255]</c> at the final byte write.
    /// </summary>
    private static bool SampleBicubic(
        ReadOnlySpan<byte> srcSpan, int srcStride, int srcW, int srcH,
        float sxf, float syf,
        out byte b, out byte g, out byte r, out byte a)
    {
        var fx = sxf - 0.5f;
        var fy = syf - 0.5f;
        var ix = (int)MathF.Floor(fx);
        var iy = (int)MathF.Floor(fy);

        // Bail when the 4×4 footprint sits entirely outside the source bounds.
        if (ix + 2 < 0 || iy + 2 < 0 || ix - 1 >= srcW || iy - 1 >= srcH)
        {
            b = g = r = a = 0;
            return false;
        }

        var tx = fx - ix;
        var ty = fy - iy;

        Span<float> wx = stackalloc float[4];
        Span<float> wy = stackalloc float[4];
        CatmullRomWeights(tx, wx);
        CatmullRomWeights(ty, wy);

        float bb = 0f, gg = 0f, rr = 0f, aa = 0f;
        for (var j = 0; j < 4; j++)
        {
            var ny = Math.Clamp(iy - 1 + j, 0, srcH - 1);
            var row = ny * srcStride;
            var wyj = wy[j];
            for (var i = 0; i < 4; i++)
            {
                var nx = Math.Clamp(ix - 1 + i, 0, srcW - 1);
                var idx = row + nx * 4;
                var w = wx[i] * wyj;
                bb += srcSpan[idx + 0] * w;
                gg += srcSpan[idx + 1] * w;
                rr += srcSpan[idx + 2] * w;
                aa += srcSpan[idx + 3] * w;
            }
        }

        b = (byte)Math.Clamp((int)(bb + 0.5f), 0, 255);
        g = (byte)Math.Clamp((int)(gg + 0.5f), 0, 255);
        r = (byte)Math.Clamp((int)(rr + 0.5f), 0, 255);
        a = (byte)Math.Clamp((int)(aa + 0.5f), 0, 255);
        return true;
    }

    /// <summary>
    /// Catmull-Rom kernel weights for the four taps at offsets <c>{-1, 0, 1, 2}</c> from the
    /// floored source index, evaluated at fractional position <paramref name="t"/> ∈ <c>[0, 1)</c>.
    /// Weights sum to 1 so the kernel is normalized; result has zero DC error.
    /// </summary>
    private static void CatmullRomWeights(float t, Span<float> dst)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        // Catmull-Rom: a = -0.5
        dst[0] = -0.5f * t3 + t2 - 0.5f * t;
        dst[1] = 1.5f * t3 - 2.5f * t2 + 1f;
        dst[2] = -1.5f * t3 + 2f * t2 + 0.5f * t;
        dst[3] = 0.5f * t3 - 0.5f * t2;
    }

    public void Dispose() => _disposed = true;
}
