namespace PMLib.Types;

/// <summary>
/// Bit-mask flags for filtering incoming MIDI message types.
/// Used with <see cref="Native.Pm_SetFilter"/>.
/// </summary>
[Flags]
public enum PmFilter : int
{
    None               = 0,
    /// <summary>Filter system-exclusive messages (0xF0).</summary>
    Sysex              = 1 << 0x00,
    /// <summary>Filter MIDI Time Code messages (0xF1).</summary>
    MIDITimeCode       = 1 << 0x01,
    /// <summary>Filter Song Position messages (0xF2).</summary>
    SongPosition       = 1 << 0x02,
    /// <summary>Filter Song Select messages (0xF3).</summary>
    SongSelect         = 1 << 0x03,
    /// <summary>Filter Tune Request messages (0xF6).</summary>
    Tune               = 1 << 0x06,
    /// <summary>Filter MIDI Clock messages (0xF8).</summary>
    Clock              = 1 << 0x08,
    /// <summary>Filter MIDI Tick messages (0xF9).</summary>
    Tick               = 1 << 0x09,
    /// <summary>Filter play messages: Start (0xFA), Continue (0xFB), Stop (0xFC).</summary>
    Play               = (1 << 0x0A) | (1 << 0x0B) | (1 << 0x0C),
    /// <summary>Filter undefined 0xFD real-time messages.</summary>
    FD                 = 1 << 0x0D,
    /// <inheritdoc cref="FD"/>
    Undefined          = 1 << 0x0D,
    /// <summary>Filter Active Sensing messages (0xFE).</summary>
    Active             = 1 << 0x0E,
    /// <summary>Filter Reset messages (0xFF).</summary>
    Reset              = 1 << 0x0F,
    /// <summary>Filter all real-time messages.</summary>
    RealTime           = Active | Sysex | Clock | Play | FD | Reset | Tick,
    /// <summary>Filter Note On and Note Off messages (0x80–0x9F).</summary>
    Note               = (1 << 0x18) | (1 << 0x19),
    /// <summary>Filter per-note (polyphonic) aftertouch messages (0xA0–0xAF).</summary>
    PolyAftertouch     = 1 << 0x1A,
    /// <summary>Filter Control Change messages (0xB0–0xBF).</summary>
    Control            = 1 << 0x1B,
    /// <summary>Filter Program Change messages (0xC0–0xCF).</summary>
    Program            = 1 << 0x1C,
    /// <summary>Filter Channel Aftertouch messages (0xD0–0xDF).</summary>
    ChannelAftertouch  = 1 << 0x1D,
    /// <summary>Filter Pitch Bend messages (0xE0–0xEF).</summary>
    PitchBend          = 1 << 0x1E,
    /// <summary>Filter both channel and polyphonic aftertouch.</summary>
    Aftertouch         = ChannelAftertouch | PolyAftertouch,
    /// <summary>Filter all System Common messages (MTC, Song Position, Song Select, Tune Request).</summary>
    SystemCommon       = MIDITimeCode | SongPosition | SongSelect | Tune,
}
