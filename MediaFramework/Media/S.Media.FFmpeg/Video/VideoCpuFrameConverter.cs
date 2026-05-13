using System.Buffers;
using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.FFmpeg.Internal;
using S.Media.FFmpeg.Video.Internal;

namespace S.Media.FFmpeg.Video;

/// <summary>
/// Converts CPU-backed <see cref="VideoFrame"/> instances between pixel formats using
/// FFmpeg <c>sws_scale</c> (for example high bit-depth YUV → <see cref="PixelFormat.Nv12"/> for NDI).
/// </summary>
public sealed unsafe class VideoCpuFrameConverter : IDisposable
{
    private SwsContext* _ctx;
    private int _width;
    private int _height;
    private PixelFormat _src;
    private PixelFormat _dst;
    private bool _disposed;

    /// <summary>True when libav can build a scaler for this width/height pair.</summary>
    public static bool CanConvert(PixelFormat src, PixelFormat dst, int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (src == dst) return true;
        var avSrc = FfmpegVideoPixelMaps.ToAvPixelFormat(src);
        var avDst = FfmpegVideoPixelMaps.ToAvPixelFormat(dst);
        if (avSrc is null || avDst is null) return false;
        var probe = sws_getCachedContext(null, width, height, avSrc.Value,
            width, height, avDst.Value, (int)SwsFlags.SWS_BICUBIC, null, null, null);
        if (probe == null) return false;
        sws_freeContext(probe);
        return true;
    }

    /// <summary>Reconfigures the scaler when dimensions or formats change.</summary>
    public void Configure(PixelFormat src, PixelFormat dst, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (src == dst)
        {
            ReleaseCtx();
            _src = src;
            _dst = dst;
            _width = width;
            _height = height;
            return;
        }

        var avSrc = FfmpegVideoPixelMaps.ToAvPixelFormat(src)
            ?? throw new NotSupportedException($"no FFmpeg pixel mapping for source {src}");
        var avDst = FfmpegVideoPixelMaps.ToAvPixelFormat(dst)
            ?? throw new NotSupportedException($"no FFmpeg pixel mapping for destination {dst}");

        if (_ctx != null && _src == src && _dst == dst && _width == width && _height == height)
            return;

        ReleaseCtx();
        _ctx = sws_getCachedContext(null, width, height, avSrc,
            width, height, avDst, (int)SwsFlags.SWS_BICUBIC, null, null, null);
        if (_ctx == null)
            throw new FFmpegException(0, $"sws_getCachedContext {src}→{dst} returned NULL");
        _src = src;
        _dst = dst;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Converts <paramref name="source"/> into a new frame in <see cref="_dst"/>.
    /// Caller must dispose the returned frame. <paramref name="source"/> is not disposed.
    /// </summary>
    public VideoFrame Convert(VideoFrame source, VideoTransferHint hint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (source.DmabufNv12 is not null || source.DmabufP010 is not null || source.DmabufP016 is not null)
            throw new NotSupportedException("VideoCpuFrameConverter does not accept DRM dma-buf frames.");
        if (source.Win32Nv12 is not null)
            throw new NotSupportedException("VideoCpuFrameConverter does not accept Win32 D3D11 shared-handle frames.");

        if (source.Format.Width != _width || source.Format.Height != _height || source.Format.PixelFormat != _src)
            throw new ArgumentException(
                $"source format {source.Format} does not match converter ({_width}x{_height} {_src})", nameof(source));

        if (_src == _dst)
            return DuplicateCpuBacking(source, hint);

        if (_ctx == null)
            throw new InvalidOperationException("Configure must be called before Convert.");

        var nSrc = PixelFormatInfo.PlaneCount(_src);
        var nDst = PixelFormatInfo.PlaneCount(_dst);
        if (source.PlaneCount != nSrc || source.Strides.Length != nSrc)
            throw new ArgumentException("unexpected source plane layout", nameof(source));

        // Pin arbitrary ReadOnlyMemory (managed arrays, pooled blocks, or unmanaged libav planes).
        // Pass-through FFmpeg frames use UnmanagedMemoryManager — GCHandle on arrays would fail.
        var srcPins = new MemoryHandle[nSrc];
        var srcLines = new byte*[8];
        var srcStride = new int[8];
        Array.Clear(srcStride);
        try
        {
            for (var i = 0; i < nSrc; i++)
            {
                srcPins[i] = source.Planes[i].Pin();
                var p = (byte*)srcPins[i].Pointer;
                if (p == null)
                    throw new ArgumentException($"plane[{i}] could not be pinned for swscale", nameof(source));
                srcLines[i] = p;
                srcStride[i] = source.Strides[i];
            }

            var dstStrides = new int[nDst];
            for (var i = 0; i < nDst; i++)
                dstStrides[i] = PixelFormatInfo.PlaneByteWidth(_dst, _width, i);

            var dstMemories = new ReadOnlyMemory<byte>[nDst];
            var dstBuffers = new byte[nDst][];
            for (var i = 0; i < nDst; i++)
            {
                var ph = PixelFormatInfo.PlaneHeight(_dst, _height, i);
                var len = checked(dstStrides[i] * ph);
                var buf = ArrayPool<byte>.Shared.Rent(len);
                dstBuffers[i] = buf;
                dstMemories[i] = buf.AsMemory(0, len);
            }

            var dstHandles = new GCHandle[nDst];
            var dstLines = new byte*[8];
            Array.Clear(dstLines);
            try
            {
                for (var i = 0; i < nDst; i++)
                {
                    if (!MemoryMarshal.TryGetArray(dstMemories[i], out var seg) || seg.Array is null)
                        throw new InvalidOperationException("internal: dst plane not array-backed");
                    dstHandles[i] = GCHandle.Alloc(seg.Array, GCHandleType.Pinned);
                    dstLines[i] = (byte*)dstHandles[i].AddrOfPinnedObject() + seg.Offset;
                }

                var dstStrideArr = new int[8];
                for (var i = 0; i < nDst; i++)
                    dstStrideArr[i] = dstStrides[i];

                var ret = sws_scale(_ctx, srcLines, srcStride, 0, _height, dstLines, dstStrideArr);
                if (ret < 0)
                    FFmpegException.ThrowIfError(ret, nameof(sws_scale));
            }
            finally
            {
                foreach (var h in dstHandles)
                {
                    if (h.IsAllocated)
                        h.Free();
                }
            }

            var fmt = new VideoFormat(_width, _height, _dst, source.Format.FrameRate);
            return new VideoFrame(source.PresentationTime, fmt, dstMemories, dstStrides, hint, release: () =>
            {
                foreach (var b in dstBuffers)
                    ArrayPool<byte>.Shared.Return(b);
            });
        }
        finally
        {
            foreach (var h in srcPins)
                h.Dispose();
        }
    }

    /// <summary>Deep-copies CPU plane bytes into pool-backed memories (same layout / format).</summary>
    public static VideoFrame DuplicateCpuBacking(VideoFrame source, VideoTransferHint hint)
    {
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

            return new VideoFrame(source.PresentationTime, source.Format, planes, stridesOut, hint, release: () =>
            {
                foreach (var b in rentedBuffers)
                    ArrayPool<byte>.Shared.Return(b);
            });
        }
        catch
        {
            foreach (var b in rentedBuffers)
                ArrayPool<byte>.Shared.Return(b);
            throw;
        }
    }

    private void ReleaseCtx()
    {
        if (_ctx == null) return;
        sws_freeContext(_ctx);
        _ctx = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseCtx();
    }
}
