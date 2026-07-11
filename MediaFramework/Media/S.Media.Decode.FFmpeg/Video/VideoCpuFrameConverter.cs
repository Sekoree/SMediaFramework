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
    // Destination plane strides/lengths derive only from (_dst, _width, _height), so they are computed
    // once per Configure. _dstPlaneStrides is handed to every emitted VideoFrame (whose Strides contract
    // is do-not-mutate), so Configure REPLACES the array instead of mutating it - frames emitted under a
    // previous configuration keep their own copy.
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
        // The pin/handle arrays stay per-call (fresh defaults make the finally blocks trivially safe);
        // the 8-slot line/stride scratch and the per-Configure stride table are reused.
        var srcPins = new MemoryHandle[nSrc];
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

            var dstStrides = _dstPlaneStrides;

            var dstMemories = new ReadOnlyMemory<byte>[nDst];
            var dstBuffers = new byte[nDst][];
            for (var i = 0; i < nDst; i++)
            {
                var len = _dstPlaneLengths[i];
                var buf = ArrayPool<byte>.Shared.Rent(len);
                dstBuffers[i] = buf;
                dstMemories[i] = buf.AsMemory(0, len);
            }

            var dstHandles = new GCHandle[nDst];
            Array.Clear(_dstLines);
            try
            {
                for (var i = 0; i < nDst; i++)
                {
                    if (!MemoryMarshal.TryGetArray(dstMemories[i], out var seg) || seg.Array is null)
                        throw new InvalidOperationException("internal: dst plane not array-backed");
                    dstHandles[i] = GCHandle.Alloc(seg.Array, GCHandleType.Pinned);
                    _dstLines[i] = (byte*)dstHandles[i].AddrOfPinnedObject() + seg.Offset;
                }

                Array.Clear(_dstStride8);
                for (var i = 0; i < nDst; i++)
                    _dstStride8[i] = dstStrides[i];

                var ret = sws_scale(_ctx, _srcLines, _srcStride, 0, _height, _dstLines, _dstStride8);
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
            return new VideoFrame(source.PresentationTime, fmt, dstMemories, dstStrides,
                release: DisposableRelease.Wrap(() =>
                {
                    foreach (var b in dstBuffers)
                        ArrayPool<byte>.Shared.Return(b);
                }),
                metadata: source.Metadata with { ColorTransferHint = hint });
        }
        finally
        {
            foreach (var h in srcPins)
                h.Dispose();
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
