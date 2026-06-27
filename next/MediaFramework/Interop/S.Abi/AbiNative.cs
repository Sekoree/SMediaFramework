using System.Runtime.InteropServices;

namespace S.Abi;

// Managed mirrors of the C ABI in include/mfp_plugin.h. Layout-critical: [StructLayout(Sequential)] + the exact
// C field order/types so a struct read/written across the boundary matches byte-for-byte on x64. Plugin->host
// callbacks are `delegate* unmanaged` to [UnmanagedCallersOnly] methods (AOT-safe — no reflection).

internal enum MfpStatus
{
    Ok = 0,
    ErrUnsupported = -1, ErrInvalidArg = -2, ErrNotFound = -3,
    ErrAgain = -4, ErrEnd = -5, ErrInternal = -6, ErrAbiMismatch = -7,
}

[System.Flags]
internal enum MfpCapability : uint
{
    AudioBackend = 1u << 0, VideoSource = 1u << 1, AudioSource = 1u << 2,
    VideoOutput = 1u << 3, LayerSurface = 1u << 4, Subtitle = 1u << 5, ControlDecoder = 1u << 6,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MfpVideoFormat
{
    public uint Width;
    public uint Height;
    public int PixelFormat;
    public uint FpsNum;
    public uint FpsDen;
}

// Host services handed to the plugin (function pointers into the host).
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpHostApi
{
    public uint AbiVersion;
    public delegate* unmanaged<int, byte*, void> Log;            // (level, msg)
    public delegate* unmanaged<byte*, void> SetLastError;
    public delegate* unmanaged<long> NowTicks;                   // 100-ns ticks
    public delegate* unmanaged<MfpVideoFormat*, void*> AllocFrame;
    public delegate* unmanaged<void*, void> ReleaseFrame;
}

// The plugin calls these to register its capability vtables. Every add_* is (ctx, id, vtable, self) -> int; the
// vtable pointer is opaque here (the host records it and binds an adapter to it later).
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpRegistrar
{
    public uint AbiVersion;
    public void* Ctx;
    public delegate* unmanaged<void*, byte*, void*, void*, int> AddAudioBackend;
    public delegate* unmanaged<void*, byte*, void*, void*, int> AddMediaSourceProvider;
    public delegate* unmanaged<void*, byte*, void*, void*, int> AddVideoOutput;
    public delegate* unmanaged<void*, byte*, void*, void*, int> AddLayerSurface;
    public delegate* unmanaged<void*, byte*, void*, void*, int> AddSubtitleProvider;
    public delegate* unmanaged<void*, byte*, void*, void*, int> AddControlDecoder;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpPluginInfo
{
    public uint AbiVersion;
    public byte* Id;
    public byte* DisplayName;
    public uint Capabilities;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpControlReading
{
    public fixed byte Address[160];   // null-terminated UTF-8
    public double Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpControlDecoderVTable
{
    // (self, osc_address, blob, blob_len, out[], out_cap, out_count) -> int status
    public delegate* unmanaged<void*, byte*, byte*, int, MfpControlReading*, int, int*, int> Decode;
    public delegate* unmanaged<void*, void> Destroy;
}

internal enum MfpFrameKind { Cpu = 0, DmaBuf = 1, D3D11 = 2, GlTexture = 3 }

[StructLayout(LayoutKind.Sequential)]
internal struct MfpSync
{
    public int Kind;
    public ulong Handle;
    public ulong Value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpCpuFrame
{
    public void* Plane0;
    public void* Plane1;
    public void* Plane2;
    public void* Plane3;
    public int Stride0;
    public int Stride1;
    public int Stride2;
    public int Stride3;
    public int PlaneCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpDmaBufFrame
{
    public int PlaneCount;
    public fixed int Fds[4];
    public fixed int Offsets[4];
    public fixed int Strides[4];
    public ulong DrmModifier;
    public uint Fourcc;
}

// Tagged union — the kind-specific payload of MfpVideoFrame. Explicit overlay; its size = the largest member
// (MfpDmaBufFrame), so the managed struct's total size equals the C sizeof and a plugin can't write past it.
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct MfpFramePayload
{
    [FieldOffset(0)] public MfpCpuFrame Cpu;
    [FieldOffset(0)] public MfpDmaBufFrame DmaBuf;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpVideoFrame
{
    public MfpFrameKind Kind;
    public uint Width;
    public uint Height;
    public int PixelFormat;
    public long PtsTicks;
    public MfpSync Sync;
    public void* Opaque;
    public MfpFramePayload Payload;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpMediaSource
{
    public void* Video;
    public void* Audio;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpVideoSourceVTable
{
    public delegate* unmanaged<void*, int*, int, int*, int> NativePixelFormats;  // (src, out[], cap, count)
    public delegate* unmanaged<void*, int, int> SelectOutputFormat;              // (src, MfpPixelFormat)
    public delegate* unmanaged<void*, MfpVideoFormat*, int> GetFormat;           // (src, out)
    public delegate* unmanaged<void*, MfpVideoFrame*, int> TryReadFrame;
    public delegate* unmanaged<void*, MfpVideoFrame*, void> ReleaseFrame;
    public delegate* unmanaged<void*, int> IsExhausted;
    public delegate* unmanaged<void*, long, int> Seek;
    public delegate* unmanaged<void*, void> Destroy;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpMediaSourceProviderVTable
{
    public delegate* unmanaged<void*, byte*, int> CanOpen;                  // (self, uri)
    public delegate* unmanaged<void*, byte*, MfpMediaSource*, int> Open;    // (self, uri, out)
    public MfpVideoSourceVTable* VideoSourceVTable;
    public void* AudioSourceVTable;   // MfpAudioSourceVTable* — opaque until audio is modeled
    public delegate* unmanaged<void*, void> Destroy;
}
