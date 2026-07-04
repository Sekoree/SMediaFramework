using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Shared CPU-frame + pixel-format marshalling between the ABI's <c>MfpVideoFrame</c> and Core's
/// <see cref="VideoFrame"/> (used by the video source/output/subtitle adapters). MfpPixelFormat ↔ PixelFormat is an
/// explicit name table — the two enums do not share an ordinal order. Linux dma-buf and Windows D3D11 shared
/// handles are duplicated into Core-owned hardware backings; GL texture frames remain context-local to layer surfaces.
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

    public static VideoFrame ToManagedFrame(in MfpVideoFrame mf, Rational fallbackRate) => mf.Kind switch
    {
        MfpFrameKind.Cpu => ToManagedCpuFrame(in mf, fallbackRate),
        MfpFrameKind.DmaBuf => ToManagedDmaBufFrame(in mf, fallbackRate),
        MfpFrameKind.D3D11 => ToManagedD3D11Frame(in mf, fallbackRate),
        MfpFrameKind.GlTexture => throw new NotSupportedException(
            "ABI GL-texture frames are valid only inside the matching plugin layer-surface GL context."),
        _ => throw new NotSupportedException($"Unknown ABI frame kind {(int)mf.Kind}."),
    };

    /// <summary>Views a managed <see cref="VideoFrame"/>'s planes as an ABI CPU frame in place. The returned handles
    /// pin the planes for the duration of a synchronous call (e.g. output submit) and MUST be disposed afterwards.</summary>
    public static MemoryHandle[] ToNativeFrame(VideoFrame frame, out MfpVideoFrame mf)
    {
        mf = default;
        mf.Width = (uint)frame.Format.Width;
        mf.Height = (uint)frame.Format.Height;
        mf.PixelFormat = FromCore(frame.Format.PixelFormat);
        mf.PtsTicks = frame.PresentationTime.Ticks;

        if (frame.DmabufNv12 is { } nv12)
        {
            SetDmaBuf(ref mf, nv12.YPlaneFd, nv12.UvPlaneFd,
                nv12.YPlaneOffsetBytes, nv12.UvPlaneOffsetBytes,
                nv12.YPlanePitchBytes, nv12.UvPlanePitchBytes,
                nv12.YPlaneDrmFormatModifier, nv12.UvPlaneDrmFormatModifier);
            return [];
        }
        if (frame.DmabufP010 is { } p010)
        {
            SetDmaBuf(ref mf, p010.YPlaneFd, p010.UvPlaneFd,
                p010.YPlaneOffsetBytes, p010.UvPlaneOffsetBytes,
                p010.YPlanePitchBytes, p010.UvPlanePitchBytes,
                p010.YPlaneDrmFormatModifier, p010.UvPlaneDrmFormatModifier);
            return [];
        }
        if (frame.DmabufP016 is { } p016)
        {
            SetDmaBuf(ref mf, p016.YPlaneFd, p016.UvPlaneFd,
                p016.YPlaneOffsetBytes, p016.UvPlaneOffsetBytes,
                p016.YPlanePitchBytes, p016.UvPlanePitchBytes,
                p016.YPlaneDrmFormatModifier, p016.UvPlaneDrmFormatModifier);
            return [];
        }
        if (frame.Win32Nv12 is { } win32)
        {
            mf.Kind = MfpFrameKind.D3D11;
            mf.Payload.D3D11 = new MfpD3D11Frame
            {
                LumaNtSharedHandle = (ulong)win32.LumaSharedNtHandle,
                ChromaNtSharedHandle = (ulong)win32.ChromaSharedNtHandle,
                DxgiFormat = 103, // DXGI_FORMAT_NV12
                ArraySlice = (uint)win32.D3D11TextureArraySliceIndex,
                YStride = win32.YPlanePitchBytes,
                UvStride = win32.UvPlanePitchBytes,
            };
            return [];
        }

        mf.Kind = MfpFrameKind.Cpu;

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

    private static VideoFrame ToManagedDmaBufFrame(in MfpVideoFrame mf, Rational fallbackRate)
    {
        EnsureNoExplicitSync(in mf);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("ABI dma-buf frames can only be imported on Linux.");

        var native = mf.Payload.DmaBuf;
        if (native.PlaneCount < 2 || native.Fds[0] < 0 || native.Fds[1] < 0
            || native.Strides[0] <= 0 || native.Strides[1] <= 0)
            throw new InvalidOperationException("ABI dma-buf frame must provide two valid planes.");

        var yFd = libc_dup(native.Fds[0]);
        if (yFd < 0)
            throw new InvalidOperationException("dup() failed for the ABI dma-buf luma plane.");
        var uvFd = libc_dup(native.Fds[1]);
        if (uvFd < 0)
        {
            _ = libc_close(yFd);
            throw new InvalidOperationException("dup() failed for the ABI dma-buf chroma plane.");
        }

        var format = FormatOf(in mf, fallbackRate);
        try
        {
            VideoFrameHardwareBacking backing = format.PixelFormat switch
            {
                PixelFormat.Nv12 => new DmabufNv12Backing(
                    yFd, native.Offsets[0], native.Strides[0],
                    uvFd, native.Offsets[1], native.Strides[1],
                    native.Modifiers[0], native.Modifiers[1]),
                PixelFormat.P010 => new DmabufP010Backing(
                    yFd, native.Offsets[0], native.Strides[0],
                    uvFd, native.Offsets[1], native.Strides[1],
                    native.Modifiers[0], native.Modifiers[1]),
                PixelFormat.P016 => new DmabufP016Backing(
                    yFd, native.Offsets[0], native.Strides[0],
                    uvFd, native.Offsets[1], native.Strides[1],
                    native.Modifiers[0], native.Modifiers[1]),
                _ => throw new NotSupportedException(
                    $"ABI dma-buf import supports NV12/P010/P016, not {format.PixelFormat}."),
            };
            return backing switch
            {
                DmabufNv12Backing b => VideoFrame.CreateNv12Dmabuf(TimeSpan.FromTicks(mf.PtsTicks), format, b),
                DmabufP010Backing b => VideoFrame.CreateP010Dmabuf(TimeSpan.FromTicks(mf.PtsTicks), format, b),
                DmabufP016Backing b => VideoFrame.CreateP016Dmabuf(TimeSpan.FromTicks(mf.PtsTicks), format, b),
                _ => throw new UnreachableException(),
            };
        }
        catch
        {
            _ = libc_close(yFd);
            _ = libc_close(uvFd);
            throw;
        }
    }

    private static VideoFrame ToManagedD3D11Frame(in MfpVideoFrame mf, Rational fallbackRate)
    {
        EnsureNoExplicitSync(in mf);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ABI D3D11 frames can only be imported on Windows.");
        var native = mf.Payload.D3D11;
        if (native.LumaNtSharedHandle == 0 || native.YStride <= 0 || native.UvStride <= 0)
            throw new InvalidOperationException("ABI D3D11 frame is missing its shared handle or plane strides.");

        var luma = DuplicateCurrentProcessHandle((nint)native.LumaNtSharedHandle);
        var sourceChroma = native.ChromaNtSharedHandle == 0
            ? (nint)native.LumaNtSharedHandle
            : (nint)native.ChromaNtSharedHandle;
        nint chroma;
        if (sourceChroma == (nint)native.LumaNtSharedHandle)
            chroma = luma;
        else
        {
            try { chroma = DuplicateCurrentProcessHandle(sourceChroma); }
            catch
            {
                _ = CloseHandle(luma);
                throw;
            }
        }

        try
        {
            var format = FormatOf(in mf, fallbackRate);
            if (format.PixelFormat != PixelFormat.Nv12)
                throw new NotSupportedException($"ABI D3D11 import currently supports NV12, not {format.PixelFormat}.");
            var backing = new Win32SharedNv12Backing(
                luma, chroma, native.YStride, native.UvStride, checked((int)native.ArraySlice));
            return VideoFrame.CreateNv12Win32Shared(TimeSpan.FromTicks(mf.PtsTicks), format, backing);
        }
        catch
        {
            _ = CloseHandle(luma);
            if (chroma != luma)
                _ = CloseHandle(chroma);
            throw;
        }
    }

    private static VideoFormat FormatOf(in MfpVideoFrame mf, Rational fallbackRate) =>
        new((int)mf.Width, (int)mf.Height, ToCore(mf.PixelFormat),
            fallbackRate.Numerator > 0 ? fallbackRate : new Rational(30, 1));

    private static void EnsureNoExplicitSync(in MfpVideoFrame mf)
    {
        if (mf.Sync.Kind != 0)
            throw new NotSupportedException(
                $"ABI sync kind {mf.Sync.Kind} was not negotiated; this host currently imports synchronized/implicit-sync frames only.");
    }

    private static void SetDmaBuf(
        ref MfpVideoFrame native,
        int yFd,
        int uvFd,
        nint yOffset,
        nint uvOffset,
        int yStride,
        int uvStride,
        ulong yModifier,
        ulong uvModifier)
    {
        native.Kind = MfpFrameKind.DmaBuf;
        native.Payload.DmaBuf.PlaneCount = 2;
        native.Payload.DmaBuf.Fds[0] = yFd;
        native.Payload.DmaBuf.Fds[1] = uvFd;
        native.Payload.DmaBuf.Offsets[0] = checked((int)yOffset);
        native.Payload.DmaBuf.Offsets[1] = checked((int)uvOffset);
        native.Payload.DmaBuf.Strides[0] = yStride;
        native.Payload.DmaBuf.Strides[1] = uvStride;
        native.Payload.DmaBuf.Modifiers[0] = yModifier;
        native.Payload.DmaBuf.Modifiers[1] = uvModifier;
    }

    private static nint DuplicateCurrentProcessHandle(nint source)
    {
        var process = GetCurrentProcess();
        if (!DuplicateHandle(process, source, process, out var duplicate, 0, false, 2))
            throw new InvalidOperationException(
                $"DuplicateHandle failed for an ABI D3D11 shared handle (Win32 error {Marshal.GetLastWin32Error()}).");
        return duplicate;
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

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int libc_dup(int fd);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int libc_close(int fd);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        nint sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
