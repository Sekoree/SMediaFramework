using Microsoft.Extensions.Logging;
using PMLib.MessageTypes;
using PMLib.Runtime;
using PMLib.Types;

namespace PMLib.Devices;

/// <summary>
/// A PortMidi output device. Call <see cref="MIDIDevice.Open"/> before writing,
/// and <see cref="MIDIDevice.Dispose"/> when finished.
/// </summary>
public class MIDIOutputDevice : MIDIDevice
{
    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Number of output events to buffer. Must be set before <see cref="Open"/>. Default: 256.
    /// </summary>
    public int BufferSize { get; set; } = 256;

    /// <summary>
    /// Output latency in milliseconds added to message timestamps.
    /// <c>0</c> ignores timestamps and delivers messages immediately.
    /// Must be set before <see cref="Open"/>. Default: 0.
    /// </summary>
    public int Latency { get; set; } = 0;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public MIDIOutputDevice(int deviceId) : base(deviceId) { }

    /// <summary>Opens the output stream.</summary>
    public override PmError Open()
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("MIDIOutputDevice.Open() (deviceId={DeviceId}, name={Name}, bufferSize={BufferSize}, latency={Latency})",
                DeviceId, Name, BufferSize, Latency);

        var err = Native.Pm_OpenOutput(
            out Stream, DeviceId,
            outputSysDepInfo: nint.Zero,
            bufferSize: BufferSize,
            timeProc: nint.Zero,
            timeInfo: nint.Zero,
            latency: Latency);

        if (err != PmError.NoError)
            Logger.LogWarning("MIDIOutputDevice.Open() failed: {Error} (deviceId={DeviceId}, name={Name})",
                err, DeviceId, Name);

        return err;
    }

    public override PmError Close()
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("MIDIOutputDevice.Close() (deviceId={DeviceId}, name={Name})", DeviceId, Name);

        return base.Close();
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes any <see cref="IMIDIMessage"/> to the output stream.
    /// For 14-bit <see cref="ControlChange"/> this sends two consecutive short messages.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="timestamp">
    /// Timestamp in milliseconds. Ignored when <see cref="Latency"/> is 0.
    /// </param>
    public PmError Write(IMIDIMessage message, int timestamp = 0)
    {
        if (!IsOpen) return PmError.BadPtr;
        return message.WriteTo(Stream, timestamp);
    }

    /// <summary>
    /// Writes a batch of <see cref="PmEvent"/> structures directly. Useful for bulk output
    /// or forwarding events read from an input device.
    /// </summary>
    public PmError Write(ReadOnlySpan<PmEvent> events)
    {
        if (!IsOpen) return PmError.BadPtr;
        return Native.Pm_Write(Stream, events, events.Length);
    }

    /// <summary>Writes a packed short MIDI message directly.</summary>
    /// <param name="message">Packed message; use <see cref="PmEvent.CreateMessage"/>.</param>
    /// <param name="timestamp">Timestamp in milliseconds.</param>
    public PmError WriteShort(uint message, int timestamp = 0)
    {
        if (!IsOpen) return PmError.BadPtr;
        return Native.Pm_WriteShort(Stream, timestamp, message);
    }

    /// <summary>
    /// Writes a SysEx message. <paramref name="data"/> must include the opening
    /// <c>0xF0</c> and closing <c>0xF7</c> (EOX) bytes.
    /// </summary>
    public PmError WriteSysEx(ReadOnlySpan<byte> data, int timestamp = 0)
    {
        if (!IsOpen) return PmError.BadPtr;
        return Native.Pm_WriteSysEx(Stream, timestamp, data);
    }

    /// <summary>
    /// Immediately terminates all pending output. Call <see cref="MIDIDevice.Close"/>
    /// immediately after.
    /// </summary>
    public PmError Abort() => IsOpen ? Native.Pm_Abort(Stream) : PmError.BadPtr;

    /// <summary>
    /// Re-synchronises the stream to the time procedure.
    /// Call this before sending the first non-zero-timestamp message after
    /// the time source starts advancing.
    /// </summary>
    public PmError Synchronize() => IsOpen ? Native.Pm_Synchronize(Stream) : PmError.BadPtr;
}