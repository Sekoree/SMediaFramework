using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// MIDI Polyphonic (per-note) Aftertouch message (status <c>0xAn</c>).
/// Reports the pressure applied to a specific key after it is pressed.
/// </summary>
public readonly struct PolyphonicAftertouch : IMIDIMessage
{
    /// <summary>MIDI channel, 0–15.</summary>
    public byte Channel { get; }
    /// <summary>Note number this aftertouch applies to, 0–127.</summary>
    public byte Note { get; }
    /// <summary>Pressure amount, 0–127.</summary>
    public byte Pressure { get; }

    public MIDIMessageType MessageType => MIDIMessageType.PolyphonicAftertouch;

    public PolyphonicAftertouch(byte channel, byte note, byte pressure)
    {
        Channel = channel;
        Note = note;
        Pressure = pressure;
    }

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage((byte)(0xA0 | (Channel & 0x0F)), Note, Pressure));
}
