using System.Buffers;

namespace S.Media.Core.Video;

/// <summary>
/// Pluggable CPU pixel-format converter (libswscale-style). The shipping implementation lives in
/// <c>S.Media.FFmpeg</c> and is installed via <see cref="VideoCpuFrameConverterRegistry.Factory"/>
/// during <c>FFmpegRuntime.EnsureInitialized()</c>. Core uses the interface so <see cref="VideoRouter"/>
/// and <see cref="VideoSinkFanoutFormats"/> can do branch conversion without referencing FFmpeg.
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

/// <summary>
/// Process-wide hook table that lets Core's <see cref="VideoRouter"/> create CPU pixel converters
/// without depending on FFmpeg. The S.Media.FFmpeg package installs the shipping implementation on
/// <c>FFmpegRuntime.EnsureInitialized()</c>. Other converter packages can install their own.
/// </summary>
public static class VideoCpuFrameConverterRegistry
{
    /// <summary>Returns a fresh <see cref="IVideoCpuFrameConverter"/> instance. <c>null</c> until a package installs one.</summary>
    public static Func<IVideoCpuFrameConverter>? Factory { get; set; }

    /// <summary>
    /// Probes whether the registered converter can build a scaler for the given pair. <c>null</c>
    /// until installed; treated as "no" by the fan-out picker so it falls through to alternatives.
    /// </summary>
    public static Func<PixelFormat, PixelFormat, int, int, bool>? CanConvertProbe { get; set; }

    /// <summary>Creates a converter from <see cref="Factory"/>; throws when no factory is registered.</summary>
    public static IVideoCpuFrameConverter Create() =>
        Factory?.Invoke()
            ?? throw new InvalidOperationException(
                "VideoCpuFrameConverterRegistry.Factory is not installed — reference S.Media.FFmpeg and call FFmpegRuntime.EnsureInitialized() to install the default swscale-backed converter.");

    /// <summary>Convenience that returns <c>false</c> when no probe is installed.</summary>
    public static bool CanConvert(PixelFormat src, PixelFormat dst, int width, int height) =>
        CanConvertProbe?.Invoke(src, dst, width, height) ?? false;
}

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
    /// Win32 D3D11 shared NV12) — those need a converter or a refcounted shared reference instead.
    /// </summary>
    public static VideoFrame DuplicateCpuBacking(VideoFrame source, VideoTransferHint hint)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.DmabufNv12 is not null || source.DmabufP010 is not null || source.DmabufP016 is not null)
            throw new NotSupportedException("DuplicateCpuBacking does not support DRM dma-buf frames.");
        if (source.Win32Nv12 is not null)
            throw new NotSupportedException("DuplicateCpuBacking does not support Win32 D3D11 shared-handle frames.");
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
                release: () =>
                {
                    foreach (var b in rentedBuffers)
                        ArrayPool<byte>.Shared.Return(b);
                },
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
