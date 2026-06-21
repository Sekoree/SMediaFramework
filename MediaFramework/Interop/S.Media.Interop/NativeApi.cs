using System.Runtime.InteropServices;
using System.Text;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.PortAudio;

namespace S.Media.Interop;

/// <summary>
/// The C ABI of the media framework. Every entry point is a <see cref="UnmanagedCallersOnlyAttribute"/>
/// static with a blittable signature so it exports as a plain C symbol from the NativeAOT shared library
/// (<c>s_media_player.so</c> / <c>s_media_player.dll</c>). The matching declarations live in
/// <c>include/s_media_player.h</c>.
/// </summary>
/// <remarks>
/// Contract for callers in any language:
/// <list type="bullet">
/// <item>Call <c>mfp_initialize</c> once before anything else and <c>mfp_shutdown</c> once at the end.</item>
/// <item>Player handles are opaque pointers. Every successful <c>mfp_open_file</c> must be matched by a
/// <c>mfp_close</c>; calling any function with a closed/garbage handle returns an error, it does not crash.</item>
/// <item>Functions never throw across the boundary — they return an <c>int</c> status (0 = OK, negative =
/// error) and stash a human-readable message retrievable with <c>mfp_last_error</c> (thread-local).</item>
/// <item>Time is in 100-ns ticks (<see cref="TimeSpan.Ticks"/>): 10,000,000 ticks = 1 second.</item>
/// </list>
/// </remarks>
internal static unsafe class NativeApi
{
    // --- status codes (part of the ABI; append only) ---------------------------------------------
    private const int Ok = 0;
    private const int ErrGeneric = -1;
    private const int ErrInvalidArg = -2;
    private const int ErrInvalidHandle = -3;
    private const int ErrOpenFailed = -4;
    private const int ErrNotInitialized = -5;

    /// <summary>Sentinel device indices for <c>mfp_open_file</c>'s <c>audio_device_index</c> parameter.</summary>
    internal const int DefaultAudioDevice = -1;
    internal const int NoAudioDevice = -2;

    [ThreadStatic] private static string? _lastError;
    private static int _initialized;

    // --- lifecycle -------------------------------------------------------------------------------

    /// <summary>Initializes FFmpeg + PortAudio (idempotent). Returns 0 on success.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_initialize")]
    public static int Initialize()
    {
        try
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 0)
                MediaFrameworkRuntime.Init().UseFFmpeg().UsePortAudio();
            return Ok;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _initialized, 0);
            return Fail(ex, ErrGeneric);
        }
    }

    /// <summary>Releases framework runtimes. Close all players first.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_shutdown")]
    public static void Shutdown()
    {
        try
        {
            if (Interlocked.Exchange(ref _initialized, 0) == 1)
                MediaFrameworkRuntime.Shutdown();
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "S.Media.Interop.Shutdown");
        }
    }

    // --- open / close ----------------------------------------------------------------------------

    /// <summary>
    /// Opens a local media file. <paramref name="withVideoWindow"/> != 0 opens an SDL video window when the
    /// file has video; <paramref name="audioDeviceIndex"/> selects a PortAudio device (-1 = default,
    /// -2 = no audio). On success writes an opaque handle to <paramref name="outHandle"/> and returns 0.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_open_file")]
    public static int OpenFile(byte* utf8Path, int withVideoWindow, int audioDeviceIndex, IntPtr* outHandle)
    {
        if (outHandle is null)
            return Fail("outHandle is null", ErrInvalidArg);
        *outHandle = IntPtr.Zero;

        if (Volatile.Read(ref _initialized) == 0)
            return Fail("mfp_initialize has not been called", ErrNotInitialized);
        if (utf8Path is null)
            return Fail("path is null", ErrInvalidArg);

        var path = Marshal.PtrToStringUTF8((IntPtr)utf8Path);
        if (string.IsNullOrEmpty(path))
            return Fail("path is empty", ErrInvalidArg);

        try
        {
            if (!PlayerInstance.TryOpen(path, withVideoWindow != 0, audioDeviceIndex, out var instance, out var error)
                || instance is null)
            {
                return Fail(error ?? "open failed", ErrOpenFailed);
            }

            *outHandle = GCHandle.ToIntPtr(GCHandle.Alloc(instance));
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrOpenFailed);
        }
    }

    /// <summary>Closes a player handle and frees its resources. Safe to call with a null handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_close")]
    public static void Close(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;
        try
        {
            var gch = GCHandle.FromIntPtr(handle);
            if (!gch.IsAllocated)
                return;
            (gch.Target as PlayerInstance)?.Dispose();
            gch.Free();
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "S.Media.Interop.Close");
        }
    }

    // --- transport -------------------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_play")]
    public static int Play(IntPtr handle) => Invoke(handle, static p => p.Play());

    [UnmanagedCallersOnly(EntryPoint = "mfp_pause")]
    public static int Pause(IntPtr handle) => Invoke(handle, static p => p.Pause());

    [UnmanagedCallersOnly(EntryPoint = "mfp_seek")]
    public static int Seek(IntPtr handle, long positionTicks)
    {
        if (positionTicks < 0)
            return Fail("positionTicks must be >= 0", ErrInvalidArg);
        return Invoke(handle, p => p.Seek(TimeSpan.FromTicks(positionTicks)));
    }

    // --- queries (return the value directly; sentinel on bad handle) -----------------------------

    /// <summary>Current playhead in ticks, or -1 on an invalid handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_get_position_ticks")]
    public static long GetPositionTicks(IntPtr handle) =>
        Resolve(handle) is { } p ? p.PositionTicks : -1L;

    /// <summary>Media duration in ticks (0 for live / unknown), or -1 on an invalid handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_get_duration_ticks")]
    public static long GetDurationTicks(IntPtr handle) =>
        Resolve(handle) is { } p ? p.DurationTicks : -1L;

    /// <summary>Playback state (see <see cref="PlayerState"/>), or -1 on an invalid handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_get_state")]
    public static int GetState(IntPtr handle) =>
        Resolve(handle) is { } p ? (int)p.State : -1;

    /// <summary>1 when the media has played to its end, 0 otherwise, -1 on an invalid handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_is_ended")]
    public static int IsEnded(IntPtr handle) =>
        Resolve(handle) is { } p ? (p.IsEnded ? 1 : 0) : -1;

    // --- diagnostics -----------------------------------------------------------------------------

    /// <summary>
    /// Copies the calling thread's last error message (UTF-8, NUL-terminated) into <paramref name="buffer"/>
    /// and returns the byte length that was needed (excluding the NUL). Pass a null buffer / 0 length to
    /// query the required size first. Returns 0 when there is no error.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_last_error")]
    public static int LastError(byte* buffer, int bufferLen)
    {
        var msg = _lastError;
        if (string.IsNullOrEmpty(msg))
        {
            if (buffer is not null && bufferLen > 0)
                buffer[0] = 0;
            return 0;
        }

        var encoded = Encoding.UTF8.GetBytes(msg);
        if (buffer is null || bufferLen <= 0)
            return encoded.Length;

        // Copy as much as fits, always leaving room for the NUL terminator. A truncated message may clip a
        // multi-byte sequence at the very end, which is harmless for a NUL-terminated diagnostic string.
        var copy = Math.Min(encoded.Length, bufferLen - 1);
        encoded.AsSpan(0, copy).CopyTo(new Span<byte>(buffer, copy));
        buffer[copy] = 0;
        return encoded.Length;
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static int Invoke(IntPtr handle, Action<PlayerInstance> action)
    {
        var instance = Resolve(handle);
        if (instance is null)
            return Fail("invalid player handle", ErrInvalidHandle);
        try
        {
            action(instance);
            return Ok;
        }
        catch (Exception ex)
        {
            return Fail(ex, ErrGeneric);
        }
    }

    private static PlayerInstance? Resolve(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var gch = GCHandle.FromIntPtr(handle);
            return gch.IsAllocated ? gch.Target as PlayerInstance : null;
        }
        catch
        {
            return null;
        }
    }

    private static int Fail(string message, int code)
    {
        _lastError = message;
        return code;
    }

    private static int Fail(Exception ex, int code)
    {
        _lastError = ex.Message;
        MediaDiagnostics.LogError(ex, "S.Media.Interop");
        return code;
    }
}
