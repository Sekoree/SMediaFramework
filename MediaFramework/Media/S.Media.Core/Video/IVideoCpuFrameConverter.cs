using System.Buffers;

namespace S.Media.Core.Video;

/// <summary>
/// Pluggable CPU pixel-format converter (libswscale-style). The shipping implementation lives in
/// <c>S.Media.FFmpeg</c> and is resolved through the media registry (<c>IMediaRegistry.CreateCpuConverter</c>).
/// Core uses the interface so the router can do branch conversion without referencing FFmpeg.
/// </summary>
public interface IVideoCpuFrameConverter : IDisposable
{
    /// <summary>Configure or reconfigure for a given source/destination pixel format pair and dimensions.</summary>
    void Configure(PixelFormat src, PixelFormat dst, int width, int height);

    /// <summary>
    /// Converts <paramref name="source"/> into a new frame. Caller owns the returned frame and must
    /// dispose it. <paramref name="source"/> is not disposed by the converter.
    /// </summary>
    VideoFrame Convert(VideoFrame source, VideoTransferHint hint);
}

// Phase 1: the old `VideoCpuFrameConverterRegistry` (a process-wide hook over MediaFrameworkPlugins) is
// removed - CPU converters are now resolved through `IMediaRegistry` (see Registry/). The interface above
// and the pure-managed `VideoFrameCpuClone` below stay in Core.

/// <summary>
/// Pure-managed helpers that operate on CPU-backed <see cref="VideoFrame"/> planes without
/// touching FFmpeg. Used by the router's fan-out path for "duplicate the CPU planes so each branch
/// owns its own buffer" cases where no real pixel conversion is needed.
/// </summary>
public static class VideoFrameCpuClone
{
    /// <summary>
    /// Duplicates the CPU plane bytes of <paramref name="source"/> into pooled buffers and returns
    /// a new <see cref="VideoFrame"/> that owns them. Throws for hardware backings (DRM dma-buf /
    /// Win32 D3D11 shared NV12) - those need a converter or a refcounted shared reference instead.
    /// </summary>
    public static VideoFrame DuplicateCpuBacking(VideoFrame source, VideoTransferHint hint)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.DmabufNv12 is not null || source.DmabufP010 is not null || source.DmabufP016 is not null)
            throw new NotSupportedException("DuplicateCpuBacking does not support DRM dma-buf frames.");
        if (source.Win32Nv12 is not null)
            throw new NotSupportedException("DuplicateCpuBacking does not support Win32 D3D11 shared-handle frames.");
        source.ValidateCpuGeometry();
        var fmt = source.Format.PixelFormat;
        var fw = source.Format.Width;
        var fh = source.Format.Height;
        var n = PixelFormatInfo.PlaneCount(fmt);
        var stridesOut = new int[n];
        var planes = new ReadOnlyMemory<byte>[n];
        List<byte[]> rentedBuffers = [];
        try
        {
            for (var i = 0; i < n; i++)
            {
                var stride = source.Strides[i];
                stridesOut[i] = stride;
                var totalBytes = PixelFormatInfo.PlanePitchBufferLength(fmt, fw, fh, i, stride);
                var planeSrc = source.Planes[i];
                if (planeSrc.Length < totalBytes)
                    throw new ArgumentException($"plane[{i}] shorter than contiguous pitch buffer", nameof(source));

                var buf = ArrayPool<byte>.Shared.Rent(totalBytes);
                rentedBuffers.Add(buf);
                planeSrc.Span[..totalBytes].CopyTo(buf);
                planes[i] = buf.AsMemory(0, totalBytes);
            }

            return new VideoFrame(source.PresentationTime, source.Format, planes, stridesOut,
                release: DisposableRelease.Wrap(() =>
                {
                    foreach (var b in rentedBuffers)
                        ArrayPool<byte>.Shared.Return(b);
                }),
                metadata: source.Metadata with { ColorTransferHint = hint });
        }
        catch
        {
            foreach (var b in rentedBuffers)
                ArrayPool<byte>.Shared.Return(b);
            throw;
        }
    }
}
