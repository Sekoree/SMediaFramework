using OSCLib;
using PMLib.Devices;
using PMLib.MessageTypes;
using S.Media.Core.Triggers;

namespace S.Control;

/// <summary>Maps MIDI input messages to Core trigger ids without coupling the PortMIDI binding to Core.</summary>
public sealed class ControlMIDITriggerBridge : IDisposable
{
    private readonly TriggerBus _bus;
    private readonly MIDIInputDevice _input;
    private readonly ControlMIDITriggerProfile _profile;
    private bool _disposed;

    public ControlMIDITriggerBridge(TriggerBus bus, MIDIInputDevice input, ControlMIDITriggerProfile profile)
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
        if (_disposed)
            return;
        _disposed = true;
        _input.MessageReceived -= OnMessage;
    }
}

public sealed class ControlMIDITriggerProfile
{
    private readonly Dictionary<(byte Channel, byte Note), string> _noteOn = new();
    private readonly Dictionary<(byte Channel, byte Controller), string> _controlChanges = new();
    private readonly Dictionary<(byte Channel, byte Program), string> _programChanges = new();

    public ControlMIDITriggerProfile MapNoteOn(byte channel, byte note, string triggerId)
    {
        _noteOn[(channel, note)] = triggerId;
        return this;
    }

    public ControlMIDITriggerProfile MapControlChange(byte channel, byte controller, string triggerId)
    {
        _controlChanges[(channel, controller)] = triggerId;
        return this;
    }

    public ControlMIDITriggerProfile MapProgramChange(byte channel, byte program, string triggerId)
    {
        _programChanges[(channel, program)] = triggerId;
        return this;
    }

    public bool TryResolveNoteOn(byte channel, byte note, out string triggerId) =>
        _noteOn.TryGetValue((channel, note), out triggerId!);

    public bool TryResolveControlChange(byte channel, byte controller, out string triggerId) =>
        _controlChanges.TryGetValue((channel, controller), out triggerId!);

    public bool TryResolveProgramChange(byte channel, byte program, out string triggerId) =>
        _programChanges.TryGetValue((channel, program), out triggerId!);
}

/// <summary>Maps inbound OSC messages to Core trigger ids without coupling OSCLib to Core.</summary>
public sealed class ControlOSCTriggerBridge : IDisposable
{
    private readonly TriggerBus _bus;
    private readonly IDisposable _registration;
    private bool _disposed;

    public ControlOSCTriggerBridge(TriggerBus bus, IOSCServer server)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        ArgumentNullException.ThrowIfNull(server);
        _registration = server.RegisterHandler("//", OnMessageAsync);
    }

    private ValueTask OnMessageAsync(OSCMessageContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _bus.Fire(context.Message.Address, MapMessageToPayload(context.Message));
        return ValueTask.CompletedTask;
    }

    public static TriggerPayload MapMessageToPayload(OSCMessage message)
    {
        if (message.Arguments.Count == 0)
            return TriggerPayload.None;

        var first = message.Arguments[0];
        return first.Type switch
        {
            OSCArgumentType.Float32 => TriggerPayload.FromNumeric(first.AsFloat32()),
            OSCArgumentType.Double64 => TriggerPayload.FromNumeric(first.AsDouble64()),
            OSCArgumentType.Int32 => TriggerPayload.FromNumeric(first.AsInt32()),
            OSCArgumentType.Int64 => TriggerPayload.FromNumeric(first.AsInt64()),
            OSCArgumentType.String or OSCArgumentType.Symbol => TriggerPayload.FromText(first.AsString()),
            OSCArgumentType.True => TriggerPayload.FromNumeric(1),
            OSCArgumentType.False => TriggerPayload.FromNumeric(0),
            OSCArgumentType.Impulse => TriggerPayload.FromNumeric(1),
            _ => TriggerPayload.None,
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _registration.Dispose();
    }
}
