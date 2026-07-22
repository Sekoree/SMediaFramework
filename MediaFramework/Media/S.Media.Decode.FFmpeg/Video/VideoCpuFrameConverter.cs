using System.Buffers;
using System.Runtime.InteropServices;
using S.Media.Decode.FFmpeg.Video.Internal;

namespace S.Media.Decode.FFmpeg.Video;

/// <summary>
/// Converts CPU-backed <see cref="VideoFrame"/> instances between pixel formats using
/// FFmpeg <c>sws_scale</c> (for example high bit-depth YUV → <see cref="PixelFormat.Nv12"/> for NDI).
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> frees the <c>sws</c> context; <strong>Debug</strong> builds log failures via <see cref="MediaDiagnostics.LogError"/>.
/// </remarks>
public sealed unsafe class VideoCpuFrameConverter : IVideoCpuFrameConverter, IDisposable
{
    private SwsContext* _ctx;
    private int _width;
    private int _height;
    private PixelFormat _src;
    private PixelFormat _dst;
    private bool _disposed;

    // Per-call swscale scratch, reused across Convert calls (the converter is documented
    // non-reentrant; the router/pump serialize Convert). Nothing here escapes into emitted frames.
    private readonly byte*[] _srcLines = new byte*[8];
    private readonly int[] _srcStride = new int[8];
    private readonly byte*[] _dstLines = new byte*[8];
    private readonly int[] _dstStride8 = new int[8];
    // Pin scratch is transient too (both sides are released inside Convert, before the frame is
    // emitted); slots are reset to default after each dispose/free so the cleanup pass after a
    // mid-pin failure only ever touches live handles.
    private readonly MemoryHandle[] _srcPins = new MemoryHandle[PooledFrameRelease.MaxPlaneCount];
    private readonly GCHandle[] _dstHandles = new GCHandle[PooledFrameRelease.MaxPlaneCount];
    // Destination plane strides/lengths derive only from (_dst, _width, _height), so they are computed
    // once per Configure. Every emitted VideoFrame gets its own stride copy via its PooledFrameRelease,
    // so frames emitted under a previous configuration are unaffected by a reconfigure.
    private int[] _dstPlaneStrides = [];
    private int[] _dstPlaneLengths = [];

    /// <summary>True when libav can build a scaler for this width/height pair.</summary>
    public static bool CanConvert(PixelFormat src, PixelFormat dst, int width, int height)
    {
        FFmpegRuntime.EnsureInitialized();
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
        FFmpegRuntime.EnsureInitialized();
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

        var nDst = PixelFormatInfo.PlaneCount(dst);
        var strides = new int[nDst];
        var lengths = new int[nDst];
        for (var i = 0; i < nDst; i++)
        {
            strides[i] = PixelFormatInfo.PlaneByteWidth(dst, width, i);
            lengths[i] = checked(strides[i] * PixelFormatInfo.PlaneHeight(dst, height, i));
        }
        _dstPlaneStrides = strides;
        _dstPlaneLengths = lengths;
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
        source.ValidateCpuGeometry();

        if (_src == _dst)
            return DuplicateCpuBacking(source, hint);

        if (_ctx == null)
            throw new InvalidOperationException("Configure must be called before Convert.");

        var nSrc = PixelFormatInfo.PlaneCount(_src);
        var nDst = PixelFormatInfo.PlaneCount(_dst);
        if (source.PlaneCount != nSrc || source.Strides.Length != nSrc)
            throw new ArgumentException("unexpected source plane layout", nameof(source));

        // Pin arbitrary ReadOnlyMemory (managed arrays, pooled blocks, or unmanaged libav planes).
        // Pass-through FFmpeg frames use UnmanagedMemoryManager - GCHandle on arrays would fail.
        // Pin/handle scratch is reused across calls (the converter is non-reentrant, see above); the
        // dst plane buffers and their exact-length plane/stride arrays travel with the emitted frame
        // in a PooledFrameRelease because the frame outlives this call.
        var srcPins = _srcPins;
        Array.Clear(_srcLines);
        Array.Clear(_srcStride);
        try
        {
            for (var i = 0; i < nSrc; i++)
            {
                srcPins[i] = source.Planes[i].Pin();
                var p = (byte*)srcPins[i].Pointer;
                if (p == null)
                    throw new ArgumentException($"plane[{i}] could not be pinned for swscale", nameof(source));
                _srcLines[i] = p;
                _srcStride[i] = source.Strides[i];
            }

            var lease = PooledFrameRelease.Rent(nDst);
            var dstHandles = _dstHandles;
            Array.Clear(_dstLines);
            try
            {
                for (var i = 0; i < nDst; i++)
                {
                    var buf = lease.RentPlane(i, _dstPlaneLengths[i], _dstPlaneStrides[i]);
                    dstHandles[i] = GCHandle.Alloc(buf, GCHandleType.Pinned);
                    _dstLines[i] = (byte*)dstHandles[i].AddrOfPinnedObject();
                }

                Array.Clear(_dstStride8);
                for (var i = 0; i < nDst; i++)
                    _dstStride8[i] = _dstPlaneStrides[i];

                var ret = sws_scale(_ctx, _srcLines, _srcStride, 0, _height, _dstLines, _dstStride8);
                if (ret < 0)
                    FFmpegException.ThrowIfError(ret, nameof(sws_scale));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            finally
            {
                for (var i = 0; i < nDst; i++)
                {
                    if (dstHandles[i].IsAllocated)
                        dstHandles[i].Free();
                    dstHandles[i] = default;
                }
            }

            var fmt = new VideoFormat(_width, _height, _dst, source.Format.FrameRate);
            return new VideoFrame(source.PresentationTime, fmt, lease.Planes, lease.Strides,
                release: lease,
                metadata: source.Metadata with { ColorTransferHint = hint });
        }
        finally
        {
            for (var i = 0; i < nSrc; i++)
            {
                srcPins[i].Dispose();
                srcPins[i] = default;
            }
        }
    }

    /// <summary>Deep-copies CPU plane bytes into pool-backed memories (same layout / format).</summary>
    /// <summary>
    /// Back-compat forwarder to <see cref="VideoFrameCpuClone.DuplicateCpuBacking"/>. The body moved
    /// into Core during the Phase 3 P3.8 split; this wrapper keeps the original
    /// <c>VideoCpuFrameConverter.DuplicateCpuBacking</c> call sites working without a using-statement
    /// change.
    /// </summary>
    public static VideoFrame DuplicateCpuBacking(VideoFrame source, VideoTransferHint hint) =>
        VideoFrameCpuClone.DuplicateCpuBacking(source, hint);

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
        MediaDiagnostics.SwallowDisposeErrors(ReleaseCtx, "VideoCpuFrameConverter.Dispose");
    }
}
