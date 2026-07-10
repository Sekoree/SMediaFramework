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

public sealed record MIDIControlEvent(
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

public sealed record MIDINoteControlEvent(
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

public sealed record MIDIMessageControlEvent(
    DateTimeOffset Timestamp,
    Guid SourceNodeId,
    Guid OriginId,
    Guid CorrelationId,
    ControlMIDIMessagePayload Message,
    IReadOnlyList<Guid>? Path = null)
    : ControlEvent(Timestamp, SourceNodeId, OriginId, CorrelationId, Path);

public sealed record ControlMIDIMessagePayload
{
    public ControlMIDIMessageType MessageType { get; init; } = ControlMIDIMessageType.Unknown;

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

    public static ControlMIDIMessagePayload FromMIDIMessage(IMIDIMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message switch
        {
            ControlChange cc => new()
            {
                MessageType = ControlMIDIMessageType.ControlChange,
                Channel = cc.Channel + 1,
                Controller = cc.Controller,
                Value = cc.Value,
                HighResolution14Bit = cc.IsHighResolution,
            },
            NoteOn note => new()
            {
                MessageType = ControlMIDIMessageType.NoteOn,
                Channel = note.Channel + 1,
                Note = note.Note,
                Velocity = note.Velocity,
                Value = note.Velocity,
                IsNoteOn = true,
            },
            NoteOff note => new()
            {
                MessageType = ControlMIDIMessageType.NoteOff,
                Channel = note.Channel + 1,
                Note = note.Note,
                Velocity = note.Velocity,
                Value = note.Velocity,
                IsNoteOn = false,
            },
            PolyphonicAftertouch aftertouch => new()
            {
                MessageType = ControlMIDIMessageType.PolyphonicAftertouch,
                Channel = aftertouch.Channel + 1,
                Note = aftertouch.Note,
                Pressure = aftertouch.Pressure,
                Value = aftertouch.Pressure,
            },
            ProgramChange program => new()
            {
                MessageType = ControlMIDIMessageType.ProgramChange,
                Channel = program.Channel + 1,
                Program = program.Program,
                Value = program.Program,
            },
            ChannelAftertouch aftertouch => new()
            {
                MessageType = ControlMIDIMessageType.ChannelAftertouch,
                Channel = aftertouch.Channel + 1,
                Pressure = aftertouch.Pressure,
                Value = aftertouch.Pressure,
            },
            PitchBend bend => new()
            {
                MessageType = ControlMIDIMessageType.PitchBend,
                Channel = bend.Channel + 1,
                PitchBend = bend.Value,
                Value = bend.Value,
            },
            SysEx sysEx => new()
            {
                MessageType = ControlMIDIMessageType.SysEx,
                Data = sysEx.Data.ToArray(),
                Value = sysEx.Data.Length,
            },
            MIDITimeCode timeCode => new()
            {
                MessageType = ControlMIDIMessageType.MIDITimeCode,
                DataByte = timeCode.DataByte,
                Value = timeCode.DataByte,
            },
            SongPosition songPosition => new()
            {
                MessageType = ControlMIDIMessageType.SongPosition,
                SongPosition = songPosition.Beats,
                Value = songPosition.Beats,
            },
            SongSelect songSelect => new()
            {
                MessageType = ControlMIDIMessageType.SongSelect,
                Song = songSelect.Song,
                Value = songSelect.Song,
            },
            TuneRequest => new() { MessageType = ControlMIDIMessageType.TuneRequest },
            TimingClock => new() { MessageType = ControlMIDIMessageType.TimingClock },
            MIDIStart => new() { MessageType = ControlMIDIMessageType.Start },
            MIDIContinue => new() { MessageType = ControlMIDIMessageType.Continue },
            MIDIStop => new() { MessageType = ControlMIDIMessageType.Stop },
            ActiveSensing => new() { MessageType = ControlMIDIMessageType.ActiveSensing },
            MIDIReset => new() { MessageType = ControlMIDIMessageType.Reset },
            NRPN nrpn => new()
            {
                MessageType = ControlMIDIMessageType.NRPN,
                Channel = nrpn.Channel + 1,
                Parameter = nrpn.Parameter,
                Value = nrpn.Value,
            },
            RPN rpn => new()
            {
                MessageType = ControlMIDIMessageType.RPN,
                Channel = rpn.Channel + 1,
                Parameter = rpn.Parameter,
                Value = rpn.Value,
            },
            _ => new() { MessageType = MapMessageType(message.MessageType) },
        };
    }

    private static ControlMIDIMessageType MapMessageType(MIDIMessageType messageType) =>
        messageType switch
        {
            MIDIMessageType.NRPN => ControlMIDIMessageType.NRPN,
            MIDIMessageType.RPN => ControlMIDIMessageType.RPN,
            MIDIMessageType.NoteOff => ControlMIDIMessageType.NoteOff,
            MIDIMessageType.NoteOn => ControlMIDIMessageType.NoteOn,
            MIDIMessageType.PolyphonicAftertouch => ControlMIDIMessageType.PolyphonicAftertouch,
            MIDIMessageType.ControlChange => ControlMIDIMessageType.ControlChange,
            MIDIMessageType.ProgramChange => ControlMIDIMessageType.ProgramChange,
            MIDIMessageType.ChannelAftertouch => ControlMIDIMessageType.ChannelAftertouch,
            MIDIMessageType.PitchBend => ControlMIDIMessageType.PitchBend,
            MIDIMessageType.SysEx => ControlMIDIMessageType.SysEx,
            MIDIMessageType.MIDITimeCode => ControlMIDIMessageType.MIDITimeCode,
            MIDIMessageType.SongPosition => ControlMIDIMessageType.SongPosition,
            MIDIMessageType.SongSelect => ControlMIDIMessageType.SongSelect,
            MIDIMessageType.TuneRequest => ControlMIDIMessageType.TuneRequest,
            MIDIMessageType.TimingClock => ControlMIDIMessageType.TimingClock,
            MIDIMessageType.Start => ControlMIDIMessageType.Start,
            MIDIMessageType.Continue => ControlMIDIMessageType.Continue,
            MIDIMessageType.Stop => ControlMIDIMessageType.Stop,
            MIDIMessageType.ActiveSensing => ControlMIDIMessageType.ActiveSensing,
            MIDIMessageType.Reset => ControlMIDIMessageType.Reset,
            _ => ControlMIDIMessageType.Unknown,
        };
}

public enum ControlMIDIMessageType
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

/// <summary>
/// A single decoded OSC message routed to the control runtime. One datagram becomes one or more of these:
/// a bundle is expanded to one <see cref="OSCControlEvent"/> per contained message (bundle time-tags are
/// honoured by <c>OSCBundleScheduler</c> before dispatch), so <strong>bundle atomicity is not preserved</strong>
/// once messages enter the dispatch queue - each message is handled independently.
/// </summary>
/// <remarks>
/// <para><strong>Coalesce/drop policy (OSC-01, relative to CTRL-01).</strong> Unlike absolute continuous MIDI
/// controls, an OSC message is <em>never coalesced</em> (<c>ControlEventQueue.CoalesceKeyFor</c> returns
/// <c>null</c> for it): OSC addresses carry semantics the queue cannot assume are idempotent, so every message
/// is preserved in order under normal load. Under sustained overflow of the bounded queue, OSC messages are
/// treated as non-coalescable "edges" - the queue first sheds the oldest coalescable continuous item, and only
/// drops an OSC message (counted in <c>DroppedCount</c>) when the buffer holds nothing but edges. So OSC delivery
/// is lossless except under extreme, sustained flooding, and even then the loss is bounded and counted rather
/// than allowed to grow memory/latency without limit. The malformed-datagram, flood, and ordering behaviours are
/// covered by <c>OSCMalformedPacketTests</c> and the OSC listener/codec suites.</para>
/// </remarks>
public sealed record OSCControlEvent(
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

public sealed record OSCCacheChangedControlEvent(
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
