using System.Diagnostics;
using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Video;

namespace S.Media.NDI.Video;

/// <summary>
/// <see cref="IVideoSink"/> backed by an NDI sender. Constructed only via
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
/// Pixel-format support: BGRA32, UYVY, NV12, I420. Planar (NV12 / I420)
/// frames are packed into one contiguous staging buffer — Y plane first at
/// the visible-width stride NDI declares via <c>LineStrideInBytes</c>;
/// chroma planes follow at the layout NDI's docs prescribe (UV interleaved
/// at full Y stride for NV12; U-then-V at half Y stride for I420).
/// </para>
/// </remarks>
public sealed unsafe class NDIVideoSender : IVideoSink, IDisposable
{
    private static readonly PixelFormat[] AcceptedFormats =
    [
        PixelFormat.Bgra32,
        PixelFormat.Uyvy,
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
    private long _lastSubmitTimestamp;

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

    internal NDIVideoSender(NDISender sender, TimeSpan? minimumSubmitSpacing = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sender = sender;
        var spacing = minimumSubmitSpacing ?? TimeSpan.Zero;
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, TimeSpan.Zero);
        _minimumSubmitSpacing = spacing;
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
                // Thread.Sleep resolves whole milliseconds (~±0.5 ms); coarse sleep then SpinWait remainder.
                var coarseMs = (int)Math.Truncate(wait.TotalMilliseconds);
                if (coarseMs >= 2)
                    Thread.Sleep(coarseMs - 1);

                while (Stopwatch.GetTimestamp() < deadlineTicks)
                    Thread.SpinWait(32);

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
        if ((format.PixelFormat == PixelFormat.I420 || format.PixelFormat == PixelFormat.Nv12)
            && (format.Width % 2 != 0 || format.Height % 2 != 0))
            throw new ArgumentException(
                $"{format.PixelFormat} requires even width/height (got {format.Width}x{format.Height})",
                nameof(format));

        // Packed staging assumes an even stride in bytes (BGRA/UYVY use full width × Bpp).
        if ((format.PixelFormat == PixelFormat.Bgra32 || format.PixelFormat == PixelFormat.Uyvy)
            && format.Width % 2 != 0)
            throw new ArgumentException(
                $"{format.PixelFormat} requires even width so row packing matches NDI line stride ({format.Width})",
                nameof(format));

        _format = format;
        _configured = true;
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

            PaceBeforePack();

            var totalBytes = StagingBytes(_format);
            EnsureStagingCapacity(_stagingIdx, totalBytes);
            var dstBase = _staging[_stagingIdx];

            PackInto(frame, dstBase);

            var native = new NDIVideoFrameV2
            {
                Xres = _format.Width,
                Yres = _format.Height,
                FourCC = ToFourCC(_format.PixelFormat),
                FrameRateN = _format.FrameRate.Numerator,
                FrameRateD = _format.FrameRate.Denominator <= 0 ? 1 : _format.FrameRate.Denominator,
                PictureAspectRatio = 0f, // 0 = derive from Xres/Yres (square pixels)
                FrameFormatType = NDIFrameFormatType.Progressive,
                Timecode = 0x7FFFFFFFFFFFFFFF, // synthesise: sender clocks itself
                PData = (nint)dstBase,
                LineStrideInBytes = LineStrideForFormat(_format),
                PMetadata = nint.Zero,
                Timestamp = 0,
            };
            _sender.SendVideoAsync(native);
            _hasInFlight = true;
            _stagingIdx ^= 1;
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void PackInto(VideoFrame frame, byte* dst)
    {
        switch (_format.PixelFormat)
        {
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
        for (var row = 0; row < height; row++)
        {
            var srcRow = ySrc.Slice(row * ySrcStride, width);
            srcRow.CopyTo(new Span<byte>(dst + row * width, width));
        }

        var uvDstBase = dst + width * height;
        var uvSrc = frame.Planes[1].Span;
        var uvSrcStride = frame.Strides[1];
        var uvRows = height / 2;
        for (var row = 0; row < uvRows; row++)
        {
            var srcRow = uvSrc.Slice(row * uvSrcStride, width);
            srcRow.CopyTo(new Span<byte>(uvDstBase + row * width, width));
        }
    }

    private void PackI420(VideoFrame frame, byte* dst)
    {
        var width = _format.Width;
        var height = _format.Height;
        var chromaW = PixelFormatInfo.ChromaWidth420(width);
        var chromaH = PixelFormatInfo.ChromaHeight420(height);

        var ySrc = frame.Planes[0].Span;
        var ySrcStride = frame.Strides[0];
        for (var row = 0; row < height; row++)
        {
            ySrc.Slice(row * ySrcStride, width)
                .CopyTo(new Span<byte>(dst + row * width, width));
        }

        var uDstBase = dst + width * height;
        var uSrc = frame.Planes[1].Span;
        var uSrcStride = frame.Strides[1];
        for (var row = 0; row < chromaH; row++)
        {
            uSrc.Slice(row * uSrcStride, chromaW)
                .CopyTo(new Span<byte>(uDstBase + row * chromaW, chromaW));
        }

        // V begins after the contiguous U bitmap (handles future strides that differ from half-visible width).
        var uPackedRowBytes = PixelFormatInfo.PlaneByteWidth(PixelFormat.I420, width, 1);
        var uPlaneByteCount = uPackedRowBytes * chromaH;
        var vDstBase = uDstBase + uPlaneByteCount;
        var vSrc = frame.Planes[2].Span;
        var vSrcStride = frame.Strides[2];
        for (var row = 0; row < chromaH; row++)
        {
            vSrc.Slice(row * vSrcStride, chromaW)
                .CopyTo(new Span<byte>(vDstBase + row * chromaW, chromaW));
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
            try { _sender.FlushAsync(); } catch { /* best effort */ }
            _hasInFlight = false;
        }
        for (var i = 0; i < _staging.Length; i++)
        {
            if (_staging[i] == null) continue;
            NativeMemory.Free(_staging[i]);
            _staging[i] = null;
            _stagingCapacity[i] = 0;
        }
    }

    private static int StagingBytes(VideoFormat fmt) => fmt.PixelFormat switch
    {
        PixelFormat.Bgra32 => fmt.Width * fmt.Height * 4,
        PixelFormat.Uyvy   => fmt.Width * fmt.Height * 2,
        PixelFormat.Nv12   => fmt.Width * fmt.Height * 3 / 2,
        PixelFormat.I420   => fmt.Width * fmt.Height * 3 / 2,
        _ => throw new NotSupportedException($"StagingBytes: {fmt.PixelFormat}"),
    };

    private static int LineStrideForFormat(VideoFormat fmt) => fmt.PixelFormat switch
    {
        PixelFormat.Bgra32 => fmt.Width * 4,
        PixelFormat.Uyvy   => fmt.Width * 2,
        // For planar formats LineStrideInBytes is the Y plane's stride
        // (and equals the visible width in our tightly-packed layout).
        PixelFormat.Nv12   => fmt.Width,
        PixelFormat.I420   => fmt.Width,
        _ => throw new NotSupportedException($"LineStrideForFormat: {fmt.PixelFormat}"),
    };

    private static int BytesPerPackedPixel(PixelFormat format) => format switch
    {
        PixelFormat.Bgra32 => 4,
        PixelFormat.Uyvy   => 2,
        _ => throw new NotSupportedException($"BytesPerPackedPixel: {format}"),
    };

    private static NDIFourCCVideoType ToFourCC(PixelFormat format) => format switch
    {
        PixelFormat.Bgra32 => NDIFourCCVideoType.Bgra,
        PixelFormat.Uyvy   => NDIFourCCVideoType.Uyvy,
        PixelFormat.Nv12   => NDIFourCCVideoType.Nv12,
        PixelFormat.I420   => NDIFourCCVideoType.I420,
        _ => throw new NotSupportedException($"ToFourCC: {format}"),
    };
}
