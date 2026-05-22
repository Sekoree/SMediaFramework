using System.Buffers;

namespace S.Media.Effects;

/// <summary>
/// Wraps an <see cref="IVideoSource"/> so its first <see cref="Duration"/> worth of frames fades
/// from black to full-brightness pass-through. After the duration elapses (measured from the first
/// emitted frame's PTS), every subsequent frame is passed through unmodified — the wrapper goes
/// out of the way completely.
/// </summary>
/// <remarks>
/// <para>
/// Implementation: CPU-side per-pixel RGB multiply by a ramp 0→1. Supports BGRA32, Rgba32, Rgb24,
/// Bgr24, and Gray8. YUV is rejected at construction because a luma-only ramp would shift hue
/// during the fade in unexpected ways.
/// </para>
/// <para>
/// Hardware-backed frames (DMA-BUF, Win32 NV12) are not supported — the wrapper needs CPU plane
/// access to apply the ramp. For hardware paths use <see cref="LayerOpacityTween"/> on a
/// <see cref="VideoCompositorSource.Slot"/> instead.
/// </para>
/// <para>
/// The wrapped source's frames are disposed by this source after the ramped copy is created. Output
/// frames carry an <see cref="ArrayPool{T}"/>-rented buffer released via their <c>release</c> hook.
/// </para>
/// </remarks>
public sealed class FadeFromBlackVideoSource : IVideoSource, IDisposable
{
    private readonly IVideoSource _inner;
    private readonly TimeSpan _duration;
    private readonly bool _disposeInner;
    private TimeSpan? _firstPts;
    private bool _disposed;

    /// <param name="inner">Underlying source. Must deliver one of: BGRA32, Rgba32, Rgb24, Bgr24, Gray8.</param>
    /// <param name="duration">Fade length. The first emitted frame is fully black; the frame at <c>+duration</c> is full-passthrough.</param>
    /// <param name="disposeInner">When true (default), <see cref="Dispose"/> also disposes <paramref name="inner"/>.</param>
    public FadeFromBlackVideoSource(IVideoSource inner, TimeSpan duration, bool disposeInner = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "must be >= 0");
        if (!IsSupportedFormat(inner.Format.PixelFormat))
            throw new ArgumentException(
                $"FadeFromBlackVideoSource does not support {inner.Format.PixelFormat}; " +
                "use a CPU-format source (BGRA32, Rgba32, Rgb24, Bgr24, Gray8) or apply opacity via " +
                "LayerOpacityTween on a VideoCompositorSource.Slot instead.",
                nameof(inner));

        _inner = inner;
        _duration = duration;
        _disposeInner = disposeInner;
    }

    public VideoFormat Format => _inner.Format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _inner.NativePixelFormats;
    public bool IsExhausted => _disposed || _inner.IsExhausted;

    public void SelectOutputFormat(PixelFormat format)
    {
        _inner.SelectOutputFormat(format);
        if (!IsSupportedFormat(format))
            throw new InvalidOperationException(
                $"FadeFromBlackVideoSource cannot fade {format}; inner switched to an unsupported format.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        if (_disposed)
        {
            frame = null!;
            return false;
        }

        if (!_inner.TryReadNextFrame(out var inner))
        {
            frame = null!;
            return false;
        }

        // Reject hardware-backed frames; we need CPU plane access.
        if (inner.DmabufNv12 is not null || inner.DmabufP010 is not null ||
            inner.DmabufP016 is not null || inner.Win32Nv12 is not null)
        {
            inner.Dispose();
            throw new NotSupportedException(
                "FadeFromBlackVideoSource cannot ramp hardware-backed frames. " +
                "Decode in software (e.g. VideoPlaybackSmoke --no-hw) or apply opacity via " +
                "LayerOpacityTween on a VideoCompositorSource.Slot.");
        }

        _firstPts ??= inner.PresentationTime;
        var elapsed = inner.PresentationTime - _firstPts.Value;
        // After duration: passthrough unmodified.
        if (elapsed >= _duration)
        {
            frame = inner;
            return true;
        }

        var t = _duration > TimeSpan.Zero ? (float)(elapsed.TotalSeconds / _duration.TotalSeconds) : 1f;
        var ramp = Math.Clamp(t, 0f, 1f);
        frame = ApplyRamp(inner, ramp);
        inner.Dispose();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_disposeInner && _inner is IDisposable d) d.Dispose();
    }

    private static VideoFrame ApplyRamp(VideoFrame src, float ramp)
    {
        // Single-plane CPU formats only — validated in ctor / SelectOutputFormat.
        var srcSpan = src.Planes[0].Span;
        var stride = src.Strides[0];
        var byteCount = stride * src.Format.Height;
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        var dst = buffer.AsSpan(0, byteCount);
        // Integer multiplier in 0..256 so we can do `(b * mul) >> 8` (matches multiplying by ramp).
        var mul256 = (int)MathF.Round(ramp * 256f);
        if (mul256 < 0) mul256 = 0;
        if (mul256 > 256) mul256 = 256;

        switch (src.Format.PixelFormat)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Rgba32:
                // 4 bytes/pixel: scale RGB (first 3 bytes) by ramp, keep alpha (4th).
                for (var y = 0; y < src.Format.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < src.Format.Width; x++)
                    {
                        var idx = row + x * 4;
                        dst[idx + 0] = (byte)((srcSpan[idx + 0] * mul256) >> 8);
                        dst[idx + 1] = (byte)((srcSpan[idx + 1] * mul256) >> 8);
                        dst[idx + 2] = (byte)((srcSpan[idx + 2] * mul256) >> 8);
                        dst[idx + 3] = srcSpan[idx + 3];
                    }
                }
                break;
            case PixelFormat.Rgb24:
            case PixelFormat.Bgr24:
            {
                // 3 bytes/pixel: scale every byte. Stride may include row padding; loop per-row.
                var rowBytes = src.Format.Width * 3;
                for (var y = 0; y < src.Format.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < rowBytes; x++)
                        dst[row + x] = (byte)((srcSpan[row + x] * mul256) >> 8);
                    // Copy any trailing stride padding verbatim (don't ramp it).
                    if (stride > rowBytes)
                        srcSpan.Slice(row + rowBytes, stride - rowBytes).CopyTo(dst.Slice(row + rowBytes));
                }
                break;
            }
            case PixelFormat.Gray8:
            {
                var rowBytes = src.Format.Width;
                for (var y = 0; y < src.Format.Height; y++)
                {
                    var row = y * stride;
                    for (var x = 0; x < rowBytes; x++)
                        dst[row + x] = (byte)((srcSpan[row + x] * mul256) >> 8);
                    if (stride > rowBytes)
                        srcSpan.Slice(row + rowBytes, stride - rowBytes).CopyTo(dst.Slice(row + rowBytes));
                }
                break;
            }
            default:
                throw new NotSupportedException($"unreachable: pixel format {src.Format.PixelFormat}");
        }

        var owned = buffer;
        return new VideoFrame(
            src.PresentationTime,
            src.Format,
            new ReadOnlyMemory<byte>(buffer, 0, byteCount),
            stride,
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)),
            metadata: src.Metadata);
    }

    private static bool IsSupportedFormat(PixelFormat pf) => pf is
        PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Rgb24 or
        PixelFormat.Bgr24 or PixelFormat.Gray8;
}
