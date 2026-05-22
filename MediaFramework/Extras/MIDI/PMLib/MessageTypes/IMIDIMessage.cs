using PMLib.Types;

namespace PMLib.MessageTypes;

public interface IMIDIMessage
{
    /// <summary>The type of this MIDI message.</summary>
    MIDIMessageType MessageType { get; }

    /// <summary>
    /// Writes this message to an open PortMidi output stream.
    /// Some message types (e.g. 14-bit <see cref="ControlChange"/>) emit two
    /// consecutive short messages.
    /// </summary>
    PmError WriteTo(nint stream, int timestamp);
}