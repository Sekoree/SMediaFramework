namespace PMLib.Types;

/// <summary>
/// Identifies the MIDI message type.
/// The underlying value matches the MIDI status byte for system messages, and the
/// upper-nibble status byte (without channel) for channel voice messages.
/// </summary>
public enum MIDIMessageType : byte
{
    // ── Composite (not a single MIDI status byte) ─────────────────────────────
    /// <summary>Composite: Non-Registered Parameter Number (CC 99/98 + CC 6/38).</summary>
    NRPN                 = 0x01,
    /// <summary>Composite: Registered Parameter Number (CC 101/100 + CC 6/38).</summary>
    RPN                  = 0x02,

    // ── Channel voice ─────────────────────────────────────────────────────────
    NoteOff              = 0x80,
    NoteOn               = 0x90,
    PolyphonicAftertouch = 0xA0,
    ControlChange        = 0xB0,
    ProgramChange        = 0xC0,
    ChannelAftertouch    = 0xD0,
    PitchBend            = 0xE0,

    // ── System common ─────────────────────────────────────────────────────────
    SysEx                = 0xF0,
    MIDITimeCode         = 0xF1,
    SongPosition         = 0xF2,
    SongSelect           = 0xF3,
    TuneRequest          = 0xF6,

    // ── System real-time ──────────────────────────────────────────────────────
    TimingClock          = 0xF8,
    Start                = 0xFA,
    Continue             = 0xFB,
    Stop                 = 0xFC,
    ActiveSensing        = 0xFE,
    Reset                = 0xFF,
}
