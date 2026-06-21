using System.Runtime.InteropServices;
using S.Media.Core.Audio;

namespace S.Media.Interop;

/// <summary>
/// Backend-agnostic audio surface over <see cref="AudioBackends"/>: list backends, enumerate a backend's
/// devices, and create outputs/sources on any backend by name. This is what makes the C ABI ready for
/// additional audio backends (e.g. miniaudio) without new per-backend entry points — a host passes the
/// backend name (null/empty = the default backend) and an opaque device id from the enumeration.
/// </summary>
internal static unsafe partial class NativeApi
{
    [ThreadStatic] private static AudioDeviceInfo[]? _backendDevices;
    [ThreadStatic] private static AudioDeviceInfo[]? _backendInputDevices;

    /// <summary>Number of registered audio backends (e.g. PortAudio).</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_backend_count")]
    public static int AudioBackendCount() => AudioBackends.All.Count;

    /// <summary>Copies the name of the backend at <paramref name="index"/> (UTF-8); negative on a bad index.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_backend_name")]
    public static int AudioBackendName(int index, byte* buffer, int bufferLen)
    {
        var all = AudioBackends.All;
        if ((uint)index >= (uint)all.Count)
            return Fail("backend index out of range", ErrInvalidArg);
        return WriteUtf8(all[index].Name, buffer, bufferLen);
    }

    /// <summary>
    /// Creates (and starts) an audio output on <paramref name="backendName"/> (null/empty = default backend)
    /// and device <paramref name="deviceId"/> (null/empty = system default; otherwise an id from
    /// <c>mfp_audio_device_get</c>). The handle is owned by the host — free it with <c>mfp_output_destroy</c>.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_output_create")]
    public static int AudioOutputCreate(byte* backendName, byte* deviceId, int sampleRate, int channels, IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;
        if (sampleRate <= 0 || channels <= 0)
            return Fail("sampleRate and channels must be > 0", ErrInvalidArg);

        var backend = ResolveBackend(backendName);
        if (backend is null)
            return Fail("no such audio backend (call mfp_initialize, or register the backend)", ErrInvalidArg);

        try
        {
            var output = backend.CreateOutput(Utf8(deviceId), new AudioFormat(sampleRate, channels));
            *outHandle = Handles.Alloc(output);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>
    /// Creates (and starts) a live audio source on <paramref name="backendName"/> (null/empty = default
    /// backend). The handle is owned by the host until it is destroyed with <c>mfp_audio_source_destroy</c>
    /// or transferred into a player with <c>mfp_player_open_live_audio</c>.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_input_create")]
    public static int AudioInputCreate(byte* backendName, byte* deviceId, int sampleRate, int channels, IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;
        if (sampleRate <= 0 || channels <= 0)
            return Fail("sampleRate and channels must be > 0", ErrInvalidArg);

        var backend = ResolveBackend(backendName);
        if (backend is null)
            return Fail("no such audio backend (call mfp_initialize, or register the backend)", ErrInvalidArg);

        try
        {
            var source = backend.CreateInput(Utf8(deviceId), new AudioFormat(sampleRate, channels));
            *outHandle = Handles.Alloc(source);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Frees a host-created audio source handle. Do not call after <c>mfp_player_open_live_audio</c>.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_source_destroy")]
    public static void AudioSourceDestroy(IntPtr source) => Handles.Free(source, dispose: true);

    /// <summary>Snapshots and returns a backend's output device count (null/empty backend = default). Negative on error.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_device_count")]
    public static int AudioDeviceCount(byte* backendName)
    {
        var backend = ResolveBackend(backendName);
        if (backend is null)
        {
            _backendDevices = null;
            return Fail("no such audio backend", ErrInvalidArg);
        }

        try
        {
            _backendDevices = [.. backend.EnumerateOutputDevices()];
            return _backendDevices.Length;
        }
        catch (Exception ex)
        {
            _backendDevices = null;
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>
    /// Reads the device at <paramref name="index"/> from the last <c>mfp_audio_device_count</c> snapshot.
    /// Writes the opaque id (pass to <c>mfp_audio_output_create</c>) and name; any out-pointer may be null.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_device_get")]
    public static int AudioDeviceGet(
        int index, int* outMaxChannels, double* outDefaultSampleRate, int* outIsDefault,
        byte* idBuffer, int idLen, byte* nameBuffer, int nameLen)
    {
        var devices = _backendDevices;
        if (devices is null)
            return Fail("call mfp_audio_device_count first", ErrInvalidArg);
        if ((uint)index >= (uint)devices.Length)
            return Fail("device index out of range", ErrInvalidArg);

        var d = devices[index];
        if (outMaxChannels is not null) *outMaxChannels = d.MaxChannels;
        if (outDefaultSampleRate is not null) *outDefaultSampleRate = d.DefaultSampleRate;
        if (outIsDefault is not null) *outIsDefault = d.IsDefault ? 1 : 0;
        WriteUtf8(d.Id, idBuffer, idLen);
        WriteUtf8(d.Name, nameBuffer, nameLen);
        return Ok;
    }

    /// <summary>Snapshots and returns a backend's input device count (null/empty backend = default). Negative on error.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_input_device_count")]
    public static int AudioInputDeviceCount(byte* backendName)
    {
        var backend = ResolveBackend(backendName);
        if (backend is null)
        {
            _backendInputDevices = null;
            return Fail("no such audio backend", ErrInvalidArg);
        }

        try
        {
            _backendInputDevices = [.. backend.EnumerateInputDevices()];
            return _backendInputDevices.Length;
        }
        catch (Exception ex)
        {
            _backendInputDevices = null;
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>
    /// Reads the input device at <paramref name="index"/> from the last <c>mfp_audio_input_device_count</c>
    /// snapshot. Writes the opaque id (pass to <c>mfp_audio_input_create</c>) and name.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_input_device_get")]
    public static int AudioInputDeviceGet(
        int index, int* outMaxChannels, double* outDefaultSampleRate, int* outIsDefault,
        byte* idBuffer, int idLen, byte* nameBuffer, int nameLen)
    {
        var devices = _backendInputDevices;
        if (devices is null)
            return Fail("call mfp_audio_input_device_count first", ErrInvalidArg);
        if ((uint)index >= (uint)devices.Length)
            return Fail("device index out of range", ErrInvalidArg);

        var d = devices[index];
        if (outMaxChannels is not null) *outMaxChannels = d.MaxChannels;
        if (outDefaultSampleRate is not null) *outDefaultSampleRate = d.DefaultSampleRate;
        if (outIsDefault is not null) *outIsDefault = d.IsDefault ? 1 : 0;
        WriteUtf8(d.Id, idBuffer, idLen);
        WriteUtf8(d.Name, nameBuffer, nameLen);
        return Ok;
    }

    private static IAudioBackend? ResolveBackend(byte* backendName)
    {
        var name = Utf8(backendName);
        if (string.IsNullOrEmpty(name))
            return AudioBackends.Default;
        return AudioBackends.TryGet(name, out var backend) ? backend : null;
    }
}
