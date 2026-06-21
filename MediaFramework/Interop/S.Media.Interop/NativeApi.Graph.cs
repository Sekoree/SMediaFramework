using System.Runtime.InteropServices;
using System.Text;

namespace S.Media.Interop;

/// <summary>
/// Graph-level player surface: open a player WITHOUT auto-wiring outputs, reach its routers, and receive
/// events. The host then composes its own pipeline via <c>NativeApi.Routing</c> / <c>NativeApi.Outputs</c>,
/// the same way HaPlay does. (The committed <c>mfp_open_file</c> remains the batteries-included shortcut.)
/// </summary>
internal static unsafe partial class NativeApi
{
    // --- event types (ABI; append only). Delivered to the callback registered with
    //     mfp_player_set_event_callback. The callback runs on a framework thread — marshal to your own. ---
    internal const int EventPosition = 0; // arg = playhead in ticks
    internal const int EventEnded = 1;    // arg = 0
    internal const int EventFaulted = 2;  // arg = 0

    private delegate bool OpenDelegate(string arg, out PlayerInstance? instance, out string? error);

    // --- open (graph mode: no outputs wired) -----------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_player_open_file")]
    public static int PlayerOpenFile(byte* utf8Path, IntPtr* outHandle) =>
        OpenGraph(outHandle, utf8Path, PlayerInstance.TryOpenFile);

    [UnmanagedCallersOnly(EntryPoint = "mfp_player_open_uri")]
    public static int PlayerOpenUri(byte* utf8Uri, IntPtr* outHandle) =>
        OpenGraph(outHandle, utf8Uri, PlayerInstance.TryOpenUri);

    [UnmanagedCallersOnly(EntryPoint = "mfp_player_open_stream")]
    public static int PlayerOpenStream(byte* data, int length, IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;
        if (Volatile.Read(ref _initialized) == 0)
            return Fail("mfp_initialize has not been called", ErrNotInitialized);
        if (data is null || length <= 0)
            return Fail("data is null or length <= 0", ErrInvalidArg);

        try
        {
            var span = new ReadOnlySpan<byte>(data, length);
            if (!PlayerInstance.TryOpenStream(span, out var instance, out var error) || instance is null)
                return Fail(error ?? "open failed", ErrOpenFailed);
            *outHandle = Handles.Alloc(instance);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrOpenFailed);
        }
    }

    private static int OpenGraph(IntPtr* outHandle, byte* utf8Arg, OpenDelegate open)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;
        if (Volatile.Read(ref _initialized) == 0)
            return Fail("mfp_initialize has not been called", ErrNotInitialized);

        var arg = Utf8(utf8Arg);
        if (string.IsNullOrEmpty(arg))
            return Fail("argument is null or empty", ErrInvalidArg);

        try
        {
            if (!open(arg, out var instance, out var error) || instance is null)
                return Fail(error ?? "open failed", ErrOpenFailed);
            *outHandle = Handles.Alloc(instance);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrOpenFailed);
        }
    }

    // --- graph accessors -------------------------------------------------------------------------

    /// <summary>The player's video router handle (valid until the player is closed), or zero on a bad handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_player_video_router")]
    public static IntPtr PlayerVideoRouter(IntPtr player) =>
        Handles.Resolve<PlayerInstance>(player)?.GetVideoRouterHandle() ?? IntPtr.Zero;

    /// <summary>The player's audio router handle, or zero when the source has no audio / on a bad handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_player_audio_router")]
    public static IntPtr PlayerAudioRouter(IntPtr player) =>
        Handles.Resolve<PlayerInstance>(player)?.GetAudioRouterHandle() ?? IntPtr.Zero;

    /// <summary>Copies the video router input id (route video outputs to it). See <c>mfp_last_error</c>-style copy.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_player_video_input_id")]
    public static int PlayerVideoInputId(IntPtr player, byte* buffer, int bufferLen) =>
        WriteUtf8(Handles.Resolve<PlayerInstance>(player)?.VideoRouterInputId, buffer, bufferLen);

    /// <summary>Copies the decoder audio source id (connect audio outputs from it), empty when no audio.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_player_audio_source_id")]
    public static int PlayerAudioSourceId(IntPtr player, byte* buffer, int bufferLen) =>
        WriteUtf8(Handles.Resolve<PlayerInstance>(player)?.AudioSourceId, buffer, bufferLen);

    // --- events ----------------------------------------------------------------------------------

    /// <summary>
    /// Registers an event callback: <c>void cb(mfp_player player, int event_type, int64_t arg, void* user_data)</c>,
    /// cdecl. Pass a null callback to clear. Fires on framework threads (clock / decode), so marshal to your own.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_player_set_event_callback")]
    public static int PlayerSetEventCallback(IntPtr player, IntPtr callback, IntPtr userData)
    {
        var instance = Handles.Resolve<PlayerInstance>(player);
        if (instance is null)
            return Fail("invalid player handle", ErrInvalidHandle);
        instance.SetEventCallback(player, callback, userData);
        return Ok;
    }

    // --- shared helpers (used across the partials) -----------------------------------------------

    private static string? Utf8(byte* utf8) =>
        utf8 is null ? null : Marshal.PtrToStringUTF8((IntPtr)utf8);

    /// <summary>Copies <paramref name="value"/> (UTF-8, NUL-terminated) into <paramref name="buffer"/> and
    /// returns the byte length needed (excluding NUL). Null buffer / 0 length queries the size.</summary>
    private static int WriteUtf8(string? value, byte* buffer, int bufferLen)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (buffer is not null && bufferLen > 0)
                buffer[0] = 0;
            return 0;
        }

        var encoded = Encoding.UTF8.GetBytes(value);
        if (buffer is null || bufferLen <= 0)
            return encoded.Length;

        var copy = Math.Min(encoded.Length, bufferLen - 1);
        encoded.AsSpan(0, copy).CopyTo(new Span<byte>(buffer, copy));
        buffer[copy] = 0;
        return encoded.Length;
    }
}
