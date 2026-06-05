using OSCLib;
using PMLib.MessageTypes;
using PMLib.Types;

namespace S.Control;

public abstract record ControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    IReadOnlyList<Guid>? Path = null);

public sealed record MidiControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    int Channel,
    int Controller,
    int Value,
    bool HighResolution14Bit,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record MidiNoteControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    int Channel,
    int Note,
    int Velocity,
    bool IsNoteOn,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record MidiMessageControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    ControlMidiMessagePayload Message,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record ControlMidiMessagePayload
{
    public ControlMidiMessageType MessageType { get; init; } = ControlMidiMessageType.Unknown;

    /// <summary>One-based MIDI channel for channel messages. System messages leave this null.</summary>
    public int? Channel { get; init; }

    public int? Controller { get; init; }

    public int? Note { get; init; }

    /// <summary>Primary numeric value: CC value, velocity, program, bend, pressure, song, or data byte.</summary>
    public int? Value { get; init; }

    public int? Velocity { get; init; }

    public bool? IsNoteOn { get; init; }

    public bool HighResolution14Bit { get; init; }

    public int? Program { get; init; }

    public int? Pressure { get; init; }

    public int? PitchBend { get; init; }

    public int? SongPosition { get; init; }

    public int? Song { get; init; }

    public int? DataByte { get; init; }

    public int? Parameter { get; init; }

    public byte[]? Data { get; init; }

    public static ControlMidiMessagePayload FromMidiMessage(IMIDIMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message switch
        {
            ControlChange cc => new()
            {
                MessageType = ControlMidiMessageType.ControlChange,
                Channel = cc.Channel + 1,
                Controller = cc.Controller,
                Value = cc.Value,
                HighResolution14Bit = cc.IsHighResolution,
            },
            NoteOn note => new()
            {
                MessageType = ControlMidiMessageType.NoteOn,
                Channel = note.Channel + 1,
                Note = note.Note,
                Velocity = note.Velocity,
                Value = note.Velocity,
                IsNoteOn = true,
            },
            NoteOff note => new()
            {
                MessageType = ControlMidiMessageType.NoteOff,
                Channel = note.Channel + 1,
                Note = note.Note,
                Velocity = note.Velocity,
                Value = note.Velocity,
                IsNoteOn = false,
            },
            PolyphonicAftertouch aftertouch => new()
            {
                MessageType = ControlMidiMessageType.PolyphonicAftertouch,
                Channel = aftertouch.Channel + 1,
                Note = aftertouch.Note,
                Pressure = aftertouch.Pressure,
                Value = aftertouch.Pressure,
            },
            ProgramChange program => new()
            {
                MessageType = ControlMidiMessageType.ProgramChange,
                Channel = program.Channel + 1,
                Program = program.Program,
                Value = program.Program,
            },
            ChannelAftertouch aftertouch => new()
            {
                MessageType = ControlMidiMessageType.ChannelAftertouch,
                Channel = aftertouch.Channel + 1,
                Pressure = aftertouch.Pressure,
                Value = aftertouch.Pressure,
            },
            PitchBend bend => new()
            {
                MessageType = ControlMidiMessageType.PitchBend,
                Channel = bend.Channel + 1,
                PitchBend = bend.Value,
                Value = bend.Value,
            },
            SysEx sysEx => new()
            {
                MessageType = ControlMidiMessageType.SysEx,
                Data = sysEx.Data.ToArray(),
                Value = sysEx.Data.Length,
            },
            MIDITimeCode timeCode => new()
            {
                MessageType = ControlMidiMessageType.MIDITimeCode,
                DataByte = timeCode.DataByte,
                Value = timeCode.DataByte,
            },
            SongPosition songPosition => new()
            {
                MessageType = ControlMidiMessageType.SongPosition,
                SongPosition = songPosition.Beats,
                Value = songPosition.Beats,
            },
            SongSelect songSelect => new()
            {
                MessageType = ControlMidiMessageType.SongSelect,
                Song = songSelect.Song,
                Value = songSelect.Song,
            },
            TuneRequest => new() { MessageType = ControlMidiMessageType.TuneRequest },
            TimingClock => new() { MessageType = ControlMidiMessageType.TimingClock },
            MIDIStart => new() { MessageType = ControlMidiMessageType.Start },
            MIDIContinue => new() { MessageType = ControlMidiMessageType.Continue },
            MIDIStop => new() { MessageType = ControlMidiMessageType.Stop },
            ActiveSensing => new() { MessageType = ControlMidiMessageType.ActiveSensing },
            MIDIReset => new() { MessageType = ControlMidiMessageType.Reset },
            NRPN nrpn => new()
            {
                MessageType = ControlMidiMessageType.NRPN,
                Channel = nrpn.Channel + 1,
                Parameter = nrpn.Parameter,
                Value = nrpn.Value,
            },
            RPN rpn => new()
            {
                MessageType = ControlMidiMessageType.RPN,
                Channel = rpn.Channel + 1,
                Parameter = rpn.Parameter,
                Value = rpn.Value,
            },
            _ => new() { MessageType = MapMessageType(message.MessageType) },
        };
    }

    private static ControlMidiMessageType MapMessageType(MIDIMessageType messageType) =>
        messageType switch
        {
            MIDIMessageType.NRPN => ControlMidiMessageType.NRPN,
            MIDIMessageType.RPN => ControlMidiMessageType.RPN,
            MIDIMessageType.NoteOff => ControlMidiMessageType.NoteOff,
            MIDIMessageType.NoteOn => ControlMidiMessageType.NoteOn,
            MIDIMessageType.PolyphonicAftertouch => ControlMidiMessageType.PolyphonicAftertouch,
            MIDIMessageType.ControlChange => ControlMidiMessageType.ControlChange,
            MIDIMessageType.ProgramChange => ControlMidiMessageType.ProgramChange,
            MIDIMessageType.ChannelAftertouch => ControlMidiMessageType.ChannelAftertouch,
            MIDIMessageType.PitchBend => ControlMidiMessageType.PitchBend,
            MIDIMessageType.SysEx => ControlMidiMessageType.SysEx,
            MIDIMessageType.MIDITimeCode => ControlMidiMessageType.MIDITimeCode,
            MIDIMessageType.SongPosition => ControlMidiMessageType.SongPosition,
            MIDIMessageType.SongSelect => ControlMidiMessageType.SongSelect,
            MIDIMessageType.TuneRequest => ControlMidiMessageType.TuneRequest,
            MIDIMessageType.TimingClock => ControlMidiMessageType.TimingClock,
            MIDIMessageType.Start => ControlMidiMessageType.Start,
            MIDIMessageType.Continue => ControlMidiMessageType.Continue,
            MIDIMessageType.Stop => ControlMidiMessageType.Stop,
            MIDIMessageType.ActiveSensing => ControlMidiMessageType.ActiveSensing,
            MIDIMessageType.Reset => ControlMidiMessageType.Reset,
            _ => ControlMidiMessageType.Unknown,
        };
}

public enum ControlMidiMessageType
{
    Unknown,
    NRPN,
    RPN,
    NoteOff,
    NoteOn,
    PolyphonicAftertouch,
    ControlChange,
    ProgramChange,
    ChannelAftertouch,
    PitchBend,
    SysEx,
    MIDITimeCode,
    SongPosition,
    SongSelect,
    TuneRequest,
    TimingClock,
    Start,
    Continue,
    Stop,
    ActiveSensing,
    Reset,
}

public sealed record OscControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    string Address,
    IReadOnlyList<OSCArgument> Arguments,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record DeviceHealthChangedControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    Guid DeviceInstanceId,
    ControlSessionState State,
    ControlSessionState? PreviousState,
    string Detail,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record OscCacheChangedControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    string DeviceKey,
    string Address,
    int ArgumentIndex,
    ControlCachedValue Value,
    ControlValueCacheSource Source,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record ScalarControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    double Value,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record TextControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    string Value,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record BlobControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    ReadOnlyMemory<byte> Value,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);
