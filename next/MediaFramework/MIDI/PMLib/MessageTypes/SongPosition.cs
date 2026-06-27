using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// MIDI Song Position Pointer message (status <c>0xF2</c>).
/// The position is a 14-bit count of MIDI beats (each beat = 6 MIDI clocks) since the
/// start of the song, ranging from 0 to 16383.
/// </summary>
public readonly struct SongPosition : IMIDIMessage
{
    /// <summary>Beat position, 0–16383 (6 MIDI clocks per beat).</summary>
    public ushort Beats { get; }

    public MIDIMessageType MessageType => MIDIMessageType.SongPosition;

    public SongPosition(ushort beats) => Beats = (ushort)(beats & 0x3FFF);

    public PmError WriteTo(nint stream, int timestamp)
    {
        byte lsb = (byte)(Beats & 0x7F);
        byte msb = (byte)((Beats >> 7) & 0x7F);
        return Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(0xF2, lsb, msb));
    }
}
