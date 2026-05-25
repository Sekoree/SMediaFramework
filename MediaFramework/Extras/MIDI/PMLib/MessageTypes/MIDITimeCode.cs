using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// MIDI Time Code Quarter Frame message (status <c>0xF1</c>).
/// Eight consecutive quarter-frame messages encode a full SMPTE timecode.
/// </summary>
public readonly struct MIDITimeCode : IMIDIMessage
{
    /// <summary>
    /// The single data byte, encoding:
    /// <list type="bullet">
    ///   <item><description>Bits 4–6: message type (0–7, selects which timecode nibble)</description></item>
    ///   <item><description>Bits 0–3: nibble value (0–15)</description></item>
    /// </list>
    /// </summary>
    public byte DataByte { get; }

    /// <summary>SMPTE quarter-frame message type selector, 0–7.</summary>
    public byte QuarterFrameType => (byte)((DataByte >> 4) & 0x07);

    /// <summary>Four-bit data nibble, 0–15.</summary>
    public byte Nibble => (byte)(DataByte & 0x0F);

    public MIDIMessageType MessageType => MIDIMessageType.MIDITimeCode;

    /// <summary>Constructs from a raw data byte.</summary>
    public MIDITimeCode(byte dataByte) => DataByte = dataByte;

    /// <summary>Constructs from a message type and nibble value.</summary>
    public MIDITimeCode(byte messageType, byte nibble)
        => DataByte = (byte)(((messageType & 0x07) << 4) | (nibble & 0x0F));

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(0xF1, DataByte, 0));
}
