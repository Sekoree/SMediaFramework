using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>MIDI Note On message (status <c>0x9n</c>).</summary>
public readonly struct NoteOn : IMIDIMessage
{
    /// <summary>MIDI channel, 0–15.</summary>
    public byte Channel { get; }
    /// <summary>Note number, 0–127 (middle C = 60).</summary>
    public byte Note { get; }
    /// <summary>Velocity, 1–127. A velocity of 0 is conventionally treated as Note Off.</summary>
    public byte Velocity { get; }

    public MIDIMessageType MessageType => MIDIMessageType.NoteOn;

    public NoteOn(byte channel, byte note, byte velocity)
    {
        Channel = channel;
        Note = note;
        Velocity = velocity;
    }

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage((byte)(0x90 | (Channel & 0x0F)), Note, Velocity));
}