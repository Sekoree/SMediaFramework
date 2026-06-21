using System.Runtime.InteropServices;
using S.Media.Core.Audio;
using S.Media.PortAudio;
using S.Media.SDL3;

namespace S.Media.Interop;

/// <summary>
/// Output factories. Each returns an opaque output handle the host OWNS: attach it to a router (which does
/// NOT take ownership), then, after closing the player/removing it from the router, free it with
/// <c>mfp_output_destroy</c>. Destroying an output still routed into a live player is a host bug.
/// </summary>
internal static unsafe partial class NativeApi
{
    /// <summary>
    /// Creates (and starts) a PortAudio output. <paramref name="deviceIndex"/> is a global device index or
    /// <see cref="DefaultAudioDevice"/>. <paramref name="sampleRate"/> must match the audio router it will be
    /// attached to (see <c>mfp_audio_router_sample_rate</c>); the device must support that rate.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_portaudio_output_create")]
    public static int PortAudioOutputCreate(int deviceIndex, int sampleRate, int channels, IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;
        if (sampleRate <= 0 || channels <= 0)
            return Fail("sampleRate and channels must be > 0", ErrInvalidArg);

        try
        {
            var device = deviceIndex == DefaultAudioDevice ? (int?)null : deviceIndex;
            var output = new PortAudioOutput(new AudioFormat(sampleRate, channels), deviceIndex: device);
            output.Start();
            *outHandle = Handles.Alloc(output);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>
    /// Creates an SDL OpenGL video window output (owns its own window + render thread). Subscribe to its
    /// close via the host side; closing the OS window stops presentation. Attach it to a video router.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_sdl_window_output_create")]
    public static int SdlWindowOutputCreate(byte* utf8Title, int width, int height, IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;

        try
        {
            var title = Utf8(utf8Title) ?? "S.Media";
            var output = new SDL3GLVideoOutput(title, width > 0 ? width : 1280, height > 0 ? height : 720);
            *outHandle = Handles.Alloc(output);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Frees a host-created output handle (audio or video) and disposes it. Call only after the
    /// output has been removed from its router / the player is closed.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_output_destroy")]
    public static void OutputDestroy(IntPtr output) => Handles.Free(output, dispose: true);
}
