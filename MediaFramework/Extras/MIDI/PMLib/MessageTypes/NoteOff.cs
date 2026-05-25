using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>MIDI Note Off message (status <c>0x8n</c>).</summary>
public readonly struct NoteOff : IMIDIMessage
{
    /// <summary>MIDI channel, 0–15.</summary>
    public byte Channel { get; }
    /// <summary>Note number, 0–127.</summary>
    public byte Note { get; }
    /// <summary>Release velocity, 0–127 (many devices ignore this).</summary>
    public byte Velocity { get; }

    public MIDIMessageType MessageType => MIDIMessageType.NoteOff;

    public NoteOff(byte channel, byte note, byte velocity = 0)
    {
        Channel = channel;
        Note = note;
        Velocity = velocity;
    }

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage((byte)(0x80 | (Channel & 0x0F)), Note, Velocity));
}
