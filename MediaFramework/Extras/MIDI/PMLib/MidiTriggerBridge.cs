using PMLib.Devices;
using PMLib.MessageTypes;
using S.Media.Core.Triggers;

namespace PMLib;

/// <summary>Maps MIDI input messages to <see cref="TriggerBus"/> ids via <see cref="MidiTriggerProfile"/>.</summary>
public sealed class MidiTriggerBridge : IDisposable
{
    private readonly TriggerBus _bus;
    private readonly MIDIInputDevice _input;
    private readonly MidiTriggerProfile _profile;
    private bool _disposed;

    public MidiTriggerBridge(TriggerBus bus, MIDIInputDevice input, MidiTriggerProfile profile)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _input.MessageReceived += OnMessage;
    }

    private void OnMessage(object? sender, IMIDIMessage message)
    {
        switch (message)
        {
            case NoteOn note when note.Velocity > 0:
                if (_profile.TryResolveNoteOn(note.Channel, note.Note, out var noteId))
                    _bus.Fire(noteId, TriggerPayload.FromNumeric(note.Velocity / 127.0));
                break;
            case ControlChange cc:
                if (_profile.TryResolveControlChange(cc.Channel, cc.Controller, out var ccId))
                    _bus.Fire(ccId, TriggerPayload.FromNumeric(cc.Value / 127.0));
                break;
            case ProgramChange pc:
                if (_profile.TryResolveProgramChange(pc.Channel, pc.Program, out var pcId))
                    _bus.Fire(pcId, TriggerPayload.FromNumeric(pc.Program));
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _input.MessageReceived -= OnMessage;
    }
}

/// <summary>Note/CC/program-change → trigger id mappings.</summary>
public sealed class MidiTriggerProfile
{
    private readonly Dictionary<(byte Ch, byte Note), string> _noteOn = new();
    private readonly Dictionary<(byte Ch, byte Cc), string> _cc = new();
    private readonly Dictionary<(byte Ch, byte Prog), string> _program = new();

    public MidiTriggerProfile MapNoteOn(byte channel, byte note, string triggerId)
    {
        _noteOn[(channel, note)] = triggerId;
        return this;
    }

    public MidiTriggerProfile MapControlChange(byte channel, byte controller, string triggerId)
    {
        _cc[(channel, controller)] = triggerId;
        return this;
    }

    public MidiTriggerProfile MapProgramChange(byte channel, byte program, string triggerId)
    {
        _program[(channel, program)] = triggerId;
        return this;
    }

    public bool TryResolveNoteOn(byte channel, byte note, out string triggerId) =>
        _noteOn.TryGetValue((channel, note), out triggerId!);

    public bool TryResolveControlChange(byte channel, byte controller, out string triggerId) =>
        _cc.TryGetValue((channel, controller), out triggerId!);

    public bool TryResolveProgramChange(byte channel, byte program, out string triggerId) =>
        _program.TryGetValue((channel, program), out triggerId!);
}
