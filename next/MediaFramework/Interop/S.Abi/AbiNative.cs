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
