using System.Runtime.InteropServices;
using S.Media.PortAudio;

namespace S.Media.Interop;

/// <summary>
/// PortAudio device discovery. Usage: call <c>*_count</c> (returns the count and snapshots the device list
/// on the calling thread), then <c>*_get(index, …)</c> for each device. Negative return = error
/// (see <c>mfp_last_error</c>). Any out-pointer may be null if you don't want that field.
/// </summary>
internal static unsafe partial class NativeApi
{
    [ThreadStatic] private static PortAudioOutputDeviceEntry[]? _outputDevices;
    [ThreadStatic] private static PortAudioInputDeviceEntry[]? _inputDevices;

    /// <summary>Snapshots and returns the number of PortAudio output devices, or a negative error code.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_portaudio_output_device_count")]
    public static int PortAudioOutputDeviceCount()
    {
        try
        {
            _outputDevices = [.. PortAudioDeviceCatalog.EnumerateOutputDevices()];
            return _outputDevices.Length;
        }
        catch (Exception ex)
        {
            _outputDevices = null;
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Reads the output device at <paramref name="index"/> from the last <c>*_count</c> snapshot.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_portaudio_output_device_get")]
    public static int PortAudioOutputDeviceGet(
        int index, int* outDeviceIndex, int* outMaxChannels, double* outDefaultSampleRate, byte* nameBuffer, int nameLen)
    {
        var devices = _outputDevices;
        if (devices is null)
            return Fail("call mfp_portaudio_output_device_count first", ErrInvalidArg);
        if ((uint)index >= (uint)devices.Length)
            return Fail("device index out of range", ErrInvalidArg);

        var d = devices[index];
        if (outDeviceIndex is not null) *outDeviceIndex = d.GlobalDeviceIndex;
        if (outMaxChannels is not null) *outMaxChannels = d.MaxOutputChannels;
        if (outDefaultSampleRate is not null) *outDefaultSampleRate = d.DefaultSampleRate;
        WriteUtf8(d.Name, nameBuffer, nameLen);
        return Ok;
    }

    /// <summary>Snapshots and returns the number of PortAudio input (capture) devices, or a negative error code.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_portaudio_input_device_count")]
    public static int PortAudioInputDeviceCount()
    {
        try
        {
            _inputDevices = [.. PortAudioDeviceCatalog.EnumerateInputDevices()];
            return _inputDevices.Length;
        }
        catch (Exception ex)
        {
            _inputDevices = null;
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Reads the input device at <paramref name="index"/> from the last <c>*_count</c> snapshot.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_portaudio_input_device_get")]
    public static int PortAudioInputDeviceGet(
        int index, int* outDeviceIndex, int* outMaxChannels, double* outDefaultSampleRate, byte* nameBuffer, int nameLen)
    {
        var devices = _inputDevices;
        if (devices is null)
            return Fail("call mfp_portaudio_input_device_count first", ErrInvalidArg);
        if ((uint)index >= (uint)devices.Length)
            return Fail("device index out of range", ErrInvalidArg);

        var d = devices[index];
        if (outDeviceIndex is not null) *outDeviceIndex = d.GlobalDeviceIndex;
        if (outMaxChannels is not null) *outMaxChannels = d.MaxInputChannels;
        if (outDefaultSampleRate is not null) *outDefaultSampleRate = d.DefaultSampleRate;
        WriteUtf8(d.Name, nameBuffer, nameLen);
        return Ok;
    }
}
