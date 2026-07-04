using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// MIDI System Exclusive message (status <c>0xF0</c>, terminated by EOX <c>0xF7</c>).
/// </summary>
/// <remarks>
/// <see cref="Data"/> must contain the full, well-formed SysEx byte sequence including
/// the opening <c>0xF0</c> and the closing EOX <c>0xF7</c>.
/// </remarks>
public readonly struct SysEx : IMIDIMessage
{
    /// <summary>
    /// The full SysEx byte array, including the <c>0xF0</c> start byte and the
    /// <c>0xF7</c> EOX terminator.
    /// </summary>
    public byte[] Data { get; }

    public MIDIMessageType MessageType => MIDIMessageType.SysEx;

    /// <param name="data">
    /// Full SysEx bytes, starting with <c>0xF0</c> and ending with <c>0xF7</c>.
    /// </param>
    public SysEx(byte[] data) => Data = data;

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteSysEx(stream, timestamp, Data);
}
