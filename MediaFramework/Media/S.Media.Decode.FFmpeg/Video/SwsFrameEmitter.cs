using System.Buffers;
using System.Runtime.InteropServices;

namespace S.Media.Decode.FFmpeg.Video;

/// <summary>
/// The single implementation of "sws_scale the decoded frame into pooled buffers and wrap them as a
/// <see cref="VideoFrame"/>" shared by <see cref="VideoFileDecoder"/> and
/// <c>MediaContainerSharedDemux</c>. Both used to carry verbatim copies of the packed and planar
/// emit paths (rent → pin → sws_scale → pooled release), so fixes had to land twice; the delicate
/// parts - the pin/rollback bookkeeping and the release closures - now live here only.
/// </summary>
/// <remarks>
/// Each decode context owns one instance: the 8-slot line/stride scratch is reused across frames and
/// is <strong>not</strong> thread-safe (both owners already serialize their video decode path).
/// <c>srcSliceHeight</c> is the SOURCE frame height handed to <c>sws_scale</c> - it may differ from
/// the output <paramref name="format"/> height on the shared demux's odd-dimension attached-pic path
/// (output dims are rounded up to even; the source is not).
/// </remarks>
internal sealed unsafe class SwsFrameEmitter
{
    private readonly byte*[] _srcLines = new byte*[8];
    private readonly int[] _srcStride = new int[8];
    private readonly byte*[] _dstLines = new byte*[8];
    private readonly int[] _dstStride = new int[8];

    /// <summary>
    /// Converts <paramref name="work"/> into a pooled <see cref="VideoFrame"/> in
    /// <paramref name="outPixelFormat"/>: packed single-plane targets (BGRA32 / packed RGB) take the
    /// contiguous path; Nv12 / Nv21 / I420 take the per-plane path. Other formats throw.
    /// </summary>
    public VideoFrame BuildConvertedFrame(
        SwsContext* sws, AVFrame* work, VideoFormat format, PixelFormat outPixelFormat, int srcSliceHeight,
        TimeSpan pts, VideoFrameMetadata meta)
    {
        var bytesPerPixel = VideoFileDecoder.BytesPerPackedPixel(outPixelFormat);
        if (bytesPerPixel == 0)
        {
            if (outPixelFormat is PixelFormat.Nv12 or PixelFormat.Nv21 or PixelFormat.I420)
                return BuildPlanarFrame(sws, work, format, outPixelFormat, srcSliceHeight, pts, meta);
            throw new NotSupportedException(
                $"sws conversion to {outPixelFormat} is not implemented - use BGRA32, NV12, I420, or a packed RGB layout.");
        }

        return BuildPackedFrame(sws, work, format, bytesPerPixel, srcSliceHeight, pts, meta);
    }

    private VideoFrame BuildPackedFrame(
        SwsContext* sws, AVFrame* work, VideoFormat format, int bytesPerPixel, int srcSliceHeight,
        TimeSpan pts, VideoFrameMetadata meta)
    {
        var width = format.Width;
        var height = format.Height;
        var stride = width * bytesPerPixel;
        var contiguous = stride * height;
        var rented = ArrayPool<byte>.Shared.Rent(contiguous);
        var dstMem = rented.AsMemory(0, contiguous);

        LoadSourceLines(work);

        fixed (byte* dstPtr = dstMem.Span)
        {
            _dstLines[0] = dstPtr;
            for (var i = 1; i < 8; i++) _dstLines[i] = null;
            Array.Clear(_dstStride);
            _dstStride[0] = stride;

            var ret = sws_scale(sws, _srcLines, _srcStride, 0, srcSliceHeight, _dstLines, _dstStride);
            if (ret < 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                FFmpegException.ThrowIfError(ret, nameof(sws_scale));
            }
        }

        byte[] pooled = rented;
        return new VideoFrame(pts, format, dstMem, stride,
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(pooled)),
            metadata: meta);
    }

    /// <summary>
    /// Planar-target sws path (Nv12 / Nv21 / I420). Allocates one rented buffer per plane (sized via
    /// <see cref="PixelFormatInfo.PlanePitchBufferLength"/>), pins for the duration of sws_scale, then
    /// returns a <see cref="VideoFrame"/> whose release callback returns every plane to the pool.
    /// </summary>
    private VideoFrame BuildPlanarFrame(
        SwsContext* sws, AVFrame* work, VideoFormat format, PixelFormat outPixelFormat, int srcSliceHeight,
        TimeSpan pts, VideoFrameMetadata meta)
    {
        var width = format.Width;
        var height = format.Height;
        var n = PixelFormatInfo.PlaneCount(outPixelFormat);

        LoadSourceLines(work);

        var strides = new int[n];
        for (var i = 0; i < n; i++)
            strides[i] = PixelFormatInfo.PlaneByteWidth(outPixelFormat, width, i);

        var buffers = new byte[n][];
        // Pinning scratch is transient (freed before return), so keep it on the stack - only the plane
        // count's worth (n ≤ 4 for any planar output). `allocated` tracks how many were pinned so a
        // mid-loop Rent failure frees exactly those.
        Span<GCHandle> handles = stackalloc GCHandle[n];
        var allocated = 0;
        try
        {
            for (var i = 0; i < n; i++)
            {
                var len = PixelFormatInfo.PlanePitchBufferLength(outPixelFormat, width, height, i, strides[i]);
                buffers[i] = ArrayPool<byte>.Shared.Rent(len);
                handles[i] = GCHandle.Alloc(buffers[i], GCHandleType.Pinned);
                allocated = i + 1;
            }

            for (var i = 0; i < n; i++)
                _dstLines[i] = (byte*)handles[i].AddrOfPinnedObject();
            for (var i = n; i < 8; i++)
                _dstLines[i] = null;

            Array.Clear(_dstStride);
            for (var i = 0; i < n; i++)
                _dstStride[i] = strides[i];

            var ret = sws_scale(sws, _srcLines, _srcStride, 0, srcSliceHeight, _dstLines, _dstStride);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(sws_scale));
        }
        catch
        {
            for (var i = 0; i < allocated; i++)
                handles[i].Free();
            foreach (var b in buffers)
            {
                if (b is not null)
                    ArrayPool<byte>.Shared.Return(b);
            }
            throw;
        }

        for (var i = 0; i < n; i++)
            handles[i].Free();

        var memories = new ReadOnlyMemory<byte>[n];
        for (var i = 0; i < n; i++)
        {
            var len = PixelFormatInfo.PlanePitchBufferLength(outPixelFormat, width, height, i, strides[i]);
            memories[i] = buffers[i].AsMemory(0, len);
        }

        var captured = buffers;
        return new VideoFrame(pts, format, memories, strides,
            release: DisposableRelease.Wrap(() =>
            {
                foreach (var b in captured)
                    ArrayPool<byte>.Shared.Return(b);
            }),
            metadata: meta);
    }

    private void LoadSourceLines(AVFrame* work)
    {
        for (var i = 0; i < 8; i++)
        {
            _srcLines[i] = work->data[(uint)i];
            _srcStride[i] = work->linesize[(uint)i];
        }
    }
}
