using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// MIDI Pitch Bend message (status <c>0xEn</c>).
/// The value is inherently 14-bit: <c>-8192</c> (full down) to <c>+8191</c> (full up),
/// with <c>0</c> as the centre / no-bend position.
/// </summary>
public readonly struct PitchBend : IMIDIMessage
{
    /// <summary>MIDI channel, 0–15.</summary>
    public byte Channel { get; }

    /// <summary>
    /// Pitch bend value in the range <c>-8192</c> to <c>+8191</c>.
    /// <c>0</c> = centre (no bend). Values are clamped on output.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// The raw, unsigned 14-bit MIDI representation (0–16383, centre = 8192).
    /// </summary>
    public int RawValue => Math.Clamp(Value + 8192, 0, 16383);

    public MIDIMessageType MessageType => MIDIMessageType.PitchBend;

    public PitchBend(byte channel, int value)
    {
        Channel = channel;
        Value = value;
    }

    public PmError WriteTo(nint stream, int timestamp)
    {
        int raw = RawValue;
        byte lsb = (byte)(raw & 0x7F);
        byte msb = (byte)((raw >> 7) & 0x7F);
        return Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage((byte)(0xE0 | (Channel & 0x0F)), lsb, msb));
    }
}
