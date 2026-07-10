using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MALib;

/// <summary>Device type passed to <see cref="MiniAudioNative"/> (matches <c>ma_device_type</c>).</summary>
public enum MiniAudioDeviceType
{
    Playback = 1,
    Capture = 2,
}

/// <summary>
/// Direct P/Invoke binding for <b>vanilla miniaudio</b> (<c>ma_*</c>) - the analogue of <c>PALib</c> for
/// PortAudio. There is NO custom C wrapper: it binds the upstream <c>libminiaudio</c> ABI and does the
/// device-config plumbing in managed code. Because miniaudio is header-only and exposes no FFI size/offset
/// accessors for its opaque handles (only <c>ma_context_sizeof</c>), the layouts below are hand-mirrored from
/// miniaudio 0.11.25's headers and the opaque <c>ma_device</c> is over-allocated. <b>Layout-sensitive: a
/// field/offset/size error corrupts memory at runtime - verify on real audio hardware after any miniaudio
/// version bump.</b>
/// </summary>
public static unsafe partial class MiniAudioNative
{
    public const int Success = 0;            // MA_SUCCESS
    private const int FormatF32 = 5;         // ma_format_f32

    // miniaudio 0.11.25 sizes (x64), hand-derived from miniaudio.h:
    //   ma_device_id   = 256 B (union; largest member is char[256])
    //   ma_device_info = 1544 B (id[256] + name[256] + isDefault[4] + count[4] + nativeDataFormats[64*16])
    private const int MaDeviceIdSize = 256;
    private const long MaDeviceInfoSize = 1544;
    private const int NameOffset = 256;
    private const int IsDefaultOffset = 512;
    private const int NativeFormatCountOffset = 516;
    private const int NativeFormatsOffset = 520;
    private const int NativeFormatStride = 16;   // {format,channels,sampleRate,flags}
    // ma_device has no size accessor; over-allocate generously (real size is a few KB across all backends).
    private const int MaDeviceAllocSize = 65536;

    private const string Library = "miniaudio";

    private readonly record struct DeviceCallback(nint CallbackPtr, nint UserData);
    private static readonly ConcurrentDictionary<nint, DeviceCallback> DeviceCallbacks = new();

    // --- mirrored structs (sequential layout = runtime computes C-matching offsets) --------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MaResamplerConfig
    {
        public int Format; public uint Channels; public uint SampleRateIn; public uint SampleRateOut;
        public int Algorithm; public nint BackendVTable; public nint BackendUserData; public uint LinearLpfOrder;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaDeviceSubConfig
    {
        public nint DeviceId; public int Format; public uint Channels; public nint ChannelMap;
        public int ChannelMixMode; public uint CalculateLfeFromSpatialChannels; public int ShareMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaDeviceConfig
    {
        public int DeviceType; public uint SampleRate; public uint PeriodSizeInFrames; public uint PeriodSizeInMilliseconds;
        public uint Periods; public int PerformanceProfile;
        public byte NoPreSilencedOutputBuffer; public byte NoClip; public byte NoDisableDenormals; public byte NoFixedSizedCallback;
        public nint DataCallback; public nint NotificationCallback; public nint StopCallback; public nint UserData;
        public MaResamplerConfig Resampling;
        public MaDeviceSubConfig Playback;
        public MaDeviceSubConfig Capture;
        // Backend-specific config blocks (wasapi/alsa/pulse/coreaudio/opensl/aaudio/…) follow `capture`. We
        // never set them (ma_device_config_init defaults them); the buffer only needs to make this struct at
        // least as large as the native one so the by-value init/read can't overflow. 1 KB is generous.
        public fixed byte BackendSpecificTail[1024];
    }

    // --- raw ma_* imports ------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "ma_context_sizeof")]
    private static partial nuint ma_context_sizeof();

    [LibraryImport(Library, EntryPoint = "ma_context_init")]
    private static partial int ma_context_init(nint backends, uint backendCount, nint config, nint context);

    [LibraryImport(Library, EntryPoint = "ma_context_uninit")]
    private static partial void ma_context_uninit(nint context);

    [LibraryImport(Library, EntryPoint = "ma_context_get_devices")]
    private static partial int ma_context_get_devices(
        nint context, out nint playbackInfos, out uint playbackCount, out nint captureInfos, out uint captureCount);

    [LibraryImport(Library, EntryPoint = "ma_device_config_init")]
    private static partial MaDeviceConfig ma_device_config_init(int deviceType);

    [LibraryImport(Library, EntryPoint = "ma_device_init")]
    private static partial int ma_device_init(nint context, MaDeviceConfig* config, nint device);

    [LibraryImport(Library, EntryPoint = "ma_device_uninit")]
    private static partial void ma_device_uninit(nint device);

    [LibraryImport(Library, EntryPoint = "ma_device_start")]
    private static partial int ma_device_start_native(nint device);

    [LibraryImport(Library, EntryPoint = "ma_device_stop")]
    private static partial int ma_device_stop_native(nint device);

    [LibraryImport(Library, EntryPoint = "ma_device_is_started")]
    private static partial uint ma_device_is_started_native(nint device);

    [LibraryImport(Library, EntryPoint = "ma_device_get_state")]
    private static partial int ma_device_get_state_native(nint device);

    [LibraryImport(Library, EntryPoint = "ma_result_description")]
    private static partial nint ma_result_description(int result);

    // --- the data-callback bridge ----------------------------------------------------------------
    // miniaudio's callback is ma_device_data_proc(ma_device*, void* out, const void* in, uint frames). We
    // route it to the managed delegate the caller passed (which has the framework's float-buffer signature),
    // keyed by the device pointer (avoids needing the offset of pUserData inside the opaque ma_device).

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DataProc(nint pDevice, void* pOutput, void* pInput, uint frameCount)
    {
        if (!DeviceCallbacks.TryGetValue(pDevice, out var cb) || cb.CallbackPtr == nint.Zero)
            return;
        try
        {
            ((delegate* unmanaged[Cdecl]<nint, float*, float*, uint, void>)cb.CallbackPtr)(
                cb.UserData, (float*)pOutput, (float*)pInput, frameCount);
        }
        catch
        {
            // A managed callback must never let an exception cross the native boundary.
        }
    }

    // --- public operations (same surface S.Media.MiniAudio consumes) -----------------------------

    public static int ContextCreate(out nint context)
    {
        context = nint.Zero;
        var size = (int)ma_context_sizeof();
        if (size <= 0)
            return -1;
        var ctx = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)ctx, size).Clear();
        var result = ma_context_init(nint.Zero, 0, nint.Zero, ctx);
        if (result != Success)
        {
            Marshal.FreeHGlobal(ctx);
            return result;
        }
        context = ctx;
        return Success;
    }

    public static void ContextDestroy(nint context)
    {
        if (context == nint.Zero) return;
        ma_context_uninit(context);
        Marshal.FreeHGlobal(context);
    }

    public static int ContextDeviceCount(nint context, int deviceType, out uint count)
    {
        count = 0;
        var result = ma_context_get_devices(context, out _, out var playbackCount, out _, out var captureCount);
        if (result != Success) return result;
        count = deviceType == (int)MiniAudioDeviceType.Capture ? captureCount : playbackCount;
        return Success;
    }

    public static int ContextDeviceGet(
        nint context, int deviceType, uint index, byte* idBuffer, int idLen, byte* nameBuffer, int nameLen,
        out uint isDefault, out uint maxChannels, out uint defaultSampleRate)
    {
        isDefault = 0; maxChannels = 0; defaultSampleRate = 0;
        var result = ma_context_get_devices(context, out var playbackInfos, out var playbackCount, out var captureInfos, out var captureCount);
        if (result != Success) return result;

        var capture = deviceType == (int)MiniAudioDeviceType.Capture;
        var infos = capture ? captureInfos : playbackInfos;
        var count = capture ? captureCount : playbackCount;
        if (index >= count) return -2;

        var info = (byte*)(infos + (nint)index * MaDeviceInfoSize);
        WriteHex(info, MaDeviceIdSize, idBuffer, idLen);
        CopyCString(info + NameOffset, nameBuffer, nameLen);
        isDefault = *(uint*)(info + IsDefaultOffset);

        var formatCount = *(uint*)(info + NativeFormatCountOffset);
        uint maxCh = 0, rate = 0;
        for (uint i = 0; i < formatCount && i < 64; i++)
        {
            var rec = info + NativeFormatsOffset + (nint)i * NativeFormatStride;
            var ch = *(uint*)(rec + 4);
            var sr = *(uint*)(rec + 8);
            if (ch == 0) ch = 2;
            if (ch > maxCh) maxCh = ch;
            if (rate == 0 && sr != 0) rate = sr;
        }
        maxChannels = maxCh == 0 ? 2u : maxCh;
        defaultSampleRate = rate == 0 ? 48000u : rate;
        return Success;
    }

    public static int DeviceCreate(
        int deviceType, byte* deviceIdHex, uint sampleRate, uint channels, uint periodSizeFrames,
        delegate* unmanaged[Cdecl]<nint, float*, float*, uint, void> callback, nint userData, out nint device)
    {
        device = nint.Zero;

        var config = ma_device_config_init(deviceType);
        config.SampleRate = sampleRate;
        config.PeriodSizeInFrames = periodSizeFrames;
        config.DataCallback = (nint)(delegate* unmanaged[Cdecl]<nint, void*, void*, uint, void>)&DataProc;

        Span<byte> id = stackalloc byte[MaDeviceIdSize];
        var hasId = TryDecodeHex(deviceIdHex, id);

        var devicePtr = Marshal.AllocHGlobal(MaDeviceAllocSize);
        new Span<byte>((void*)devicePtr, MaDeviceAllocSize).Clear();
        config.UserData = devicePtr;

        int result;
        fixed (byte* idPtr = id)
        {
            if (deviceType == (int)MiniAudioDeviceType.Capture)
            {
                config.Capture.Format = FormatF32;
                config.Capture.Channels = channels;
                config.Capture.DeviceId = hasId ? (nint)idPtr : nint.Zero;
            }
            else
            {
                config.Playback.Format = FormatF32;
                config.Playback.Channels = channels;
                config.Playback.DeviceId = hasId ? (nint)idPtr : nint.Zero;
            }

            DeviceCallbacks[devicePtr] = new DeviceCallback((nint)callback, userData);
            result = ma_device_init(nint.Zero, &config, devicePtr);
        }

        if (result != Success)
        {
            DeviceCallbacks.TryRemove(devicePtr, out _);
            Marshal.FreeHGlobal(devicePtr);
            return result;
        }

        device = devicePtr;
        return Success;
    }

    public static int DeviceStart(nint device) => ma_device_start_native(device);

    public static int DeviceStop(nint device) => ma_device_stop_native(device);

    public static void DeviceDestroy(nint device)
    {
        if (device == nint.Zero) return;
        ma_device_uninit(device);
        DeviceCallbacks.TryRemove(device, out _);
        Marshal.FreeHGlobal(device);
    }

    public static int DeviceIsStarted(nint device) => device == nint.Zero ? 0 : (int)ma_device_is_started_native(device);

    public static int DeviceGetState(nint device) => device == nint.Zero ? 0 : ma_device_get_state_native(device);

    public static int DeviceIdHexCapacity() => MaDeviceIdSize * 2 + 1;

    public static string ResultDescription(int result) =>
        Marshal.PtrToStringUTF8(ma_result_description(result)) ?? $"miniaudio result {result}";

    // --- helpers ---------------------------------------------------------------------------------

    public static byte[] ToUtf8NullTerminated(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [0];
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[^1] = 0;
        return bytes;
    }

    public static string FromUtf8NullTerminated(byte[] buffer)
    {
        var len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return len == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, len);
    }

    private static void WriteHex(byte* src, int srcLen, byte* dst, int dstLen)
    {
        if (dst is null || dstLen <= 0) return;
        var needed = srcLen * 2 + 1;
        if (dstLen < needed) { dst[0] = 0; return; }
        ReadOnlySpan<byte> hex = "0123456789abcdef"u8;
        for (var i = 0; i < srcLen; i++)
        {
            dst[i * 2] = hex[(src[i] >> 4) & 0xF];
            dst[i * 2 + 1] = hex[src[i] & 0xF];
        }
        dst[srcLen * 2] = 0;
    }

    private static bool TryDecodeHex(byte* hex, Span<byte> dst)
    {
        dst.Clear();
        if (hex is null || hex[0] == 0) return false;
        var len = 0;
        while (hex[len] != 0) len++;
        if (len != dst.Length * 2) return false;
        for (var i = 0; i < dst.Length; i++)
        {
            var hi = HexVal(hex[i * 2]);
            var lo = HexVal(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            dst[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int HexVal(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    private static void CopyCString(byte* src, byte* dst, int dstLen)
    {
        if (dst is null || dstLen <= 0) return;
        var i = 0;
        while (i < dstLen - 1 && src[i] != 0) { dst[i] = src[i]; i++; }
        dst[i] = 0;
    }
}
