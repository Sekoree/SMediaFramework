using System.Runtime.InteropServices;
using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Interop;

/// <summary>
/// Router wiring: add host-created outputs to a player's video/audio router and connect routes. Output ids
/// are auto-generated and written back (UTF-8, NUL-terminated) into the caller's buffer; pass a null buffer
/// to query the needed length. Outputs are attached non-owning (see <c>NativeApi.Outputs</c>).
/// </summary>
internal static unsafe partial class NativeApi
{
    // --- video router ----------------------------------------------------------------------------

    /// <summary>Adds <paramref name="output"/> to the video router; writes the new output id to <paramref name="outIdBuffer"/>.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_video_router_add_output")]
    public static int VideoRouterAddOutput(IntPtr router, IntPtr output, byte* outIdBuffer, int idBufferLen)
    {
        var r = Handles.Resolve<VideoRouter>(router);
        var o = Handles.Resolve<IVideoOutput>(output);
        if (r is null)
            return Fail("invalid video router handle", ErrInvalidHandle);
        if (o is null)
            return Fail("invalid video output handle", ErrInvalidHandle);

        try
        {
            var id = r.AddOutput(o, id: null, disposeOutputOnRouterDispose: false);
            WriteUtf8(id, outIdBuffer, idBufferLen);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Routes the player's video input (see <c>mfp_player_video_input_id</c>) to an output.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_video_router_add_route")]
    public static int VideoRouterAddRoute(IntPtr router, byte* inputId, byte* outputId)
    {
        var r = Handles.Resolve<VideoRouter>(router);
        if (r is null)
            return Fail("invalid video router handle", ErrInvalidHandle);
        var inId = Utf8(inputId);
        var outId = Utf8(outputId);
        if (string.IsNullOrEmpty(inId) || string.IsNullOrEmpty(outId))
            return Fail("inputId and outputId are required", ErrInvalidArg);

        try
        {
            return r.TryAddRoute(inId, outId, out var err) ? Ok : Fail(err ?? "TryAddRoute failed", ErrGeneric);
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Removes a video output (and its routes) from the router. Does not free the output handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_video_router_remove_output")]
    public static int VideoRouterRemoveOutput(IntPtr router, byte* outputId)
    {
        var r = Handles.Resolve<VideoRouter>(router);
        if (r is null)
            return Fail("invalid video router handle", ErrInvalidHandle);
        var id = Utf8(outputId);
        if (string.IsNullOrEmpty(id))
            return Fail("outputId is required", ErrInvalidArg);

        try
        {
            r.RemoveOutput(id);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    // --- audio router ----------------------------------------------------------------------------

    /// <summary>The audio router's nominal mix sample rate (Hz); create PortAudio outputs at this rate. -1 on a bad handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_router_sample_rate")]
    public static int AudioRouterSampleRate(IntPtr router) =>
        Handles.Resolve<AudioRouter>(router)?.SampleRate ?? -1;

    /// <summary>Adds <paramref name="output"/> to the audio router; writes the new output id to <paramref name="outIdBuffer"/>.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_router_add_output")]
    public static int AudioRouterAddOutput(IntPtr router, IntPtr output, byte* outIdBuffer, int idBufferLen)
    {
        var r = Handles.Resolve<AudioRouter>(router);
        var o = Handles.Resolve<IAudioOutput>(output);
        if (r is null)
            return Fail("invalid audio router handle", ErrInvalidHandle);
        if (o is null)
            return Fail("invalid audio output handle", ErrInvalidHandle);

        try
        {
            var id = r.AddOutput(o);
            WriteUtf8(id, outIdBuffer, idBufferLen);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Connects the player's audio source (see <c>mfp_player_audio_source_id</c>) to an output at
    /// <paramref name="gain"/> (1.0 = unity), with an identity channel map sized to the output.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_router_connect")]
    public static int AudioRouterConnect(IntPtr router, byte* sourceId, byte* outputId, float gain)
    {
        var r = Handles.Resolve<AudioRouter>(router);
        if (r is null)
            return Fail("invalid audio router handle", ErrInvalidHandle);
        var sId = Utf8(sourceId);
        var oId = Utf8(outputId);
        if (string.IsNullOrEmpty(sId) || string.IsNullOrEmpty(oId))
            return Fail("sourceId and outputId are required", ErrInvalidArg);

        try
        {
            r.Connect(sId, oId, map: null, gain: gain);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Adjusts the gain of an existing source→output route (1.0 = unity).</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_router_set_gain")]
    public static int AudioRouterSetGain(IntPtr router, byte* sourceId, byte* outputId, float gain)
    {
        var r = Handles.Resolve<AudioRouter>(router);
        if (r is null)
            return Fail("invalid audio router handle", ErrInvalidHandle);
        var sId = Utf8(sourceId);
        var oId = Utf8(outputId);
        if (string.IsNullOrEmpty(sId) || string.IsNullOrEmpty(oId))
            return Fail("sourceId and outputId are required", ErrInvalidArg);

        try
        {
            r.SetRouteGain(sId, oId, gain);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Removes an audio output (and its routes) from the router. Does not free the output handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_audio_router_remove_output")]
    public static int AudioRouterRemoveOutput(IntPtr router, byte* outputId)
    {
        var r = Handles.Resolve<AudioRouter>(router);
        if (r is null)
            return Fail("invalid audio router handle", ErrInvalidHandle);
        var id = Utf8(outputId);
        if (string.IsNullOrEmpty(id))
            return Fail("outputId is required", ErrInvalidArg);

        try
        {
            r.RemoveOutput(id);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }
}
