using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// MIDI Channel Aftertouch message (status <c>0xDn</c>).
/// Reports the greatest pressure applied to any key on the channel (not per-note).
/// </summary>
public readonly struct ChannelAftertouch : IMIDIMessage
{
    /// <summary>MIDI channel, 0–15.</summary>
    public byte Channel { get; }
    /// <summary>Pressure amount, 0–127.</summary>
    public byte Pressure { get; }

    public MIDIMessageType MessageType => MIDIMessageType.ChannelAftertouch;

    public ChannelAftertouch(byte channel, byte pressure)
    {
        Channel = channel;
        Pressure = pressure;
    }

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage((byte)(0xD0 | (Channel & 0x0F)), Pressure, 0));
}
