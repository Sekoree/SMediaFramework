using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>MIDI Timing Clock message (status <c>0xF8</c>). Sent 24 times per quarter note.</summary>
public readonly struct TimingClock : IMIDIMessage
{
    public MIDIMessageType MessageType => MIDIMessageType.TimingClock;
    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp, PmEvent.CreateMessage(0xF8, 0, 0));
}

/// <summary>MIDI Start message (status <c>0xFA</c>). Starts playback from the beginning.</summary>
public readonly struct MIDIStart : IMIDIMessage
{
    public MIDIMessageType MessageType => MIDIMessageType.Start;
    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp, PmEvent.CreateMessage(0xFA, 0, 0));
}

/// <summary>MIDI Continue message (status <c>0xFB</c>). Resumes playback from the current position.</summary>
public readonly struct MIDIContinue : IMIDIMessage
{
    public MIDIMessageType MessageType => MIDIMessageType.Continue;
    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp, PmEvent.CreateMessage(0xFB, 0, 0));
}

/// <summary>MIDI Stop message (status <c>0xFC</c>). Stops playback.</summary>
public readonly struct MIDIStop : IMIDIMessage
{
    public MIDIMessageType MessageType => MIDIMessageType.Stop;
    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp, PmEvent.CreateMessage(0xFC, 0, 0));
}

/// <summary>
/// MIDI Active Sensing message (status <c>0xFE</c>).
/// Sent periodically to indicate the connection is alive. Filtered by PortMidi by default.
/// </summary>
public readonly struct ActiveSensing : IMIDIMessage
{
    public MIDIMessageType MessageType => MIDIMessageType.ActiveSensing;
    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp, PmEvent.CreateMessage(0xFE, 0, 0));
}

/// <summary>MIDI Reset message (status <c>0xFF</c>). Resets all receivers to their initial state.</summary>
public readonly struct MIDIReset : IMIDIMessage
{
    public MIDIMessageType MessageType => MIDIMessageType.Reset;
    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp, PmEvent.CreateMessage(0xFF, 0, 0));
}
