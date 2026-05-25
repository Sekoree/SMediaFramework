using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// Decodes raw <see cref="PmEvent"/> structures received from PortMidi into
/// strongly-typed <see cref="IMIDIMessage"/> instances.
/// </summary>
/// <remarks>
/// <b>14-bit Control Change:</b> PortMidi delivers CC messages individually, so a 14-bit
/// update arrives as two separate <see cref="ControlChange"/> objects (coarse CC 0–31
/// followed by fine CC 32–63). Higher-level code must track these pairs and combine them
/// using <see cref="ControlChange.FromCoarseFine"/>.
///
/// <b>SysEx:</b> SysEx messages are fragmented across multiple <see cref="PmEvent"/>
/// structures and are assembled by <see cref="MIDIInputDevice"/> before being surfaced as a
/// complete <see cref="SysEx"/> instance.
/// </remarks>
public static class MIDIMessageParser
{
    private static readonly bool[] RealTimeStatuses = BuildRealTimeTable();

    private static bool[] BuildRealTimeTable()
    {
        var t = new bool[256];
        t[0xF8] = true; // Timing Clock
        t[0xFA] = true; // Start
        t[0xFB] = true; // Continue
        t[0xFC] = true; // Stop
        t[0xFE] = true; // Active Sensing
        t[0xFF] = true; // Reset
        return t;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="status"/> is a real-time status byte
    /// (0xF8, 0xFA, 0xFB, 0xFC, 0xFE, 0xFF). Real-time bytes can appear inside SysEx streams.
    /// </summary>
    internal static bool IsRealTime(byte status) => RealTimeStatuses[status];

    /// <summary>
    /// Decodes a single <see cref="PmEvent"/> into an <see cref="IMIDIMessage"/>.
    /// Returns <see langword="null"/> for unknown or unsupported status bytes and for
    /// SysEx start events (0xF0), which are handled separately by the input device.
    /// </summary>
    public static IMIDIMessage? Decode(PmEvent ev)
    {
        byte status = PmEvent.GetStatus(ev.Message);
        byte data1  = PmEvent.GetData1(ev.Message);
        byte data2  = PmEvent.GetData2(ev.Message);

        byte nibble  = (byte)(status & 0xF0);
        byte channel = (byte)(status & 0x0F);

        // Channel voice messages
        return nibble switch
        {
            0x80 => new NoteOff(channel, data1, data2),
            // Note On with velocity 0 is treated as Note Off per the MIDI spec
            0x90 => data2 == 0
                ? new NoteOff(channel, data1, 0)
                : new NoteOn(channel, data1, data2),
            0xA0 => new PolyphonicAftertouch(channel, data1, data2),
            0xB0 => new ControlChange(channel, data1, data2),
            0xC0 => new ProgramChange(channel, data1),
            0xD0 => new ChannelAftertouch(channel, data1),
            0xE0 => new PitchBend(channel, (data1 | (data2 << 7)) - 8192),
            // System messages (status >= 0xF0 — channel nibble is meaningless)
            0xF0 => DecodeSystem(status, data1, data2),
            _ => null
        };
    }

    private static IMIDIMessage? DecodeSystem(byte status, byte data1, byte data2) =>
        status switch
        {
            0xF0 => null,               // SysEx start — assembled by MIDIInputDevice
            0xF1 => new MIDITimeCode(data1),
            0xF2 => new SongPosition((ushort)(data1 | (data2 << 7))),
            0xF3 => new SongSelect(data1),
            0xF6 => new TuneRequest(),
            0xF8 => new TimingClock(),
            0xFA => new MIDIStart(),
            0xFB => new MIDIContinue(),
            0xFC => new MIDIStop(),
            0xFE => new ActiveSensing(),
            0xFF => new MIDIReset(),
            _ => null
        };
}
