using OSCLib;

namespace HaPlay.ControlGraph;

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
