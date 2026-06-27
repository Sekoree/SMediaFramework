using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpVideoSourceVTable</c> to the managed <see cref="IVideoSource"/>. The resolved
/// format + native pixel formats are queried up front (via the ABI's get_format / native_pixel_formats); each read
/// pulls one <c>MfpVideoFrame</c>, copies its CPU planes into a managed <see cref="VideoFrame"/>, then returns the
/// native frame to the plugin via release_frame. Only the CPU frame kind is marshalled in this slice (GPU-handle
/// kinds are released + skipped). MfpPixelFormat ↔ PixelFormat is an explicit name map — the two enums do not share
/// an ordinal order.
/// </summary>
internal sealed unsafe class NativeVideoSource : IVideoSource, IDisposable
{
    // MfpPixelFormat (index = the ABI ordinal) -> Core PixelFormat. Keep aligned with mfp_plugin.h's MfpPixelFormat.
    private static readonly PixelFormat[] PixelFormats =
    [
        PixelFormat.Unknown, PixelFormat.Bgra32, PixelFormat.Rgba32, PixelFormat.Argb32,
        PixelFormat.Bgr24, PixelFormat.Rgb24, PixelFormat.Rgba16, PixelFormat.Rgba16F,
        PixelFormat.I420, PixelFormat.Yv12, PixelFormat.Nv12, PixelFormat.Nv21,
        PixelFormat.Yuyv, PixelFormat.Uyvy, PixelFormat.Yuv422P, PixelFormat.Yuv444P,
        PixelFormat.Yuv422P10Le, PixelFormat.P010, PixelFormat.P016, PixelFormat.P216, PixelFormat.Pa16,
    ];

    private readonly MfpVideoSourceVTable* _vt;
    private readonly void* _src;
    private readonly VideoFormat _format;
    private readonly PixelFormat[] _native;
    private bool _disposed;

    private NativeVideoSource(MfpVideoSourceVTable* vt, void* src, VideoFormat format, PixelFormat[] native)
    {
        _vt = vt;
        _src = src;
        _format = format;
        _native = native;
    }

    public static NativeVideoSource Create(nint vtable, nint src)
    {
        var vt = (MfpVideoSourceVTable*)vtable;
        var s = (void*)src;

        var native = QueryNativeFormats(vt, s);

        var format = new VideoFormat(0, 0, PixelFormat.Unknown, new Rational(30, 1));
        if (vt->GetFormat != null)
        {
            MfpVideoFormat mf = default;
            if (vt->GetFormat(s, &mf) == (int)MfpStatus.Ok)
                format = ToVideoFormat(in mf);
        }

        return new NativeVideoSource(vt, s, format, native);
    }

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;
    public bool IsExhausted => _disposed || (_vt->IsExhausted != null && _vt->IsExhausted(_src) != 0);

    public void SelectOutputFormat(PixelFormat format)
    {
        if (_vt->SelectOutputFormat == null)
            return;
        var rc = _vt->SelectOutputFormat(_src, FromCore(format));
        if (rc != (int)MfpStatus.Ok)
            throw new InvalidOperationException($"plugin video source rejected output format {format} (status {rc}).");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        frame = null!;
        if (_disposed || _vt->TryReadFrame == null)
            return false;

        MfpVideoFrame mf = default;
        if (_vt->TryReadFrame(_src, &mf) != (int)MfpStatus.Ok)
            return false;

        try
        {
            if (mf.Kind != MfpFrameKind.Cpu)
                return false; // only CPU frames are marshalled in this slice
            frame = CopyCpuFrame(in mf);
            return true;
        }
        finally
        {
            if (_vt->ReleaseFrame != null)
                _vt->ReleaseFrame(_src, &mf);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_src);
    }

    private VideoFrame CopyCpuFrame(in MfpVideoFrame mf)
    {
        var pf = ToCore(mf.PixelFormat);
        var rate = _format.FrameRate.Numerator > 0 ? _format.FrameRate : new Rational(30, 1);
        var format = new VideoFormat((int)mf.Width, (int)mf.Height, pf, rate);

        var cpu = mf.Payload.Cpu;
        var planeCount = Math.Clamp(cpu.PlaneCount, 0, 4);
        var planes = new ReadOnlyMemory<byte>[planeCount];
        var strides = new int[planeCount];
        for (var i = 0; i < planeCount; i++)
        {
            var stride = StrideAt(in cpu, i);
            var rows = PlaneRows(pf, i, (int)mf.Height);
            var bytes = stride > 0 && rows > 0 ? stride * rows : 0;
            var buf = new byte[bytes];
            var srcPtr = PlanePtr(in cpu, i);
            if (srcPtr != null && bytes > 0)
                new ReadOnlySpan<byte>(srcPtr, bytes).CopyTo(buf);
            planes[i] = buf;
            strides[i] = stride;
        }

        return new VideoFrame(TimeSpan.FromTicks(mf.PtsTicks), format, planes, strides);
    }

    private static PixelFormat[] QueryNativeFormats(MfpVideoSourceVTable* vt, void* s)
    {
        if (vt->NativePixelFormats == null)
            return [];
        const int cap = 32;
        var buf = stackalloc int[cap];
        var count = 0;
        if (vt->NativePixelFormats(s, buf, cap, &count) != (int)MfpStatus.Ok || count <= 0)
            return [];
        count = Math.Min(count, cap);
        var result = new PixelFormat[count];
        for (var i = 0; i < count; i++)
            result[i] = ToCore(buf[i]);
        return result;
    }

    private static VideoFormat ToVideoFormat(in MfpVideoFormat mf)
    {
        var rate = mf.FpsNum > 0 && mf.FpsDen > 0
            ? new Rational((int)mf.FpsNum, (int)mf.FpsDen)
            : new Rational(30, 1);
        return new VideoFormat((int)mf.Width, (int)mf.Height, ToCore(mf.PixelFormat), rate);
    }

    private static PixelFormat ToCore(int mfp) =>
        (uint)mfp < (uint)PixelFormats.Length ? PixelFormats[mfp] : PixelFormat.Unknown;

    private static int FromCore(PixelFormat pf)
    {
        var i = Array.IndexOf(PixelFormats, pf);
        return i < 0 ? 0 : i;
    }

    private static int PlaneRows(PixelFormat pf, int planeIndex, int height) => pf switch
    {
        PixelFormat.Nv12 or PixelFormat.Nv21 or PixelFormat.P010 or PixelFormat.P016
            or PixelFormat.I420 or PixelFormat.Yv12 => planeIndex == 0 ? height : (height + 1) / 2,
        _ => height,
    };

    private static void* PlanePtr(in MfpCpuFrame c, int i) =>
        i switch { 0 => c.Plane0, 1 => c.Plane1, 2 => c.Plane2, _ => c.Plane3 };

    private static int StrideAt(in MfpCpuFrame c, int i) =>
        i switch { 0 => c.Stride0, 1 => c.Stride1, 2 => c.Stride2, _ => c.Stride3 };
}
