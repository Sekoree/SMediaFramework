using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JackLib.Runtime;
using JackLib.Types;

namespace JackLib;

/// <summary>
/// Raw P/Invoke declarations for libjack2.
/// All methods are <see langword="internal"/>; external code should use
/// <see cref="JackClient"/> or another managed wrapper.
/// </summary>
internal static unsafe partial class Native
{
    private const string Lib = JackLibraryNames.Default;

    // ── Version ───────────────────────────────────────────────────────────

    [LibraryImport(Lib, EntryPoint = "jack_get_version_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint jack_get_version_string_raw();

    internal static string? jack_get_version_string()
        => Marshal.PtrToStringUTF8(jack_get_version_string_raw());

    // ── Client open/close ─────────────────────────────────────────────────
    // jack_client_open is variadic — DllImport only (LibraryImport rejects varargs).
    // We expose a non-variadic overload covering the common cases (no server-name arg).

    [DllImport(Lib, EntryPoint = "jack_client_open",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi)]
    private static extern nint jack_client_open_raw(
        string      client_name,
        JackOptions options,
        out JackStatus status);

    internal static nint jack_client_open(string clientName, JackOptions options, out JackStatus status)
        => jack_client_open_raw(clientName, options, out status);

    [LibraryImport(Lib, EntryPoint = "jack_client_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_client_close(nint client);

    [LibraryImport(Lib, EntryPoint = "jack_client_name_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_client_name_size();

    [LibraryImport(Lib, EntryPoint = "jack_get_client_name")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint jack_get_client_name_raw(nint client);

    internal static string? jack_get_client_name(nint client)
        => Marshal.PtrToStringUTF8(jack_get_client_name_raw(client));

    // ── Activation ────────────────────────────────────────────────────────

    [LibraryImport(Lib, EntryPoint = "jack_activate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_activate(nint client);

    [LibraryImport(Lib, EntryPoint = "jack_deactivate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_deactivate(nint client);

    [LibraryImport(Lib, EntryPoint = "jack_is_realtime")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_is_realtime(nint client);

    // ── Callbacks ─────────────────────────────────────────────────────────
    // Callbacks are passed as raw nint (function pointer).
    // The caller (JackClient) keeps the managed delegate alive in a field.

    [LibraryImport(Lib, EntryPoint = "jack_set_process_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_set_process_callback(nint client, nint process_callback, nint arg);

    [LibraryImport(Lib, EntryPoint = "jack_on_shutdown")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void jack_on_shutdown(nint client, nint shutdown_callback, nint arg);

    [LibraryImport(Lib, EntryPoint = "jack_set_buffer_size_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_set_buffer_size_callback(nint client, nint bufsize_callback, nint arg);

    [LibraryImport(Lib, EntryPoint = "jack_set_sample_rate_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_set_sample_rate_callback(nint client, nint srate_callback, nint arg);

    [LibraryImport(Lib, EntryPoint = "jack_set_xrun_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_set_xrun_callback(nint client, nint xrun_callback, nint arg);

    // ── Server / client info ──────────────────────────────────────────────

    [LibraryImport(Lib, EntryPoint = "jack_get_sample_rate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint jack_get_sample_rate(nint client);

    [LibraryImport(Lib, EntryPoint = "jack_get_buffer_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint jack_get_buffer_size(nint client);

    [LibraryImport(Lib, EntryPoint = "jack_cpu_load")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial float jack_cpu_load(nint client);

    // ── Port registration ─────────────────────────────────────────────────
    // flags and buffer_size are C 'unsigned long' (= ulong on Linux LP64).

    [LibraryImport(Lib, EntryPoint = "jack_port_register",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint jack_port_register(
        nint   client,
        string port_name,
        string port_type,
        ulong  flags,
        ulong  buffer_size);

    [LibraryImport(Lib, EntryPoint = "jack_port_unregister")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_port_unregister(nint client, nint port);

    /// <summary>
    /// Returns a pointer to the port's audio buffer for this cycle.
    /// Must only be called from within the process callback.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "jack_port_get_buffer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint jack_port_get_buffer(nint port, uint nframes);

    // ── Port info ─────────────────────────────────────────────────────────

    [LibraryImport(Lib, EntryPoint = "jack_port_name")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint jack_port_name_raw(nint port);
    internal static string? jack_port_name(nint port)
        => Marshal.PtrToStringUTF8(jack_port_name_raw(port));

    [LibraryImport(Lib, EntryPoint = "jack_port_short_name")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint jack_port_short_name_raw(nint port);
    internal static string? jack_port_short_name(nint port)
        => Marshal.PtrToStringUTF8(jack_port_short_name_raw(port));

    [LibraryImport(Lib, EntryPoint = "jack_port_flags")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_port_flags(nint port);

    [LibraryImport(Lib, EntryPoint = "jack_port_type")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint jack_port_type_raw(nint port);
    internal static string? jack_port_type(nint port)
        => Marshal.PtrToStringUTF8(jack_port_type_raw(port));

    [LibraryImport(Lib, EntryPoint = "jack_port_name_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_port_name_size();

    [LibraryImport(Lib, EntryPoint = "jack_port_connected")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_port_connected(nint port);

    [LibraryImport(Lib, EntryPoint = "jack_port_connected_to",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_port_connected_to(nint port, string port_name);

    [LibraryImport(Lib, EntryPoint = "jack_port_by_name",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint jack_port_by_name(nint client, string port_name);

    [LibraryImport(Lib, EntryPoint = "jack_port_by_id")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint jack_port_by_id(nint client, uint port_id);

    [LibraryImport(Lib, EntryPoint = "jack_port_disconnect")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_port_disconnect(nint client, nint port);

    // ── Connections ───────────────────────────────────────────────────────

    [LibraryImport(Lib, EntryPoint = "jack_connect",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_connect(nint client, string source_port, string destination_port);

    [LibraryImport(Lib, EntryPoint = "jack_disconnect",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int jack_disconnect(nint client, string source_port, string destination_port);

    // ── Port search ───────────────────────────────────────────────────────

    // Returns a null-terminated char** that must be freed with jack_free().
    [LibraryImport(Lib, EntryPoint = "jack_get_ports",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint jack_get_ports_raw(
        nint    client,
        string? port_name_pattern,
        string? type_name_pattern,
        ulong   flags);

    /// <summary>
    /// Returns port names matching the given criteria.
    /// Handles <c>jack_free</c> internally; the returned array is fully managed.
    /// </summary>
    internal static string[] jack_get_ports(
        nint          client,
        string?       namePattern,
        string?       typePattern,
        JackPortFlags flags)
    {
        var ptr = jack_get_ports_raw(client, namePattern, typePattern, (ulong)flags);
        if (ptr == nint.Zero) return [];
        try
        {
            var result = new List<string>();
            int i = 0;
            while (true)
            {
                var entryPtr = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                if (entryPtr == nint.Zero) break;
                var s = Marshal.PtrToStringUTF8(entryPtr);
                if (s is not null) result.Add(s);
                i++;
            }
            return result.ToArray();
        }
        finally
        {
            jack_free(ptr);
        }
    }

    // ── Memory ────────────────────────────────────────────────────────────

    [LibraryImport(Lib, EntryPoint = "jack_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void jack_free(nint ptr);

    // ── Time / frame functions ─────────────────────────────────────────────

    [LibraryImport(Lib, EntryPoint = "jack_frames_since_cycle_start")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint jack_frames_since_cycle_start(nint client);

    [LibraryImport(Lib, EntryPoint = "jack_frame_time")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint jack_frame_time(nint client);

    [LibraryImport(Lib, EntryPoint = "jack_last_frame_time")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint jack_last_frame_time(nint client);

    [LibraryImport(Lib, EntryPoint = "jack_frames_to_time")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong jack_frames_to_time(nint client, uint frames);

    [LibraryImport(Lib, EntryPoint = "jack_time_to_frames")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint jack_time_to_frames(nint client, ulong time);

    /// <summary>Returns JACK's current system time in microseconds (monotonic).</summary>
    [LibraryImport(Lib, EntryPoint = "jack_get_time")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong jack_get_time();
}

