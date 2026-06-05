using OSCLib;

namespace HaPlay.ControlGraph;

/// <summary>Host-mediated OSC output used by the control runtime and script command router.</summary>
public interface IControlOscSender
{
    ValueTask SendAsync(string host, int port, string address, IReadOnlyList<OSCArgument> arguments, CancellationToken cancellationToken = default);
}

/// <summary>Host-mediated MIDI output used by the control runtime and script command router.</summary>
public interface IControlMidiSender
{
    ValueTask SendControlChangeAsync(
        Guid? endpointId,
        int channel,
        int controller,
        int value,
        bool highResolution14Bit,
        CancellationToken cancellationToken = default);

    ValueTask SendNoteAsync(
        Guid? endpointId,
        int channel,
        int note,
        int velocity,
        bool isNoteOn,
        CancellationToken cancellationToken = default);

    ValueTask SendProgramChangeAsync(
        Guid? endpointId,
        int channel,
        int program,
        CancellationToken cancellationToken = default);

    ValueTask SendPitchBendAsync(
        Guid? endpointId,
        int channel,
        int value,
        CancellationToken cancellationToken = default);
}
