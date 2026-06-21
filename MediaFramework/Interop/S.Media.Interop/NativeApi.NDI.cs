using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Audio;
using S.Media.NDI;
using S.Media.NDI.Video;

namespace S.Media.Interop;

/// <summary>
/// NDI-specific C ABI building blocks. NDI is optional and initialized lazily by these calls, so
/// <c>mfp_initialize</c> remains usable on machines without the NDI runtime installed.
/// </summary>
internal static unsafe partial class NativeApi
{
    [ThreadStatic] private static NDIDiscoveredSource[]? _ndiSources;

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_runtime_is_available")]
    public static int NdiRuntimeIsAvailable()
    {
        try
        {
            if (!NDIRuntime.IsSupportedCpu())
                return 0;

            var rc = NDIRuntime.Create(out var runtime);
            runtime?.Dispose();
            return rc == 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_runtime_version")]
    public static int NdiRuntimeVersion(byte* buffer, int bufferLen)
    {
        try
        {
            return WriteUtf8(NDIRuntime.Version, buffer, bufferLen);
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_source_count")]
    public static int NdiSourceCount(int timeoutMs, int showLocalSources, byte* groups, byte* extraIps)
    {
        if (timeoutMs < 0)
            return Fail("timeoutMs must be >= 0", ErrInvalidArg);

        try
        {
            _ndiSources = [.. NDISource.Find(
                TimeSpan.FromMilliseconds(timeoutMs),
                new NDIFindOptions
                {
                    ShowLocalSources = showLocalSources != 0,
                    Groups = Utf8(groups),
                    ExtraIps = Utf8(extraIps),
                })];
            return _ndiSources.Length;
        }
        catch (Exception ex)
        {
            _ndiSources = null;
            return Fail(ex, ErrGeneric);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_source_get")]
    public static int NdiSourceGet(int index, byte* nameBuffer, int nameLen, byte* urlBuffer, int urlLen)
    {
        var sources = _ndiSources;
        if (sources is null)
            return Fail("call mfp_ndi_source_count first", ErrInvalidArg);
        if ((uint)index >= (uint)sources.Length)
            return Fail("source index out of range", ErrInvalidArg);

        var source = sources[index];
        WriteUtf8(source.Name, nameBuffer, nameLen);
        WriteUtf8(source.UrlAddress, urlBuffer, urlLen);
        return Ok;
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_source_open")]
    public static int NdiSourceOpen(
        byte* ndiName,
        byte* urlAddress,
        int receiveAudio,
        int receiveVideo,
        byte* receiverName,
        int bandwidth,
        int colorFormat,
        int maxQueuedVideoFrames,
        IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;

        var name = Utf8(ndiName);
        if (string.IsNullOrWhiteSpace(name))
            return Fail("ndiName is required", ErrInvalidArg);
        if (receiveAudio == 0 && receiveVideo == 0)
            return Fail("at least one of receiveAudio or receiveVideo must be enabled", ErrInvalidArg);
        if (!IsValidNdiBandwidth(bandwidth))
            return Fail("invalid NDI bandwidth value", ErrInvalidArg);
        if (!IsValidNdiColorFormat(colorFormat))
            return Fail("invalid NDI color format value", ErrInvalidArg);

        try
        {
            var source = NDISource.Open(
                new NDIDiscoveredSource(name, Utf8(urlAddress)),
                new NDISourceOptions
                {
                    ReceiveAudio = receiveAudio != 0,
                    ReceiveVideo = receiveVideo != 0,
                    ReceiverName = Utf8(receiverName),
                    Bandwidth = (NDIRecvBandwidth)bandwidth,
                    ColorFormat = (NDIRecvColorFormat)colorFormat,
                    MaxQueuedVideoFrames = maxQueuedVideoFrames > 0 ? maxQueuedVideoFrames : 8,
                });
            *outHandle = Handles.Alloc(source);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrOpenFailed);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_source_destroy")]
    public static void NdiSourceDestroy(IntPtr source) => Handles.Free(source, dispose: true);

    [UnmanagedCallersOnly(EntryPoint = "mfp_player_open_live_ndi")]
    public static int PlayerOpenLiveNdi(IntPtr sourceHandle, IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;
        if (Volatile.Read(ref _initialized) == 0)
            return Fail("mfp_initialize has not been called", ErrNotInitialized);

        var source = Handles.Take<NDISource>(sourceHandle);
        if (source is null)
            return Fail("invalid NDI source handle", ErrInvalidHandle);

        try
        {
            var audio = source.ReceiveAudio ? source.Audio : null;
            var video = source.ReceiveVideo ? source.Video : null;
            if (!PlayerInstance.TryOpenLive(audio, video, out var instance, out var error) || instance is null)
                return Fail(error ?? "open failed", ErrOpenFailed);

            *outHandle = Handles.Alloc(instance);
            return Ok;
        }
        catch (Exception ex)
        {
            source.Dispose();
            return Fail(ex, ErrOpenFailed);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_output_create")]
    public static int NdiOutputCreate(
        byte* sourceName,
        byte* groups,
        int clockVideo,
        int clockAudio,
        int videoTimecodeMode,
        IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;

        var name = Utf8(sourceName);
        if (string.IsNullOrWhiteSpace(name))
            return Fail("sourceName is required", ErrInvalidArg);
        if (!IsValidNdiVideoTimecodeMode(videoTimecodeMode))
            return Fail("invalid NDI video timecode mode", ErrInvalidArg);

        try
        {
            var output = new NDIOutput(
                name,
                Utf8(groups),
                clockVideo: clockVideo != 0,
                clockAudio: clockAudio != 0,
                videoTimecodeMode: (NDIVideoTimecodeMode)videoTimecodeMode);
            *outHandle = Handles.Alloc(output);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_output_video")]
    public static int NdiOutputVideo(IntPtr ndiOutput, IntPtr* outOutput)
    {
        if (outOutput is null)
            return Fail("outOutput is null", ErrInvalidArg);
        *outOutput = IntPtr.Zero;

        var output = Handles.Resolve<NDIOutput>(ndiOutput);
        if (output is null)
            return Fail("invalid NDI output handle", ErrInvalidHandle);

        try
        {
            *outOutput = Handles.Alloc(output.Video);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_output_audio")]
    public static int NdiOutputAudio(IntPtr ndiOutput, int sampleRate, int channels, IntPtr* outOutput)
    {
        if (outOutput is null)
            return Fail("outOutput is null", ErrInvalidArg);
        *outOutput = IntPtr.Zero;
        if (sampleRate <= 0 || channels <= 0)
            return Fail("sampleRate and channels must be > 0", ErrInvalidArg);

        var output = Handles.Resolve<NDIOutput>(ndiOutput);
        if (output is null)
            return Fail("invalid NDI output handle", ErrInvalidHandle);

        try
        {
            *outOutput = Handles.Alloc(output.EnableAudio(new AudioFormat(sampleRate, channels)));
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_output_connection_count")]
    public static int NdiOutputConnectionCount(IntPtr ndiOutput, int timeoutMs)
    {
        if (timeoutMs < 0)
            return Fail("timeoutMs must be >= 0", ErrInvalidArg);

        var output = Handles.Resolve<NDIOutput>(ndiOutput);
        if (output is null)
            return Fail("invalid NDI output handle", ErrInvalidHandle);

        try
        {
            return output.GetReceiverConnectionCount((uint)timeoutMs);
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_ndi_output_destroy")]
    public static void NdiOutputDestroy(IntPtr ndiOutput) => Handles.Free(ndiOutput, dispose: true);

    private static bool IsValidNdiBandwidth(int value) =>
        value is (int)NDIRecvBandwidth.MetadataOnly
            or (int)NDIRecvBandwidth.AudioOnly
            or (int)NDIRecvBandwidth.Lowest
            or (int)NDIRecvBandwidth.Highest;

    private static bool IsValidNdiColorFormat(int value) =>
        value is (int)NDIRecvColorFormat.BgrxBgra
            or (int)NDIRecvColorFormat.UyvyBgra
            or (int)NDIRecvColorFormat.RgbxRgba
            or (int)NDIRecvColorFormat.UyvyRgba
            or (int)NDIRecvColorFormat.Fastest
            or (int)NDIRecvColorFormat.Best;

    private static bool IsValidNdiVideoTimecodeMode(int value) =>
        value is (int)NDIVideoTimecodeMode.Synthesize
            or (int)NDIVideoTimecodeMode.PresentationRelativeTicks
            or (int)NDIVideoTimecodeMode.MuxerPresentationTicks
            or (int)NDIVideoTimecodeMode.SmpteFromFrame;
}
