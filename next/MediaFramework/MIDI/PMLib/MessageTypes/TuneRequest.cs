using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// MIDI Tune Request message (status <c>0xF6</c>).
/// Requests all analog synthesisers to tune their oscillators.
/// </summary>
public readonly struct TuneRequest : IMIDIMessage
{
    public MIDIMessageType MessageType => MIDIMessageType.TuneRequest;

    public PmError WriteTo(nint stream, int timestamp)
        => Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(0xF6, 0, 0));
}
