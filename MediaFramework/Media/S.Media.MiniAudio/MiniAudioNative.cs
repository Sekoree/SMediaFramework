using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using S.Media.MiniAudio.Runtime;

namespace S.Media.MiniAudio;

internal enum MiniAudioDeviceType
{
    Playback = 1,
    Capture = 2,
}

internal static unsafe partial class MiniAudioNative
{
    public const int Success = 0;

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_device_id_hex_capacity")]
    public static partial int DeviceIdHexCapacity();

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_result_description")]
    private static partial nint ResultDescriptionNative(int result);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_context_create")]
    public static partial int ContextCreate(out nint context);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_context_destroy")]
    public static partial void ContextDestroy(nint context);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_context_device_count")]
    public static partial int ContextDeviceCount(nint context, int deviceType, out uint count);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_context_device_get")]
    public static partial int ContextDeviceGet(
        nint context,
        int deviceType,
        uint index,
        byte* idBuffer,
        int idLen,
        byte* nameBuffer,
        int nameLen,
        out uint isDefault,
        out uint maxChannels,
        out uint defaultSampleRate);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_device_create")]
    public static partial int DeviceCreate(
        int deviceType,
        byte* deviceIdHex,
        uint sampleRate,
        uint channels,
        uint periodSizeFrames,
        delegate* unmanaged[Cdecl]<nint, float*, float*, uint, void> callback,
        nint userData,
        out nint device);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_device_start")]
    public static partial int DeviceStart(nint device);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_device_stop")]
    public static partial int DeviceStop(nint device);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_device_destroy")]
    public static partial void DeviceDestroy(nint device);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_device_is_started")]
    public static partial int DeviceIsStarted(nint device);

    [LibraryImport(MiniAudioLibraryNames.Default, EntryPoint = "sma_device_get_state")]
    public static partial int DeviceGetState(nint device);

    public static string ResultDescription(int result)
    {
        var ptr = ResultDescriptionNative(result);
        return Marshal.PtrToStringUTF8(ptr) ?? $"miniaudio result {result}";
    }

    public static byte[] ToUtf8NullTerminated(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return [0];

        var byteCount = Encoding.UTF8.GetByteCount(value);
        var bytes = new byte[byteCount + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[^1] = 0;
        return bytes;
    }

    public static string FromUtf8NullTerminated(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) length = buffer.Length;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, length);
    }
}
