using System.Runtime.InteropServices;

namespace S.Abi;

// Managed mirrors of the C ABI in include/mfp_plugin.h. Layout-critical: [StructLayout(Sequential)] + the exact
// C field order/types so a struct read/written across the boundary matches byte-for-byte on x64. Plugin->host
// callbacks are `delegate* unmanaged` to [UnmanagedCallersOnly] methods (AOT-safe - no reflection).

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
    public uint StructSize;
    public delegate* unmanaged<int, byte*, void> Log;            // (level, msg)
    public delegate* unmanaged<byte*, void> SetLastError;
    public delegate* unmanaged<long> NowTicks;                   // 100-ns ticks
    public uint SupportedFrameKinds;
    public uint SupportedSyncKinds;
}

// The plugin calls these to register its capability vtables. Every add_* is (ctx, id, vtable, self) -> int; the
// vtable pointer is opaque here (the host records it and binds an adapter to it later).
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpRegistrar
{
    public uint AbiVersion;
    public uint StructSize;
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
    public uint StructSize;
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
    public uint AbiVersion;
    public uint StructSize;
    // (self, osc_address, args, arg_count, blob_arg_index, out[], out_cap, out_count) -> int status
    public delegate* unmanaged<void*, byte*, MfpControlArg*, int, int, MfpControlReading*, int, int*, int> Decode;
    public delegate* unmanaged<void*, void> Destroy;
}

internal enum MfpControlArgKind
{
    Unsupported = 0, Int32, Float32, String, Blob, Int64, Double64, True, False, Nil, Impulse,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpControlArg
{
    public MfpControlArgKind Kind;
    public int DataLength;
    public long IntValue;
    public double FloatValue;
    public byte* Data;
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
    public fixed ulong Modifiers[4];
    public uint Fourcc;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MfpD3D11Frame
{
    public ulong LumaNtSharedHandle;
    public ulong ChromaNtSharedHandle;
    public uint DxgiFormat;
    public uint ArraySlice;
    public int YStride;
    public int UvStride;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MfpGlTextureFrame
{
    public uint TextureId;
    public uint Target;
    public ulong ContextId;
}

// Tagged union - the kind-specific payload of MfpVideoFrame. Explicit overlay; its size = the largest member
// (MfpDmaBufFrame), so the managed struct's total size equals the C sizeof and a plugin can't write past it.
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct MfpFramePayload
{
    [FieldOffset(0)] public MfpCpuFrame Cpu;
    [FieldOffset(0)] public MfpDmaBufFrame DmaBuf;
    [FieldOffset(0)] public MfpD3D11Frame D3D11;
    [FieldOffset(0)] public MfpGlTextureFrame Gl;
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
    public uint AbiVersion;
    public uint StructSize;
    public uint SupportedFrameKinds;
    public uint SupportedSyncKinds;
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
    public uint AbiVersion;
    public uint StructSize;
    public delegate* unmanaged<void*, byte*, int> CanOpen;                  // (self, uri)
    public delegate* unmanaged<void*, byte*, MfpMediaSource*, int> Open;    // (self, uri, out)
    public MfpVideoSourceVTable* VideoSourceVTable;
    public MfpAudioSourceVTable* AudioSourceVTable;
    public delegate* unmanaged<void*, void> Destroy;
}

// --- audio backend ---

[StructLayout(LayoutKind.Sequential)]
internal struct MfpAudioFormat
{
    public uint SampleRate;
    public uint Channels;
    public int SampleFormat;   // MfpAudioSampleFormat; 0 = f32 interleaved
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpAudioDeviceInfo
{
    public fixed byte Id[128];
    public fixed byte Name[128];
    public uint MaxChannels;
    public uint DefaultSampleRate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MfpAudioOpts
{
    public double SuggestedLatencySeconds;
    public uint PrebufferFrames;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpAudioBackendVTable
{
    public uint AbiVersion;
    public uint StructSize;
    public delegate* unmanaged<void*, MfpAudioDeviceInfo*, int, int*, int> EnumerateOutputs;
    public delegate* unmanaged<void*, MfpAudioDeviceInfo*, int, int*, int> EnumerateInputs;
    public delegate* unmanaged<void*, byte*, MfpAudioFormat*, MfpAudioOpts*, void*> OpenOutput;
    public delegate* unmanaged<void*, byte*, MfpAudioFormat*, MfpAudioOpts*, void*> OpenInput;
    public delegate* unmanaged<void*, float*, int, int> OutputSubmit;   // (handle, interleaved, float_count)
    public delegate* unmanaged<void*, float*, int, int> InputReadInto;  // (handle, dst, float_count)
    public delegate* unmanaged<void*, void> CloseHandle;
    public delegate* unmanaged<void*, void> Destroy;
    public delegate* unmanaged<void*, long> OutputPlayedFrames;         // optional (NULL = not a clock)
    public delegate* unmanaged<void*, int> OutputWritableFrames;        // optional (NULL = always writable)
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpAudioSourceVTable
{
    public uint AbiVersion;
    public uint StructSize;
    public delegate* unmanaged<void*, MfpAudioFormat*, int> NativeFormat;
    public delegate* unmanaged<void*, float*, int, int> ReadInto;
    public delegate* unmanaged<void*, int> IsExhausted;
    public delegate* unmanaged<void*, long, int> Seek;
    public delegate* unmanaged<void*, void> Destroy;
}

// --- video output ---

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpVideoOutputVTable
{
    public uint AbiVersion;
    public uint StructSize;
    public uint AcceptedFrameKinds;
    public uint AcceptedSyncKinds;
    public delegate* unmanaged<void*, int*, int, int*, int> AcceptedPixelFormats;
    public delegate* unmanaged<void*, MfpVideoFormat*, int> Configure;
    public delegate* unmanaged<void*, MfpVideoFrame*, int> Submit;
    public delegate* unmanaged<void*, void> Destroy;
    public delegate* unmanaged<void*, void> AbandonQueued;          // optional (NULL = no internal queue)
    public delegate* unmanaged<void*, int, int> WaitForIdle;        // optional; (self, timeout_ms) -> idle?
}

// --- subtitle ---

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpSubtitleVTable
{
    public uint AbiVersion;
    public uint StructSize;
    public delegate* unmanaged<void*, long, MfpVideoFrame*, int> RenderAt;  // (self, position_ticks, out)
    public delegate* unmanaged<void*, MfpVideoFrame*, void> ReleaseFrame;
    public delegate* unmanaged<void*, void> Destroy;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpSubtitleProviderVTable
{
    public uint AbiVersion;
    public uint StructSize;
    public delegate* unmanaged<void*, byte*, int> CanOpen;
    public delegate* unmanaged<void*, byte*, uint, uint, void*> Open;   // (self, uri, canvas_w, canvas_h) -> instance
    public MfpSubtitleVTable* SubtitleVTable;
    public delegate* unmanaged<void*, void> Destroy;
}

// --- layer surface (GL) ---

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpGlContext
{
    public ulong ContextId;
    public delegate* unmanaged<byte*, void*> GetProcAddress;   // (name) -> GL proc address
}

[StructLayout(LayoutKind.Sequential)]
internal struct MfpTransform2D
{
    public float A;
    public float B;
    public float C;
    public float D;
    public float Tx;
    public float Ty;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpLayerSurfaceVTable
{
    public uint AbiVersion;
    public uint StructSize;
    public delegate* unmanaged<void*, MfpGlContext*, uint, uint, int> ConfigureGl;  // (surface, ctx, canvas_w, canvas_h)
    public delegate* unmanaged<void*, MfpGlContext*, uint, long, MfpTransform2D*, float, int> Render; // (surface, ctx, fbo, master_ticks, placement, opacity)
    public delegate* unmanaged<void*, void> Destroy;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MfpLayerSurfaceFactoryVTable
{
    public uint AbiVersion;
    public uint StructSize;
    public delegate* unmanaged<void*, byte*, void*> Create;   // (self, config_json) -> surface instance
    public MfpLayerSurfaceVTable* SurfaceVTable;
    public delegate* unmanaged<void*, void> Destroy;
}
