namespace HaPlay.Models;

public sealed record ControlSystemConfig
{
    public bool IsArmed { get; init; }

    public List<ControlOscListenerConfig> OscListeners { get; init; } = [new()];

    public ControlOscCacheUpdateMode OscCacheUpdateMode { get; init; } = ControlOscCacheUpdateMode.IncomingOnly;

    public ControlMonitorOptions Monitor { get; init; } = new();

    public List<ControlDeviceInstanceConfig> Devices { get; init; } = new();

    public List<ControlLayerConfig> Layers { get; init; } = new();

    public List<ControlScriptConfig> Scripts { get; init; } = new();
}

public enum ControlOscSocketMode
{
    SharedAppListener,
}

public sealed record ControlOscListenerConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Main OSC Listener";

    public bool IsEnabled { get; init; } = true;

    public int LocalPort { get; init; } = 10020;

    public ControlOscSocketMode SocketMode { get; init; } = ControlOscSocketMode.SharedAppListener;
}

public enum ControlOscCacheUpdateMode
{
    IncomingOnly,
    OptimisticSendAndIncoming,
}

public sealed record ControlMonitorOptions
{
    public int MaxVisibleMessages { get; init; } = 1000;

    public ControlMonitorCaptureFormat CaptureFormat { get; init; } = ControlMonitorCaptureFormat.JsonLines;

    public bool IncludeRawBytes { get; init; } = true;
}

public enum ControlMonitorCaptureFormat
{
    JsonLines,
}

public sealed record ControlDeviceInstanceConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public ControlDeviceProtocol Protocol { get; init; }

    public ControlDeviceProfileMode ProfileMode { get; init; } = ControlDeviceProfileMode.Suggestion;

    public bool IsEnabled { get; init; } = true;

    public ControlDeviceBindingConfig Binding { get; init; } = new();

    public List<ControlPeriodicOscSendConfig> PeriodicOscSends { get; init; } = new();

    public List<Guid> ScriptIds { get; init; } = new();
}

public enum ControlDeviceProtocol
{
    Midi,
    Osc,
}

public enum ControlDeviceProfileMode
{
    Suggestion,
    Required,
}

public sealed record ControlDeviceBindingConfig
{
    public string? Alias { get; init; }

    public int? MidiInputDeviceId { get; init; }

    public string? MidiInputDeviceName { get; init; }

    public int? MidiOutputDeviceId { get; init; }

    public string? MidiOutputDeviceName { get; init; }

    public string? OscHost { get; init; }

    public int? OscPort { get; init; }

    public Guid? OscListenerId { get; init; }
}

public sealed record ControlPeriodicOscSendConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "/xremote";

    public bool IsEnabled { get; init; } = true;

    public string Address { get; init; } = "/xremote";

    public int IntervalMs { get; init; } = 8000;

    public List<ControlOscArgumentConfig> Arguments { get; init; } = new();
}

public sealed record ControlOscArgumentConfig
{
    public ControlOscArgumentKind Kind { get; init; }

    public string? StringValue { get; init; }

    public long IntegerValue { get; init; }

    public double FloatValue { get; init; }

    public bool BoolValue { get; init; }

    public byte[]? BlobValue { get; init; }
}

public enum ControlOscArgumentKind
{
    Int32,
    Int64,
    Float32,
    Double64,
    String,
    Symbol,
    True,
    False,
    Nil,
    Blob,
}

public sealed record ControlLayerConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Layer";

    public bool IsEnabled { get; init; }

    public int Priority { get; init; }

    public List<Guid> ScriptIds { get; init; } = new();
}

public sealed record ControlScriptConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Control Script";

    public bool IsEnabled { get; init; } = true;

    public string ScriptPath { get; init; } = string.Empty;

    public ControlScriptScope Scope { get; init; } = ControlScriptScope.Project;

    public Guid? DeviceInstanceId { get; init; }

    public Guid? EndpointInstanceId { get; init; }

    public Guid? LayerId { get; init; }

    public ControlScriptFailurePolicy FailurePolicy { get; init; } = new();

    public List<string> Imports { get; init; } = new();

    public List<ControlScriptTriggerConfig> Triggers { get; init; } = new();
}

public enum ControlScriptScope
{
    Project,
    Device,
    Endpoint,
    Layer,
}

public sealed record ControlScriptFailurePolicy
{
    public ControlScriptFailureMode Mode { get; init; } = ControlScriptFailureMode.DisableScript;

    public int MaxConsecutiveFailures { get; init; } = 3;
}

public enum ControlScriptFailureMode
{
    KeepRunning,
    DisableScript,
    DisableScope,
    FaultControlSystem,
}

public sealed record ControlScriptTriggerConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public ControlScriptTriggerKind Kind { get; init; }

    public string FunctionName { get; init; } = string.Empty;

    public Guid? DeviceInstanceId { get; init; }

    public Guid? EndpointInstanceId { get; init; }

    public Guid? LayerId { get; init; }

    public string? OscAddressPattern { get; init; }

    public int? MidiChannel { get; init; }

    public int? MidiController { get; init; }

    public int? MidiNote { get; init; }

    public int? IntervalMs { get; init; }
}

public enum ControlScriptTriggerKind
{
    DeviceEnabled,
    DeviceDisabled,
    DeviceHealthChanged,
    MidiMessage,
    MidiControlChange,
    MidiNote,
    OscMessage,
    OscCacheChanged,
    LayerEnabled,
    LayerDisabled,
    Periodic,
    Manual,
}
