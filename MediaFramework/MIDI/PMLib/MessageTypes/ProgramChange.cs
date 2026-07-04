using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>MIDI Program Change message (status <c>0xCn</c>). Selects a new patch/preset.</summary>
public readonly struct ProgramChange : IMIDIMessage
{
    /// <summary>MIDI channel, 0–15.</summary>
    public byte Channel { get; }
    /// <summary>Program (patch) number, 0–127.</summary>
    public byte Program { get; }

    public MIDIMessageType MessageType => MIDIMessageType.ProgramChange;

    public ProgramChange(byte channel, byte program)
    {
        Channel = channel;
        Program = program;
    }

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage((byte)(0xC0 | (Channel & 0x0F)), Program, 0));
}
