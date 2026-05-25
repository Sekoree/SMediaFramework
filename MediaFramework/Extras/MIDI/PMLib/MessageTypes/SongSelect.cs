using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>MIDI Song Select message (status <c>0xF3</c>). Selects a song or sequence.</summary>
public readonly struct SongSelect : IMIDIMessage
{
    /// <summary>Song number, 0–127.</summary>
    public byte Song { get; }

    public MIDIMessageType MessageType => MIDIMessageType.SongSelect;

    public SongSelect(byte song) => Song = song;

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(0xF3, Song, 0));
}
