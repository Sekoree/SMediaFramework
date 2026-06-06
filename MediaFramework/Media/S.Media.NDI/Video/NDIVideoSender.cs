using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.NDI;

namespace S.Media.NDI.Video;

/// <summary>
/// <see cref="IVideoOutput"/> backed by an NDI sender. Constructed only via
/// <see cref="NDIOutput"/> (which owns the underlying <see cref="NDISender"/>
/// so audio + video share one NDI source on the wire).
/// </summary>
/// <remarks>
/// <para>
/// Async path — uses <c>NDIlib_send_send_video_async_v2</c> with a
/// double-buffered unmanaged staging area (<see cref="NativeMemory"/>): the
/// previously-sent buffer stays alive until the next send completes (NDI's
/// contract), so we ping-pong two natively-allocated frames.
/// </para>
/// <para>
/// When <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/> is selected via <see cref="NDIOutput"/>,
/// video and <see cref="NDIAudioOutput"/> share one internal presentation anchor so NDI timecodes
/// match on both streams. Without that wiring (no shared timeline), the sender keeps a private anchor.
/// <see cref="NDIVideoTimecodeMode.MuxerPresentationTicks"/> uses absolute mux PTS ticks on every frame.
/// </para>
/// <para>
/// Pixel-format support (negotiation order — higher chroma first when multiple
/// paths exist): UYVY, BGRA32, RGBA32, NV12, I420. Planar (NV12 / I420)
/// frames are packed into one contiguous staging buffer — Y plane first at
/// the visible-width stride NDI declares via <c>LineStrideInBytes</c>;
/// chroma planes follow at the layout NDI's docs prescribe (UV interleaved
/// at full Y stride for NV12; U-then-V at half Y stride for I420). Contiguous
/// source strides use bulk copies when they match the packed staging layout (NV12 and I420).
/// </para>
/// <para>
/// <strong>Fan-out cost (SDL + NDI):</strong> when <c>VideoRouter</c> fans out negotiated CPU
/// <see cref="PixelFormat.Nv12"/> without a per-branch <c>VideoCpuFrameConverter</c>, branches share one backing via
/// <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/> — the NDI path still packs into ping-pong staging here (one
/// memcpy from that shared view plus SDK upload), but the router no longer deep-copies NV12 for each branch.
/// Contiguous NV12 planes use bulk copies when strides match the packed layout to reduce per-row overhead.
/// </para>
/// <para>
/// <strong>SDK vs host pacing:</strong> when <see cref="NDIOutput"/> is constructed with <c>clockVideo:false</c>,
/// the NDI runtime does not clock video sends; <see cref="PaceBeforePack"/> (optional <c>minimumVideoSubmitSpacing</c>)
/// is then the primary wall-clock throttle. With <c>clockVideo:true</c>, both may apply — see field notes in
/// <c>VideoPlaybackSmoke --ndi-clock-video</c> / <c>--ndi-disable-wall-pace</c>.
/// </para>
/// <para>
/// <see cref="Dispose"/> flushes in-flight async video when needed, then frees each staging slot; failures on individual slots log in
/// <strong>Debug</strong> via <see cref="MediaDiagnostics.LogError"/> while <strong>Release</strong> continues freeing remaining slots.
/// </para>
/// </remarks>
internal sealed unsafe class NDIVideoSender : IVideoOutput, IDisposable
{
    private static readonly PixelFormat[] AcceptedFormats =
    [
        PixelFormat.P216,
        PixelFormat.Pa16,
        PixelFormat.Uyvy,
        PixelFormat.Bgra32,
        PixelFormat.Rgba32,
        PixelFormat.Nv12,
        PixelFormat.I420,
    ];

    private readonly NDISender _sender;
    private VideoFormat _format;
    private bool _configured;
    private bool _disposed;

    // Ping-pong unmanaged staging. _live[idx] is the buffer NDI is currently
    // referencing (after a SendVideoAsync); we write the next frame into the
    // other slot and swap.
    private readonly byte*[] _staging = new byte*[2];
    private readonly int[] _stagingCapacity = new int[2];
    private int _stagingIdx;
    // True after the first SendVideoAsync — we owe a FlushAsync on dispose
    // to release whichever staging buffer is currently in-flight.
    private bool _hasInFlight;
    /// <summary>Wall-clock spacing between submits; zero disables pacing.</summary>
    private readonly TimeSpan _minimumSubmitSpacing;
    private readonly NDIVideoTimecodeMode _timecodeMode;
    private readonly NDIEgressPresentationTimeline? _sharedPresentationTimeline;
    private long _lastSubmitTimestamp;
    private TimeSpan? _presentationAnchor;
    private int _firstSubmitLogged;
    private long _submittedCount;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.NDI.Video.NDIVideoSender");

    public VideoFormat Format
    {
        get
        {
            if (!_configured)
                throw new InvalidOperationException("NDIVideoSender.Configure has not been called yet");
            return _format;
        }
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => AcceptedFormats;

    internal NDIVideoSender(NDISender sender, TimeSpan? minimumSubmitSpacing = null,
                            NDIVideoTimecodeMode timecodeMode = NDIVideoTimecodeMode.Synthesize,
                            NDIEgressPresentationTimeline? sharedPresentationTimeline = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sender = sender;
        var spacing = minimumSubmitSpacing ?? TimeSpan.Zero;
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, TimeSpan.Zero);
        _minimumSubmitSpacing = spacing;
        _timecodeMode = timecodeMode;
        _sharedPresentationTimeline = sharedPresentationTimeline;
    }

    /// <summary>
    /// Clears the presentation-time anchor used by <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/>.
    /// Call after a seek or when starting a new session on the same sender instance.
    /// (No effect for <see cref="NDIVideoTimecodeMode.MuxerPresentationTicks"/>.)
    /// </summary>
    public void ResetPresentationTimecodeAnchor()
    {
        if (_sharedPresentationTimeline is null)
            _presentationAnchor = null;
    }

    private void PaceBeforePack()
    {
        if (_minimumSubmitSpacing <= TimeSpan.Zero)
            return;

        var now = Stopwatch.GetTimestamp();
        if (_lastSubmitTimestamp != 0)
        {
            var since = Stopwatch.GetElapsedTime(_lastSubmitTimestamp, now);
            var wait = _minimumSubmitSpacing - since;
            if (wait > TimeSpan.Zero)
            {
                var deadlineTicks = now + (long)(wait.TotalSeconds * Stopwatch.Frequency);
                // Thread.Sleep resolves whole milliseconds (~±0.5 ms); coarse sleep then sleep remainder
                // (avoid busy SpinWait on the async video pump thread).
                var coarseMs = (int)Math.Truncate(wait.TotalMilliseconds);
                if (coarseMs >= 2)
                    Thread.Sleep(coarseMs - 1);

                var t = Stopwatch.GetTimestamp();
                if (t < deadlineTicks)
                {
                    var remainder = Stopwatch.GetElapsedTime(t, deadlineTicks);
                    if (remainder > TimeSpan.Zero)
                        Thread.Sleep(remainder);
                }

                now = Stopwatch.GetTimestamp();
            }
        }

        _lastSubmitTimestamp = now;
    }

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (format.Width <= 0 || format.Height <= 0)
            throw new ArgumentException("video format must have positive dimensions", nameof(format));
        if (Array.IndexOf(AcceptedFormats, format.PixelFormat) < 0)
            throw new NotSupportedException(
                $"NDIVideoSender does not accept pixel format {format.PixelFormat}; supported: {string.Join(", ", AcceptedFormats)}");
        // Planar 4:2:0 formats need even dimensions so chroma grids align.
        if ((format.PixelFormat is PixelFormat.I420 or PixelFormat.Nv12 or PixelFormat.P216 or PixelFormat.Pa16)
            && (format.Width % 2 != 0 || format.Height % 2 != 0))
            throw new ArgumentException(
                $"{format.PixelFormat} requires even width/height (got {format.Width}x{format.Height})",
                nameof(format));

        // Packed staging assumes an even stride in bytes (RGBA/BGRA/UYVY use full width × Bpp).
        if ((format.PixelFormat is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Uyvy)
            && format.Width % 2 != 0)
            throw new ArgumentException(
                $"{format.PixelFormat} requires even width so row packing matches NDI line stride ({format.Width})",
                nameof(format));

        _format = format;
        _sharedPresentationTimeline?.Reset();
        _presentationAnchor = null;
        _configured = true;
        Interlocked.Exchange(ref _firstSubmitLogged, 0);
        Trace.LogDebug("Configure: {Format} timecodeMode={Mode} spacing={Spacing}ms",
            format, _timecodeMode, _minimumSubmitSpacing.TotalMilliseconds);
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_configured)
                throw new InvalidOperationException("NDIVideoSender.Submit called before Configure");
            if (frame.Format.Width != _format.Width || frame.Format.Height != _format.Height
                || frame.Format.PixelFormat != _format.PixelFormat)
                throw new ArgumentException(
                    $"frame format {frame.Format} does not match sender format {_format}", nameof(frame));

            var expectedPlanes = PixelFormatInfo.PlaneCount(_format.PixelFormat);
            if (frame.PlaneCount != expectedPlanes)
                throw new ArgumentException(
                    $"frame has {frame.PlaneCount} planes; {_format.PixelFormat} requires {expectedPlanes}",
                    nameof(frame));

            if (frame.DmabufNv12 is not null || frame.DmabufP010 is not null || frame.DmabufP016 is not null || frame.Win32Nv12 is not null)
                throw new NotSupportedException(
                    "NDIVideoSender requires CPU-backed pixel planes; GPU NV12/P010/P016 (Linux dma-buf or Windows D3D11 shared) has no readable bytes here. Use software decode or a CPU VideoFrame path.");

            PaceBeforePack();

            var totalBytes = StagingBytes(_format);
            EnsureStagingCapacity(_stagingIdx, totalBytes);
            var dstBase = _staging[_stagingIdx];

            PackInto(frame, dstBase);

            var (timecode, timestamp) = BuildTimecode(frame);

            var native = new NDIVideoFrameV2
            {
                Xres = _format.Width,
                Yres = _format.Height,
                FourCC = ToFourCC(_format.PixelFormat),
                FrameRateN = _format.FrameRate.Numerator,
                FrameRateD = _format.FrameRate.Denominator <= 0 ? 1 : _format.FrameRate.Denominator,
                PictureAspectRatio = 0f, // 0 = derive from Xres/Yres (square pixels)
                FrameFormatType = MapFieldOrder(frame.FieldOrder),
                Timecode = timecode,
                PData = (nint)dstBase,
                LineStrideInBytes = LineStrideForFormat(_format),
                PMetadata = nint.Zero,
                Timestamp = timestamp,
            };
            _sender.SendVideoAsync(native);
            _hasInFlight = true;
            _stagingIdx ^= 1;
            var n = Interlocked.Increment(ref _submittedCount);
            if (Interlocked.Exchange(ref _firstSubmitLogged, 1) == 0)
                Trace.LogDebug("First Submit: format={Format} pts={Pts} tc={TC}",
                    _format, frame.PresentationTime, timecode);
            else if (Trace.IsEnabled(LogLevel.Trace) && n % 300 == 0)
                Trace.LogTrace("Submit: #{N} pts={Pts}", n, frame.PresentationTime);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private (long timecode, long timestamp) BuildTimecode(VideoFrame frame)
    {
        switch (_timecodeMode)
        {
            case NDIVideoTimecodeMode.Synthesize:
                return (NDIConstants.TimecodeSynthesize, 0);
            case NDIVideoTimecodeMode.PresentationRelativeTicks:
                return BuildPresentationRelativeTimecode(frame);
            case NDIVideoTimecodeMode.MuxerPresentationTicks:
            {
                var t = frame.PresentationTime.Ticks;
                return (t < 0 ? 0L : t, NDIConstants.TimestampUndefined);
            }
            case NDIVideoTimecodeMode.SmpteFromFrame:
                if (frame.Timecode is { } tc)
                {
                    var ticks = tc.ToTicksAtRate();
                    return (ticks < 0 ? 0L : ticks, NDIConstants.TimestampUndefined);
                }
                // No SMPTE attached — fall back to presentation-relative ticks so downstream still has a sync source.
                return BuildPresentationRelativeTimecode(frame);
            default:
                throw new InvalidOperationException($"unknown {nameof(NDIVideoTimecodeMode)} {_timecodeMode}");
        }
    }

    private (long timecode, long timestamp) BuildPresentationRelativeTimecode(VideoFrame frame)
    {
        if (_sharedPresentationTimeline is not null)
        {
            var tc = _sharedPresentationTimeline.TimecodeFromPresentationTime(frame.PresentationTime);
            return (tc, NDIConstants.TimestampUndefined);
        }

        if (_presentationAnchor is null)
            _presentationAnchor = frame.PresentationTime;

        var anchor = _presentationAnchor.Value;
        var delta = frame.PresentationTime - anchor;
        if (delta < TimeSpan.FromSeconds(-1))
        {
            _presentationAnchor = frame.PresentationTime;
            delta = TimeSpan.Zero;
        }

        var tcLocal = delta < TimeSpan.Zero ? 0L : delta.Ticks;
        return (tcLocal, NDIConstants.TimestampUndefined);
    }

    private static NDIFrameFormatType MapFieldOrder(VideoFieldOrder order) => order switch
    {
        VideoFieldOrder.Progressive => NDIFrameFormatType.Progressive,
        VideoFieldOrder.TopFieldFirst or VideoFieldOrder.BottomFieldFirst or VideoFieldOrder.Interlaced
            => NDIFrameFormatType.Interleaved,
        _ => NDIFrameFormatType.Progressive,
    };

    private void PackInto(VideoFrame frame, byte* dst)
    {
        switch (_format.PixelFormat)
        {
            case PixelFormat.Rgba32:
            case PixelFormat.Bgra32:
            case PixelFormat.Uyvy:
                PackPacked(frame, dst);
                break;
            case PixelFormat.Nv12:
                PackNv12(frame, dst);
                break;
            case PixelFormat.I420:
                PackI420(frame, dst);
                break;
            case PixelFormat.P216:
                PackP216(frame, dst);
                break;
            case PixelFormat.Pa16:
                PackPa16(frame, dst);
                break;
            default:
                throw new NotSupportedException($"PackInto: {_format.PixelFormat}");
        }
    }

    private void PackPacked(VideoFrame frame, byte* dst)
    {
        var srcPlane = frame.Planes[0].Span;
        var srcStride = frame.Strides[0];
        var visibleStride = _format.Width * BytesPerPackedPixel(_format.PixelFormat);
        var contiguous = visibleStride * _format.Height;
        if (srcStride == visibleStride && srcPlane.Length >= contiguous)
        {
            srcPlane.Slice(0, contiguous).CopyTo(new Span<byte>(dst, contiguous));
            return;
        }

        for (var row = 0; row < _format.Height; row++)
        {
            var srcRow = srcPlane.Slice(row * srcStride, visibleStride);
            srcRow.CopyTo(new Span<byte>(dst + row * visibleStride, visibleStride));
        }
    }

    private void PackNv12(VideoFrame frame, byte* dst)
    {
        // NDI: Y plane at LineStrideInBytes (=Width); UV interleaved plane
        // immediately after, also at LineStrideInBytes (Yres/2 rows × Width
        // bytes — Width/2 chroma pairs × 2 bytes per pair).
        var width = _format.Width;
        var height = _format.Height;
        var ySrc = frame.Planes[0].Span;
        var ySrcStride = frame.Strides[0];
        var yBytes = width * height;
        if (ySrcStride == width && ySrc.Length >= yBytes)
            ySrc.Slice(0, yBytes).CopyTo(new Span<byte>(dst, yBytes));
        else
        {
            for (var row = 0; row < height; row++)
            {
                var srcRow = ySrc.Slice(row * ySrcStride, width);
                srcRow.CopyTo(new Span<byte>(dst + row * width, width));
            }
        }

        var uvDstBase = dst + yBytes;
        var uvSrc = frame.Planes[1].Span;
        var uvSrcStride = frame.Strides[1];
        var uvRows = height / 2;
        var uvBytes = width * uvRows;
        if (uvSrcStride == width && uvSrc.Length >= uvBytes)
            uvSrc.Slice(0, uvBytes).CopyTo(new Span<byte>(uvDstBase, uvBytes));
        else
        {
            for (var row = 0; row < uvRows; row++)
            {
                var srcRow = uvSrc.Slice(row * uvSrcStride, width);
                srcRow.CopyTo(new Span<byte>(uvDstBase + row * width, width));
            }
        }
    }

    private void PackP216(VideoFrame frame, byte* dst)
    {
        var width = _format.Width;
        var height = _format.Height;
        var yStride = frame.Strides[0];
        var uvStride = frame.Strides[1];
        var lineStride = width * sizeof(ushort);

        if (yStride < lineStride || uvStride < lineStride)
            throw new ArgumentException("P216 frame strides must be at least width*2 bytes per row.");

        var ySrc = frame.Planes[0].Span;
        var yBytes = lineStride * height;
        CopyPlaneRows(ySrc, yStride, dst, lineStride, lineStride, height);

        var uvDstBase = dst + yBytes;
        var uvSrc = frame.Planes[1].Span;
        CopyPlaneRows(uvSrc, uvStride, uvDstBase, lineStride, lineStride, height);
    }

    private void PackPa16(VideoFrame frame, byte* dst)
    {
        PackP216(frame, dst);
        var width = _format.Width;
        var height = _format.Height;
        var lineStride = width * sizeof(ushort);
        var yBytes = lineStride * height;
        var uvBytes = lineStride * height;
        var aDstBase = dst + yBytes + uvBytes;
        var aStride = frame.Strides[2];
        if (aStride < lineStride)
            throw new ArgumentException("PA16 alpha stride must be at least width*2 bytes per row.");
        CopyPlaneRows(frame.Planes[2].Span, aStride, aDstBase, lineStride, lineStride, height);
    }

    private static void CopyPlaneRows(ReadOnlySpan<byte> src, int srcStride, byte* dst, int dstStride, int rowBytes, int rows)
    {
        if (srcStride == rowBytes)
        {
            var total = rowBytes * rows;
            if (src.Length >= total)
            {
                src.Slice(0, total).CopyTo(new Span<byte>(dst, total));
                return;
            }
        }

        for (var row = 0; row < rows; row++)
            src.Slice(row * srcStride, rowBytes).CopyTo(new Span<byte>(dst + row * dstStride, rowBytes));
    }

    private void PackI420(VideoFrame frame, byte* dst)
    {
        var width = _format.Width;
        var height = _format.Height;
        var chromaW = PixelFormatInfo.ChromaWidth420(width);
        var chromaH = PixelFormatInfo.ChromaHeight420(height);

        var ySrc = frame.Planes[0].Span;
        var ySrcStride = frame.Strides[0];
        var yBytes = width * height;
        if (ySrcStride == width && ySrc.Length >= yBytes)
            ySrc.Slice(0, yBytes).CopyTo(new Span<byte>(dst, yBytes));
        else
        {
            for (var row = 0; row < height; row++)
            {
                ySrc.Slice(row * ySrcStride, width)
                    .CopyTo(new Span<byte>(dst + row * width, width));
            }
        }

        var uDstBase = dst + yBytes;
        var uSrc = frame.Planes[1].Span;
        var uSrcStride = frame.Strides[1];
        var uPackedRowBytes = PixelFormatInfo.PlaneByteWidth(PixelFormat.I420, width, 1);
        var uPlaneByteCount = uPackedRowBytes * chromaH;
        if (uSrcStride == chromaW && uSrc.Length >= chromaW * chromaH && uPackedRowBytes == chromaW)
            uSrc.Slice(0, chromaW * chromaH).CopyTo(new Span<byte>(uDstBase, uPlaneByteCount));
        else
        {
            for (var row = 0; row < chromaH; row++)
            {
                uSrc.Slice(row * uSrcStride, chromaW)
                    .CopyTo(new Span<byte>(uDstBase + row * uPackedRowBytes, chromaW));
            }
        }

        var vDstBase = uDstBase + uPlaneByteCount;
        var vSrc = frame.Planes[2].Span;
        var vSrcStride = frame.Strides[2];
        if (vSrcStride == chromaW && vSrc.Length >= chromaW * chromaH && uPackedRowBytes == chromaW)
            vSrc.Slice(0, chromaW * chromaH).CopyTo(new Span<byte>(vDstBase, uPlaneByteCount));
        else
        {
            for (var row = 0; row < chromaH; row++)
            {
                vSrc.Slice(row * vSrcStride, chromaW)
                    .CopyTo(new Span<byte>(vDstBase + row * uPackedRowBytes, chromaW));
            }
        }
    }

    private void EnsureStagingCapacity(int slot, int neededBytes)
    {
        if (_stagingCapacity[slot] >= neededBytes) return;
        if (_staging[slot] != null) NativeMemory.Free(_staging[slot]);
        // Round to a power of two — re-allocations on slowly growing frames
        // are rare, but we don't want every couple of frames to bounce.
        var capacity = 1;
        while (capacity < neededBytes) capacity <<= 1;
        _staging[slot] = (byte*)NativeMemory.Alloc((nuint)capacity);
        _stagingCapacity[slot] = capacity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Flush any in-flight async send so NDI releases its reference to our
        // staging buffer before we free it.
        if (_hasInFlight)
        {
            MediaDiagnostics.SwallowDisposeErrors(_sender.SynchronizeAsyncVideo, "NDIVideoSender.Dispose: SynchronizeAsyncVideo");
            _hasInFlight = false;
        }
        for (var i = 0; i < _staging.Length; i++)
        {
            if (_staging[i] == null) continue;
            MediaDiagnostics.SwallowDisposeErrors(() => NativeMemory.Free(_staging[i]), $"NDIVideoSender.Dispose: staging[{i}]");
            _staging[i] = null;
            _stagingCapacity[i] = 0;
        }
    }

    private static int StagingBytes(VideoFormat fmt) => fmt.PixelFormat switch
    {
        PixelFormat.Rgba32 => fmt.Width * fmt.Height * 4,
        PixelFormat.Bgra32 => fmt.Width * fmt.Height * 4,
        PixelFormat.Uyvy   => fmt.Width * fmt.Height * 2,
        PixelFormat.Nv12   => fmt.Width * fmt.Height * 3 / 2,
        PixelFormat.I420   => fmt.Width * fmt.Height * 3 / 2,
        PixelFormat.P216   => fmt.Width * fmt.Height * 4,
        PixelFormat.Pa16   => fmt.Width * fmt.Height * 6,
        _ => throw new NotSupportedException($"StagingBytes: {fmt.PixelFormat}"),
    };

    private static int LineStrideForFormat(VideoFormat fmt) => fmt.PixelFormat switch
    {
        PixelFormat.Rgba32 => fmt.Width * 4,
        PixelFormat.Bgra32 => fmt.Width * 4,
        PixelFormat.Uyvy   => fmt.Width * 2,
        // For planar formats LineStrideInBytes is the Y plane's stride
        // (and equals the visible width in our tightly-packed layout).
        PixelFormat.Nv12   => fmt.Width,
        PixelFormat.I420   => fmt.Width,
        PixelFormat.P216 or PixelFormat.Pa16 => fmt.Width * sizeof(ushort),
        _ => throw new NotSupportedException($"LineStrideForFormat: {fmt.PixelFormat}"),
    };

    private static int BytesPerPackedPixel(PixelFormat format) => format switch
    {
        PixelFormat.Rgba32 => 4,
        PixelFormat.Bgra32 => 4,
        PixelFormat.Uyvy   => 2,
        _ => throw new NotSupportedException($"BytesPerPackedPixel: {format}"),
    };

    private static NDIFourCCVideoType ToFourCC(PixelFormat format) => format switch
    {
        PixelFormat.Rgba32 => NDIFourCCVideoType.Rgba,
        PixelFormat.Bgra32 => NDIFourCCVideoType.Bgra,
        PixelFormat.Uyvy   => NDIFourCCVideoType.Uyvy,
        PixelFormat.Nv12   => NDIFourCCVideoType.Nv12,
        PixelFormat.I420   => NDIFourCCVideoType.I420,
        PixelFormat.P216   => NDIFourCCVideoType.P216,
        PixelFormat.Pa16   => NDIFourCCVideoType.Pa16,
        _ => throw new NotSupportedException($"ToFourCC: {format}"),
    };
}
