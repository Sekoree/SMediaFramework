using System.Runtime.InteropServices;
using System.Text;
using PMLib.Types;

namespace PMLib;

/// <summary>
/// Static utility helpers for common PortMidi operations.
/// These are the primary public API surface for PortMidi — <c>Native</c> is internal.
/// </summary>
public static class PMUtil
{
    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the PortMidi library and scans for available devices.
    /// Must be called before any other <c>Pm_*</c> function.
    /// </summary>
    public static PmError Initialize() => Native.Pm_Initialize();

    /// <summary>
    /// Terminates the PortMidi library. Call when you are finished with PortMidi.
    /// All streams must be closed before calling this.
    /// </summary>
    public static PmError Terminate() => Native.Pm_Terminate();

    // ── Device enumeration ────────────────────────────────────────────────────

    /// <summary>Returns the total number of MIDI devices currently known to PortMidi.</summary>
    public static int CountDevices() => Native.Pm_CountDevices();

    /// <summary>
    /// Returns a low-level copy of the <see cref="PmDeviceInfo"/> for device <paramref name="id"/>,
    /// or <see langword="null"/> if the ID is out of range.
    /// <para>
    /// <strong>Warning:</strong> <see cref="PmDeviceInfo.Name"/> and <see cref="PmDeviceInfo.Interf"/>
    /// call into native memory on every access and are only safe until <see cref="Terminate"/> is
    /// called. Prefer <see cref="GetDeviceEntry"/> for any value you need to store or inspect later.
    /// </para>
    /// </summary>
    public static PmDeviceInfo? GetDeviceInfo(int id)
    {
        var ptr = Native.Pm_GetDeviceInfo(id);
        return ptr == nint.Zero ? null : Marshal.PtrToStructure<PmDeviceInfo>(ptr);
    }

    /// <summary>
    /// Returns a fully managed <see cref="PmDeviceEntry"/> snapshot for device <paramref name="id"/>,
    /// or <see langword="null"/> if the ID is out of range.
    /// All string fields are eagerly copied and remain valid after <see cref="Terminate"/>.
    /// </summary>
    public static PmDeviceEntry? GetDeviceEntry(int id) => PmDeviceEntry.TryFromNative(id);

    /// <summary>
    /// Returns all known MIDI devices as an eagerly-materialised, fully managed list.
    /// String fields are copied from native memory immediately — safe to access after
    /// <see cref="Terminate"/>.
    /// </summary>
    public static IReadOnlyList<PmDeviceEntry> GetAllDevices()
    {
        var count = Native.Pm_CountDevices();
        var result = new List<PmDeviceEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = PmDeviceEntry.TryFromNative(i);
            if (entry.HasValue) result.Add(entry.Value);
        }
        return result;
    }

    /// <summary>Returns only devices that support MIDI input, eagerly materialised.</summary>
    public static IReadOnlyList<PmDeviceEntry> GetInputDevices()
    {
        var all = GetAllDevices();
        var result = new List<PmDeviceEntry>(all.Count);
        foreach (var d in all)
            if (d.IsInput) result.Add(d);
        return result;
    }

    /// <summary>Returns only devices that support MIDI output, eagerly materialised.</summary>
    public static IReadOnlyList<PmDeviceEntry> GetOutputDevices()
    {
        var all = GetAllDevices();
        var result = new List<PmDeviceEntry>(all.Count);
        foreach (var d in all)
            if (d.IsOutput) result.Add(d);
        return result;
    }

    // ── Stream lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Opens a MIDI input stream with the given <paramref name="bufferSize"/>.
    /// Uses the default PortTime clock and no sys-dep info.
    /// </summary>
    public static PmError OpenInput(out nint stream, int deviceId, int bufferSize = 256)
        => Native.Pm_OpenInput(out stream, deviceId,
            inputSysDepInfo: nint.Zero,
            bufferSize: bufferSize,
            timeProc: nint.Zero,
            timeInfo: nint.Zero);

    /// <summary>
    /// Opens a MIDI output stream. <paramref name="latency"/> = 0 ignores timestamps.
    /// Uses the default PortTime clock and no sys-dep info.
    /// </summary>
    public static PmError OpenOutput(out nint stream, int deviceId, int bufferSize = 256, int latency = 0)
        => Native.Pm_OpenOutput(out stream, deviceId,
            outputSysDepInfo: nint.Zero,
            bufferSize: bufferSize,
            timeProc: nint.Zero,
            timeInfo: nint.Zero,
            latency: latency);

    /// <summary>Closes a MIDI stream and flushes any pending output buffers where possible.</summary>
    public static PmError Close(nint stream) => Native.Pm_Close(stream);

    // ── I/O ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads up to <paramref name="length"/> MIDI events from an input stream.
    /// Returns the number of events read (≥ 0), or a negative <see cref="PmError"/> on error.
    /// </summary>
    public static int Read(nint stream, Span<PmEvent> buffer, int length)
        => Native.Pm_Read(stream, buffer, length);

    /// <summary>Writes a single packed short MIDI message to an output stream.</summary>
    public static PmError WriteShort(nint stream, int timestamp, uint message)
        => Native.Pm_WriteShort(stream, timestamp, message);

    // ── Stream configuration ──────────────────────────────────────────────────

    /// <summary>
    /// Sets message-type filters on an open input stream.
    /// Filtered message types are silently discarded.
    /// </summary>
    public static PmError SetFilter(nint stream, PmFilter filters)
        => Native.Pm_SetFilter(stream, filters);

    /// <summary>
    /// Sets a 16-bit channel mask on an input stream.
    /// OR multiple <see cref="ChannelMask"/> values to allow several channels.
    /// </summary>
    public static PmError SetChannelMask(nint stream, int mask)
        => Native.Pm_SetChannelMask(stream, mask);

    // ── Error text ────────────────────────────────────────────────────────────

    /// <summary>Returns a managed <see cref="string"/> describing <paramref name="error"/>.</summary>
    public static string? GetErrorText(PmError error)
        => Marshal.PtrToStringUTF8(Native.Pm_GetErrorText(error));

    /// <summary>
    /// Returns and clears the pending host-level error as a managed <see cref="string"/>.
    /// Returns an empty string if there is no pending error.
    /// </summary>
    public static string GetHostErrorText()
    {
        Span<byte> buffer = stackalloc byte[256]; // PM_HOST_ERROR_MSG_LEN
        Native.Pm_GetHostErrorText(buffer, (uint)buffer.Length);
        var nullIdx = buffer.IndexOf((byte)0);
        var slice = nullIdx >= 0 ? buffer[..nullIdx] : buffer;
        return Encoding.UTF8.GetString(slice);
    }

    // ── Channel mask ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a bitmask for a single MIDI channel (0–15) for use with
    /// <see cref="SetChannelMask"/>.
    /// OR multiple calls together to allow several channels simultaneously.
    /// </summary>
    /// <example>
    /// <code>
    /// // Allow only channels 1 and 10 (0-indexed: 0 and 9)
    /// PMUtil.SetChannelMask(stream, PMUtil.ChannelMask(0) | PMUtil.ChannelMask(9));
    /// </code>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="channel"/> is not in the range 0–15.
    /// </exception>
    public static int ChannelMask(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 15);
        return 1 << channel;
    }
}
