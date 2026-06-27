using System.Buffers;
using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Shared CPU-frame + pixel-format marshalling between the ABI's <c>MfpVideoFrame</c> and Core's
/// <see cref="VideoFrame"/> (used by the video source/output/subtitle adapters). MfpPixelFormat ↔ PixelFormat is an
/// explicit name table — the two enums do not share an ordinal order. Only the CPU frame kind is marshalled; GPU
/// kinds are the layer-surface/zero-copy path (not handled here).
/// </summary>
internal static unsafe class AbiFrameMarshal
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

    public static PixelFormat ToCore(int mfp) =>
        (uint)mfp < (uint)PixelFormats.Length ? PixelFormats[mfp] : PixelFormat.Unknown;

    public static int FromCore(PixelFormat pf)
    {
        var i = Array.IndexOf(PixelFormats, pf);
        return i < 0 ? 0 : i;
    }

    public static int PlaneRows(PixelFormat pf, int planeIndex, int height) => pf switch
    {
        PixelFormat.Nv12 or PixelFormat.Nv21 or PixelFormat.P010 or PixelFormat.P016
            or PixelFormat.I420 or PixelFormat.Yv12 => planeIndex == 0 ? height : (height + 1) / 2,
        _ => height,
    };

    /// <summary>Copies an ABI CPU frame into a managed <see cref="VideoFrame"/> (planes copied; the caller releases
    /// the native frame via release_frame afterwards).</summary>
    public static VideoFrame ToManagedCpuFrame(in MfpVideoFrame mf, Rational fallbackRate)
    {
        var pf = ToCore(mf.PixelFormat);
        var rate = fallbackRate.Numerator > 0 ? fallbackRate : new Rational(30, 1);
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

    /// <summary>Views a managed <see cref="VideoFrame"/>'s planes as an ABI CPU frame in place. The returned handles
    /// pin the planes for the duration of a synchronous call (e.g. output submit) and MUST be disposed afterwards.</summary>
    public static MemoryHandle[] ToNativeCpuFrame(VideoFrame frame, out MfpVideoFrame mf)
    {
        mf = default;
        mf.Kind = MfpFrameKind.Cpu;
        mf.Width = (uint)frame.Format.Width;
        mf.Height = (uint)frame.Format.Height;
        mf.PixelFormat = FromCore(frame.Format.PixelFormat);
        mf.PtsTicks = frame.PresentationTime.Ticks;

        var planes = frame.Planes;
        var strides = frame.Strides;
        var count = Math.Min(Math.Min(planes.Length, strides.Length), 4);
        var handles = new MemoryHandle[count];
        for (var i = 0; i < count; i++)
        {
            handles[i] = planes[i].Pin();
            SetPlane(ref mf.Payload.Cpu, i, handles[i].Pointer, strides[i]);
        }
        mf.Payload.Cpu.PlaneCount = count;
        return handles;
    }

    private static void* PlanePtr(in MfpCpuFrame c, int i) =>
        i switch { 0 => c.Plane0, 1 => c.Plane1, 2 => c.Plane2, _ => c.Plane3 };

    private static int StrideAt(in MfpCpuFrame c, int i) =>
        i switch { 0 => c.Stride0, 1 => c.Stride1, 2 => c.Stride2, _ => c.Stride3 };

    private static void SetPlane(ref MfpCpuFrame c, int i, void* ptr, int stride)
    {
        switch (i)
        {
            case 0: c.Plane0 = ptr; c.Stride0 = stride; break;
            case 1: c.Plane1 = ptr; c.Stride1 = stride; break;
            case 2: c.Plane2 = ptr; c.Stride2 = stride; break;
            default: c.Plane3 = ptr; c.Stride3 = stride; break;
        }
    }
}
