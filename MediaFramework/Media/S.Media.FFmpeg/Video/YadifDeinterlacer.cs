using System.Buffers;
using System.Runtime.InteropServices;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using CorePixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.FFmpeg.Video;

/// <summary>
/// FFmpeg-based deinterlacer using libavfilter's <c>yadif</c> filter (mode 0 — send_frame, frame-rate
/// preserving). Builds a graph of <c>buffer → yadif → buffersink</c> per <see cref="Configure"/> and
/// pushes/pulls frames via <c>av_buffersrc_add_frame</c> / <c>av_buffersink_get_frame</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as the default <see cref="VideoDeinterlacerRegistry.Factory"/> by
/// <see cref="FFmpegRuntime.EnsureInitialized"/>. Core consumers get yadif when they reference
/// <c>S.Media.FFmpeg</c>; consumers that don't fall back to <see cref="BobDeinterlacer"/>.
/// </para>
/// <para>
/// Supported input pixel formats: I420 (yuv420p) — covers DV, MPEG-2, broadcast HD interlaced
/// content. NV12 is converted to I420 internally on input and back to NV12 on output (yadif's
/// in-tree NV12 support is incomplete on some FFmpeg builds; the round-trip is robust). Other
/// formats throw at <see cref="Configure"/>.
/// </para>
/// <para>
/// Progressive input bypasses the filter graph — <see cref="Process"/> returns the original frame
/// as a single output (caller still owns disposal).
/// </para>
/// </remarks>
public sealed unsafe class YadifDeinterlacer : IDeinterlacer
{
    private VideoFormat _input;
    private VideoFormat _output;
    private AVFilterGraph* _graph;
    private AVFilterContext* _src;
    private AVFilterContext* _sink;
    private AVFrame* _scratchIn;
    private AVFrame* _scratchOut;
    private bool _configured;
    private bool _disposed;
    private int _pushCounter;

    /// <summary>Construct from <paramref name="input"/>; <see cref="Configure"/> is called immediately.</summary>
    public YadifDeinterlacer(VideoFormat input)
    {
        FFmpegRuntime.EnsureInitialized();
        Configure(input);
    }

    public VideoFormat InputFormat => _input;
    public VideoFormat OutputFormat => _output;

    public void Configure(VideoFormat input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.PixelFormat is not (CorePixelFormat.I420 or CorePixelFormat.Nv12))
            throw new ArgumentException(
                $"YadifDeinterlacer only supports I420 / Nv12 input; got {input.PixelFormat}. " +
                "Use BobDeinterlacer for other formats or pre-convert via the existing CPU converter.",
                nameof(input));
        if (input.Width <= 0 || input.Height <= 0)
            throw new ArgumentException($"invalid dimensions {input.Width}x{input.Height}", nameof(input));

        TearDownGraph();
        _input = input;
        _output = input; // yadif mode 0 preserves frame rate and format (we convert NV12↔I420 internally).

        BuildGraph();
        _configured = true;
    }

    private void BuildGraph()
    {
        // Graph is always built around yuv420p internally — yadif's most reliable accepted format.
        var width = _input.Width;
        var height = _input.Height;
        var rate = _input.FrameRate;
        var num = rate.Numerator > 0 ? rate.Numerator : 25;
        var den = rate.Denominator > 0 ? rate.Denominator : 1;

        _graph = avfilter_graph_alloc();
        if (_graph == null) throw new OutOfMemoryException("avfilter_graph_alloc returned NULL");

        try
        {
            var bufferFilter = avfilter_get_by_name("buffer");
            var sinkFilter = avfilter_get_by_name("buffersink");
            if (bufferFilter == null || sinkFilter == null)
                throw new InvalidOperationException(
                    "libavfilter 'buffer' / 'buffersink' filters unavailable — link with libavfilter.");
            var yadifFilter = avfilter_get_by_name("yadif");
            if (yadifFilter == null)
                throw new InvalidOperationException(
                    "libavfilter 'yadif' filter unavailable — FFmpeg build is missing --enable-filter=yadif.");

            // Internal format always yuv420p — minimal surprise for yadif and matches I420 byte layout
            // bit-for-bit. NV12 inputs round-trip through I420 (Configure throws on other formats).
            var pixFmt = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            var srcArgs = $"video_size={width}x{height}:pix_fmt={pixFmt}:time_base={den}/{num}:pixel_aspect=1/1";

            AVFilterContext* srcCtx = null;
            AVFilterContext* yadifCtx = null;
            AVFilterContext* sinkCtx = null;

            var ret = avfilter_graph_create_filter(&srcCtx, bufferFilter, "in", srcArgs, null, _graph);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(avfilter_graph_create_filter));

            ret = avfilter_graph_create_filter(&yadifCtx, yadifFilter, "yadif", "mode=0:parity=auto:deint=interlaced", null, _graph);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(avfilter_graph_create_filter));

            ret = avfilter_graph_create_filter(&sinkCtx, sinkFilter, "out", null, null, _graph);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(avfilter_graph_create_filter));

            ret = avfilter_link(srcCtx, 0, yadifCtx, 0);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(avfilter_link));
            ret = avfilter_link(yadifCtx, 0, sinkCtx, 0);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(avfilter_link));

            ret = avfilter_graph_config(_graph, null);
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(avfilter_graph_config));

            _src = srcCtx;
            _sink = sinkCtx;
            _scratchIn = av_frame_alloc();
            _scratchOut = av_frame_alloc();
            if (_scratchIn == null || _scratchOut == null)
                throw new OutOfMemoryException("av_frame_alloc returned NULL");
        }
        catch
        {
            TearDownGraph();
            throw;
        }
    }

    public int Process(VideoFrame frame, Span<VideoFrame?> outputs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (outputs.Length < 1)
            throw new ArgumentException("outputs span must have at least 1 slot", nameof(outputs));
        if (!_configured)
            throw new InvalidOperationException("YadifDeinterlacer must be Configure()d before Process.");

        if (frame.FieldOrder == VideoFieldOrder.Progressive)
        {
            outputs[0] = frame;
            return 1;
        }

        // Push the interlaced frame into the buffer source.
        var pushed = PushFrame(frame);
        if (!pushed)
        {
            // FFmpeg dropped the frame; emit no outputs.
            return 0;
        }

        // Pull as many progressive frames as the filter is willing to emit. Yadif mode 0 normally
        // emits one per input; defensive loop in case the filter changes that.
        var produced = 0;
        while (produced < outputs.Length)
        {
            var ret = av_buffersink_get_frame(_sink, _scratchOut);
            if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF) break;
            if (ret < 0) FFmpegException.ThrowIfError(ret, nameof(av_buffersink_get_frame));

            outputs[produced++] = BuildOutputFrame(_scratchOut, frame);
            av_frame_unref(_scratchOut);
        }

        return produced;
    }

    private bool PushFrame(VideoFrame frame)
    {
        av_frame_unref(_scratchIn);
        _scratchIn->width = _input.Width;
        _scratchIn->height = _input.Height;
        _scratchIn->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        _scratchIn->pts = ++_pushCounter;
        _scratchIn->flags |= AV_FRAME_FLAG_INTERLACED;
        if (frame.FieldOrder == VideoFieldOrder.TopFieldFirst)
            _scratchIn->flags |= AV_FRAME_FLAG_TOP_FIELD_FIRST;

        var ret = av_frame_get_buffer(_scratchIn, 32);
        if (ret < 0)
        {
#if DEBUG
            MediaDiagnostics.LogWarning("YadifDeinterlacer: av_frame_get_buffer failed ({0}); dropping frame.", ret);
#endif
            return false;
        }

        // Copy plane data into the allocated AVFrame. Input is either I420 (3 planes) or NV12 (2);
        // for NV12 we interleave UV → split into U + V to match yuv420p.
        var w = _input.Width;
        var h = _input.Height;
        var halfW = w / 2;
        var halfH = h / 2;

        if (frame.Format.PixelFormat == CorePixelFormat.I420)
        {
            CopyPlane(frame.Planes[0].Span, frame.Strides[0], _scratchIn->data[0], _scratchIn->linesize[0], w, h);
            CopyPlane(frame.Planes[1].Span, frame.Strides[1], _scratchIn->data[1], _scratchIn->linesize[1], halfW, halfH);
            CopyPlane(frame.Planes[2].Span, frame.Strides[2], _scratchIn->data[2], _scratchIn->linesize[2], halfW, halfH);
        }
        else
        {
            // NV12 → yuv420p: split UV plane into U + V.
            CopyPlane(frame.Planes[0].Span, frame.Strides[0], _scratchIn->data[0], _scratchIn->linesize[0], w, h);
            SplitNv12UvToI420(
                frame.Planes[1].Span, frame.Strides[1],
                _scratchIn->data[1], _scratchIn->linesize[1],
                _scratchIn->data[2], _scratchIn->linesize[2],
                halfW, halfH);
        }

        ret = av_buffersrc_add_frame_flags(_src, _scratchIn, (int)AV_BUFFERSRC_FLAG_PUSH);
        av_frame_unref(_scratchIn);
        if (ret < 0)
        {
#if DEBUG
            MediaDiagnostics.LogWarning("YadifDeinterlacer: av_buffersrc_add_frame_flags failed ({0}); dropping frame.", ret);
#endif
            return false;
        }
        return true;
    }

    private VideoFrame BuildOutputFrame(AVFrame* deinterlaced, VideoFrame original)
    {
        var w = _input.Width;
        var h = _input.Height;
        var halfW = w / 2;
        var halfH = h / 2;

        // Output is yuv420p; if input was NV12, we re-interleave U+V → UV.
        var inputIsNv12 = original.Format.PixelFormat == CorePixelFormat.Nv12;
        if (inputIsNv12)
        {
            var yStride = w;
            var uvStride = w; // NV12 UV is 2 bytes per chroma sample, full width.
            var yBytes = yStride * h;
            var uvBytes = uvStride * halfH;
            var totalBytes = yBytes + uvBytes;
            var buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
            var span = buffer.AsSpan(0, totalBytes);
            CopyPlaneOut(deinterlaced->data[0], deinterlaced->linesize[0], span.Slice(0, yBytes), yStride, w, h);
            InterleaveI420UvToNv12(
                deinterlaced->data[1], deinterlaced->linesize[1],
                deinterlaced->data[2], deinterlaced->linesize[2],
                span.Slice(yBytes, uvBytes), uvStride, halfW, halfH);

            var planes = new ReadOnlyMemory<byte>[]
            {
                new(buffer, 0, yBytes),
                new(buffer, yBytes, uvBytes),
            };
            var strides = new[] { yStride, uvStride };
            var owned = buffer;
            return new VideoFrame(
                original.PresentationTime, _output, planes, strides,
                release: () => ArrayPool<byte>.Shared.Return(owned, clearArray: false),
                metadata: original.Metadata with { FieldOrder = VideoFieldOrder.Progressive });
        }
        else
        {
            // I420 round-trip.
            var yStride = w;
            var cStride = halfW;
            var yBytes = yStride * h;
            var cBytes = cStride * halfH;
            var totalBytes = yBytes + 2 * cBytes;
            var buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
            var span = buffer.AsSpan(0, totalBytes);
            CopyPlaneOut(deinterlaced->data[0], deinterlaced->linesize[0], span.Slice(0, yBytes), yStride, w, h);
            CopyPlaneOut(deinterlaced->data[1], deinterlaced->linesize[1], span.Slice(yBytes, cBytes), cStride, halfW, halfH);
            CopyPlaneOut(deinterlaced->data[2], deinterlaced->linesize[2], span.Slice(yBytes + cBytes, cBytes), cStride, halfW, halfH);

            var planes = new ReadOnlyMemory<byte>[]
            {
                new(buffer, 0, yBytes),
                new(buffer, yBytes, cBytes),
                new(buffer, yBytes + cBytes, cBytes),
            };
            var strides = new[] { yStride, cStride, cStride };
            var owned = buffer;
            return new VideoFrame(
                original.PresentationTime, _output, planes, strides,
                release: () => ArrayPool<byte>.Shared.Return(owned, clearArray: false),
                metadata: original.Metadata with { FieldOrder = VideoFieldOrder.Progressive });
        }
    }

    private static void CopyPlane(ReadOnlySpan<byte> src, int srcStride, byte* dst, int dstStride, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            var srcRow = src.Slice(y * srcStride, w);
            for (var x = 0; x < w; x++)
                dst[y * dstStride + x] = srcRow[x];
        }
    }

    private static void CopyPlaneOut(byte* src, int srcStride, Span<byte> dst, int dstStride, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
                dst[y * dstStride + x] = src[y * srcStride + x];
        }
    }

    private static void SplitNv12UvToI420(
        ReadOnlySpan<byte> uvSrc, int uvSrcStride,
        byte* uDst, int uDstStride,
        byte* vDst, int vDstStride,
        int halfW, int halfH)
    {
        for (var y = 0; y < halfH; y++)
        {
            var row = uvSrc.Slice(y * uvSrcStride, halfW * 2);
            for (var x = 0; x < halfW; x++)
            {
                uDst[y * uDstStride + x] = row[x * 2];
                vDst[y * vDstStride + x] = row[x * 2 + 1];
            }
        }
    }

    private static void InterleaveI420UvToNv12(
        byte* uSrc, int uSrcStride,
        byte* vSrc, int vSrcStride,
        Span<byte> uvDst, int uvDstStride,
        int halfW, int halfH)
    {
        for (var y = 0; y < halfH; y++)
        {
            for (var x = 0; x < halfW; x++)
            {
                uvDst[y * uvDstStride + x * 2] = uSrc[y * uSrcStride + x];
                uvDst[y * uvDstStride + x * 2 + 1] = vSrc[y * vSrcStride + x];
            }
        }
    }

    private void TearDownGraph()
    {
        if (_scratchIn != null)
        {
            var p = _scratchIn;
            av_frame_free(&p);
            _scratchIn = null;
        }
        if (_scratchOut != null)
        {
            var p = _scratchOut;
            av_frame_free(&p);
            _scratchOut = null;
        }
        if (_graph != null)
        {
            var g = _graph;
            avfilter_graph_free(&g);
            _graph = null;
        }
        _src = null;
        _sink = null;
        _configured = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TearDownGraph();
    }

    // FFmpeg.AutoGen 8.x exposes these as int consts via ffmpeg.* — declare locally so we don't depend on
    // bindings to surface them by the same name.
    private const int AV_FRAME_FLAG_INTERLACED = 1 << 3;
    private const int AV_FRAME_FLAG_TOP_FIELD_FIRST = 1 << 4;
    private const uint AV_BUFFERSRC_FLAG_PUSH = 4;
}
