using System.Buffers;

namespace S.Media.Core.Video;

/// <summary>
/// Simple "Bob" line-doubling deinterlacer. For each interlaced input, emits two progressive
/// outputs at double the frame rate: one built from the top field (even lines) with odd lines
/// vertically interpolated, one built from the bottom field (odd lines) with even lines
/// interpolated.
/// </summary>
/// <remarks>
/// <para>
/// Production paths should prefer the FFmpeg <c>yadif</c> implementation
/// (<c>S.Media.FFmpeg.Video.YadifDeinterlacer</c>) resolved through the media registry (<c>IMediaRegistry.CreateDeinterlacer</c>).
/// Bob exists so headless test paths and FFmpeg-less Core consumers have a deinterlacer at all.
/// </para>
/// <para>
/// Supported input formats: BGRA32 / RGBA32 (any 4-byte packed format that's literally pixel-aligned),
/// I420 (3 planes), NV12 (2 planes). Other formats throw at <see cref="Configure"/>.
/// </para>
/// <para>
/// Progressive input bypasses the deinterlacer — <see cref="Process"/> returns the original frame
/// as a single output (caller still owns disposal of the inner frame).
/// </para>
/// </remarks>
public sealed class BobDeinterlacer : IDeinterlacer
{
    private VideoFormat _input;
    private VideoFormat _output;
    private bool _configured;
    private bool _disposed;

    public BobDeinterlacer(VideoFormat input)
    {
        Configure(input);
    }

    public VideoFormat InputFormat => _input;
    public VideoFormat OutputFormat => _output;

    public void Configure(VideoFormat input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.Width <= 0 || input.Height <= 0)
            throw new ArgumentException($"invalid dimensions {input.Width}x{input.Height}", nameof(input));
        if (input.PixelFormat is not (PixelFormat.Bgra32 or PixelFormat.Rgba32
            or PixelFormat.I420 or PixelFormat.Nv12))
            throw new ArgumentException(
                $"BobDeinterlacer does not support {input.PixelFormat}; use BGRA32/Rgba32/I420/NV12.",
                nameof(input));
        if ((input.Height & 1) != 0)
            throw new ArgumentException($"input height must be even (got {input.Height})", nameof(input));

        _input = input;
        // Bob doubles the temporal rate.
        var doubled = input.FrameRate.Denominator > 0
            ? new Rational(input.FrameRate.Numerator * 2, input.FrameRate.Denominator)
            : input.FrameRate;
        _output = new VideoFormat(input.Width, input.Height, input.PixelFormat, doubled);
        _configured = true;
    }

    public int Process(VideoFrame frame, Span<VideoFrame?> outputs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (outputs.Length < 1)
            throw new ArgumentException("outputs span must have at least 1 slot", nameof(outputs));
        if (!_configured)
            throw new InvalidOperationException("BobDeinterlacer must be Configure()d before Process.");

        if (frame.Format.PixelFormat != _input.PixelFormat ||
            frame.Format.Width != _input.Width || frame.Format.Height != _input.Height)
            throw new ArgumentException(
                $"frame format {frame.Format} does not match configured input {_input}", nameof(frame));

        // Progressive passthrough — emit the input frame as the sole output. The caller still owns it.
        if (frame.FieldOrder == VideoFieldOrder.Progressive)
        {
            outputs[0] = frame;
            return 1;
        }
        if (outputs.Length < 2)
            throw new ArgumentException("interlaced input requires outputs span with >= 2 slots", nameof(outputs));

        frame.ValidateCpuGeometry();
        var tff = frame.FieldOrder != VideoFieldOrder.BottomFieldFirst; // unknown → assume TFF
        // Emit first field (temporally first), then second field. Each output PTS spaced by half-frame.
        var halfPeriod = _output.FrameRate.Denominator > 0
            ? TimeSpan.FromSeconds((double)_output.FrameRate.Denominator / _output.FrameRate.Numerator)
            : TimeSpan.Zero;
        outputs[0] = BuildField(frame, isTopField: tff, presentationTime: frame.PresentationTime);
        outputs[1] = BuildField(frame, isTopField: !tff, presentationTime: frame.PresentationTime + halfPeriod);
        return 2;
    }

    private VideoFrame BuildField(VideoFrame src, bool isTopField, TimeSpan presentationTime)
    {
        var fmt = src.Format.PixelFormat;
        if (fmt is PixelFormat.Bgra32 or PixelFormat.Rgba32)
            return BuildPackedField(src, isTopField, presentationTime);
        if (fmt is PixelFormat.I420)
            return BuildI420Field(src, isTopField, presentationTime);
        if (fmt is PixelFormat.Nv12)
            return BuildNv12Field(src, isTopField, presentationTime);
        throw new NotSupportedException($"unreachable: {fmt}");
    }

    private static VideoFrame BuildPackedField(VideoFrame src, bool isTopField, TimeSpan pts)
    {
        var w = src.Format.Width;
        var h = src.Format.Height;
        var srcStride = src.Strides[0];
        var srcSpan = src.Planes[0].Span;
        var dstStride = w * 4;
        var byteCount = dstStride * h;
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        var dst = buffer.AsSpan(0, byteCount);

        var fieldOffset = isTopField ? 0 : 1;
        // Field rows: 0, 2, 4 ... (top) or 1, 3, 5 ... (bottom).
        for (var y = 0; y < h; y++)
        {
            int dstRow = y * dstStride;
            // Pick a source row from the same field. For the rows that ARE field rows, copy directly.
            // For the rows in between, average the two neighbouring field rows.
            var inField = (y & 1) == fieldOffset;
            if (inField)
            {
                srcSpan.Slice(y * srcStride, w * 4).CopyTo(dst.Slice(dstRow));
            }
            else
            {
                // Average with neighbour rows of the same field.
                var prev = y - 1;
                var next = y + 1;
                if (prev < 0) prev = next;
                if (next >= h) next = prev;
                var prevSpan = srcSpan.Slice(prev * srcStride, w * 4);
                var nextSpan = srcSpan.Slice(next * srcStride, w * 4);
                for (var x = 0; x < w * 4; x++)
                    dst[dstRow + x] = (byte)((prevSpan[x] + nextSpan[x] + 1) >> 1);
            }
        }

        var owned = buffer;
        return new VideoFrame(pts, src.Format, new ReadOnlyMemory<byte>(buffer, 0, byteCount), dstStride,
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)),
            metadata: src.Metadata with { FieldOrder = VideoFieldOrder.Progressive });
    }

    private static VideoFrame BuildI420Field(VideoFrame src, bool isTopField, TimeSpan pts)
    {
        var w = src.Format.Width;
        var h = src.Format.Height;
        var ySrcStride = src.Strides[0];
        var uSrcStride = src.Strides[1];
        var vSrcStride = src.Strides[2];
        var ySpan = src.Planes[0].Span;
        var uSpan = src.Planes[1].Span;
        var vSpan = src.Planes[2].Span;
        var chromaW = w / 2;
        var chromaH = h / 2;
        var fieldOffset = isTopField ? 0 : 1;

        var yDstStride = w;
        var cDstStride = chromaW;
        var yBytes = yDstStride * h;
        var cBytes = cDstStride * chromaH;
        var totalBytes = yBytes + 2 * cBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(totalBytes);

        var yDst = buffer.AsSpan(0, yBytes);
        var uDst = buffer.AsSpan(yBytes, cBytes);
        var vDst = buffer.AsSpan(yBytes + cBytes, cBytes);

        // Y plane: per-row de-bob on full-res.
        for (var y = 0; y < h; y++)
        {
            var dstRow = y * yDstStride;
            var inField = (y & 1) == fieldOffset;
            if (inField)
            {
                ySpan.Slice(y * ySrcStride, w).CopyTo(yDst.Slice(dstRow));
            }
            else
            {
                var prev = y - 1;
                var next = y + 1;
                if (prev < 0) prev = next;
                if (next >= h) next = prev;
                var p = ySpan.Slice(prev * ySrcStride, w);
                var n = ySpan.Slice(next * ySrcStride, w);
                for (var x = 0; x < w; x++)
                    yDst[dstRow + x] = (byte)((p[x] + n[x] + 1) >> 1);
            }
        }
        // Chroma planes (half-res): treat each chroma row as corresponding to two luma rows. For
        // chroma the "field" granularity is one chroma row = two luma rows; simplest correct copy
        // is to just take every chroma row from the source (chroma is already shared between fields).
        for (var y = 0; y < chromaH; y++)
        {
            uSpan.Slice(y * uSrcStride, chromaW).CopyTo(uDst.Slice(y * cDstStride));
            vSpan.Slice(y * vSrcStride, chromaW).CopyTo(vDst.Slice(y * cDstStride));
        }

        var owned = buffer;
        var planes = new ReadOnlyMemory<byte>[]
        {
            new(buffer, 0, yBytes),
            new(buffer, yBytes, cBytes),
            new(buffer, yBytes + cBytes, cBytes),
        };
        var strides = new[] { yDstStride, cDstStride, cDstStride };
        return new VideoFrame(pts, src.Format, planes, strides,
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)),
            metadata: src.Metadata with { FieldOrder = VideoFieldOrder.Progressive });
    }

    private static VideoFrame BuildNv12Field(VideoFrame src, bool isTopField, TimeSpan pts)
    {
        var w = src.Format.Width;
        var h = src.Format.Height;
        var ySrcStride = src.Strides[0];
        var uvSrcStride = src.Strides[1];
        var ySpan = src.Planes[0].Span;
        var uvSpan = src.Planes[1].Span;
        var chromaH = h / 2;
        var fieldOffset = isTopField ? 0 : 1;

        var yDstStride = w;
        var uvDstStride = w; // NV12 UV is half-height but full-width (2 bytes per chroma sample).
        var yBytes = yDstStride * h;
        var uvBytes = uvDstStride * chromaH;
        var totalBytes = yBytes + uvBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(totalBytes);
        var yDst = buffer.AsSpan(0, yBytes);
        var uvDst = buffer.AsSpan(yBytes, uvBytes);

        for (var y = 0; y < h; y++)
        {
            var dstRow = y * yDstStride;
            var inField = (y & 1) == fieldOffset;
            if (inField)
            {
                ySpan.Slice(y * ySrcStride, w).CopyTo(yDst.Slice(dstRow));
            }
            else
            {
                var prev = y - 1;
                var next = y + 1;
                if (prev < 0) prev = next;
                if (next >= h) next = prev;
                var p = ySpan.Slice(prev * ySrcStride, w);
                var n = ySpan.Slice(next * ySrcStride, w);
                for (var x = 0; x < w; x++)
                    yDst[dstRow + x] = (byte)((p[x] + n[x] + 1) >> 1);
            }
        }
        for (var y = 0; y < chromaH; y++)
            uvSpan.Slice(y * uvSrcStride, w).CopyTo(uvDst.Slice(y * uvDstStride));

        var owned = buffer;
        var planes = new ReadOnlyMemory<byte>[]
        {
            new(buffer, 0, yBytes),
            new(buffer, yBytes, uvBytes),
        };
        var strides = new[] { yDstStride, uvDstStride };
        return new VideoFrame(pts, src.Format, planes, strides,
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)),
            metadata: src.Metadata with { FieldOrder = VideoFieldOrder.Progressive });
    }

    public void Dispose() => _disposed = true;
}
