using System.Text.Json.Serialization;

namespace HaPlay.Models;

public sealed record ControlGraphConfig
{
    public string Schema { get; init; } = "HaPlayControlGraph/v1";
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Control Graph";
    public bool IsEnabled { get; init; }
    public double ViewportX { get; init; }
    public double ViewportY { get; init; }
    public double Zoom { get; init; } = 1.0;
    public List<ControlNodeConfig> Nodes { get; init; } = new();
    public List<ControlConnectionConfig> Connections { get; init; } = new();
}

public sealed record ControlNodeConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = string.Empty;
    public ControlNodeKind Kind { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public ControlNodeSettings Settings { get; init; } = new PassthroughControlNodeSettings();
}

public sealed record ControlConnectionConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid FromNodeId { get; init; }
    public string FromPortId { get; init; } = "out";
    public Guid ToNodeId { get; init; }
    public string ToPortId { get; init; } = "in";
}

public enum ControlNodeKind
{
    MidiInput,
    OscInput,
    MapRange,
    OscOutput,
    MidiOutput,
    X32ChannelFader,
    ScriptTransform,
    Passthrough,
}

public enum ControlPortType
{
    Any,
    Midi,
    Osc,
    Scalar,
    Text,
    Blob,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PassthroughControlNodeSettings), "passthrough")]
[JsonDerivedType(typeof(MidiInputControlNodeSettings), "midiInput")]
[JsonDerivedType(typeof(OscInputControlNodeSettings), "oscInput")]
[JsonDerivedType(typeof(MapRangeControlNodeSettings), "mapRange")]
[JsonDerivedType(typeof(OscOutputControlNodeSettings), "oscOutput")]
[JsonDerivedType(typeof(MidiOutputControlNodeSettings), "midiOutput")]
[JsonDerivedType(typeof(X32ChannelFaderControlNodeSettings), "x32ChannelFader")]
[JsonDerivedType(typeof(ScriptTransformControlNodeSettings), "scriptTransform")]
public abstract record ControlNodeSettings;

public sealed record PassthroughControlNodeSettings : ControlNodeSettings;

public sealed record MidiInputControlNodeSettings : ControlNodeSettings
{
    public Guid? EndpointId { get; init; }
    public int Channel { get; init; } = 1;
    public int Controller { get; init; }
    public bool HighResolution14Bit { get; init; }
    public bool SoftTakeoverEnabled { get; init; }
    public double SoftTakeoverTolerance { get; init; } = 0.02;
}

public sealed record OscInputControlNodeSettings : ControlNodeSettings
{
    public Guid? EndpointId { get; init; }
    public int LocalPort { get; init; } = 9000;
    public string AddressPattern { get; init; } = "/ch/01/mix/fader";
}

public sealed record MapRangeControlNodeSettings : ControlNodeSettings
{
    public double InputMin { get; init; }
    public double InputMax { get; init; } = 127;
    public double OutputMin { get; init; }
    public double OutputMax { get; init; } = 1;
    public bool Clamp { get; init; } = true;
}

public sealed record ScriptTransformControlNodeSettings : ControlNodeSettings
{
    public string Source { get; init; } = "return emit.scalar(event.scalar.value);";
    public int InstructionLimit { get; init; } = 100_000;
}

public sealed record OscOutputControlNodeSettings : ControlNodeSettings
{
    public Guid? EndpointId { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 10023;
    public string Address { get; init; } = "/ch/01/mix/fader";
    public ControlOscArgumentMode ArgumentMode { get; init; } = ControlOscArgumentMode.FirstScalarAsFloat;
    public ControlFeedbackMode FeedbackMode { get; init; } = ControlFeedbackMode.DoNotEchoToOrigin;
    public int MinSendIntervalMs { get; init; }
}

public sealed record MidiOutputControlNodeSettings : ControlNodeSettings
{
    public Guid? EndpointId { get; init; }
    public int Channel { get; init; } = 1;
    public int Controller { get; init; }
    public bool HighResolution14Bit { get; init; }
    public ControlFeedbackMode FeedbackMode { get; init; } = ControlFeedbackMode.DoNotEchoToOrigin;
    public int MinSendIntervalMs { get; init; }
}

public sealed record X32ChannelFaderControlNodeSettings : ControlNodeSettings
{
    public Guid? EndpointId { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 10023;
    public int Channel { get; init; } = 1;
    public ControlFeedbackMode FeedbackMode { get; init; } = ControlFeedbackMode.DoNotEchoToOrigin;
    public int MinSendIntervalMs { get; init; }
}

public enum ControlOscArgumentMode
{
    None,
    FirstScalarAsFloat,
    FirstScalarAsInt,
    FirstTextAsString,
}

public enum ControlFeedbackMode
{
    SendFeedback,
    DoNotEchoToOrigin,
    SoftTakeover,
    MotorFeedbackOnly,
}

public sealed record X32CustomLayerConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "X32 Layer";
    public List<X32CustomLayerSlotConfig> Slots { get; init; } = new();
}

public sealed record X32CustomLayerSlotConfig
{
    public int SlotIndex { get; init; }
    public string Label { get; init; } = string.Empty;
    public X32LayerTargetKind TargetKind { get; init; } = X32LayerTargetKind.Channel;
    public int TargetIndex { get; init; } = 1;
    public int MidiChannel { get; init; } = 1;
    public int MidiController { get; init; }
    public bool HighResolution14Bit { get; init; }
}

public enum X32LayerTargetKind
{
    Channel,
    Bus,
    Dca,
    MainStereo,
}
