using Mond;
using Mond.Libraries;

namespace HaPlay.ControlGraph;

public sealed class ControlScriptRuntimeServices
{
    public ControlScriptRuntimeServices(
        IControlScriptCommandSink? commandSink = null,
        ControlValueCache? oscCache = null,
        ControlScriptStateStore? stateStore = null,
        IControlMonitorSink? monitor = null,
        IReadOnlyList<HaPlay.Models.ControlDeviceInstanceConfig>? devices = null,
        ControlDeviceHealthRegistry? deviceHealth = null)
    {
        CommandSink = commandSink ?? NullControlScriptCommandSink.Instance;
        OscCache = oscCache ?? new ControlValueCache();
        StateStore = stateStore ?? new ControlScriptStateStore();
        Monitor = monitor ?? NullControlMonitorSink.Instance;
        Devices = devices ?? [];
        DeviceHealth = deviceHealth ?? new ControlDeviceHealthRegistry();
    }

    public IControlScriptCommandSink CommandSink { get; }

    public ControlValueCache OscCache { get; }

    public ControlScriptStateStore StateStore { get; }

    public IControlMonitorSink Monitor { get; }

    public IReadOnlyList<HaPlay.Models.ControlDeviceInstanceConfig> Devices { get; }

    public ControlDeviceHealthRegistry DeviceHealth { get; }
}

public interface IControlScriptCommandSink
{
    void SendOsc(ControlScriptOscMessage message);

    void SendMidi(ControlScriptMidiMessage message);
}

public sealed class NullControlScriptCommandSink : IControlScriptCommandSink
{
    public static NullControlScriptCommandSink Instance { get; } = new();

    private NullControlScriptCommandSink()
    {
    }

    public void SendOsc(ControlScriptOscMessage message)
    {
    }

    public void SendMidi(ControlScriptMidiMessage message)
    {
    }
}

public sealed record ControlScriptOscMessage(
    string DeviceKey,
    string Address,
    IReadOnlyList<ControlScriptOscArgument> Arguments);

public readonly record struct ControlScriptOscArgument(ControlScriptOscArgumentType Type, double NumberValue, string? StringValue, bool BooleanValue)
{
    public static ControlScriptOscArgument Float32(double value) =>
        new(ControlScriptOscArgumentType.Float32, value, null, false);

    public static ControlScriptOscArgument Double64(double value) =>
        new(ControlScriptOscArgumentType.Double64, value, null, false);

    public static ControlScriptOscArgument Int32(int value) =>
        new(ControlScriptOscArgumentType.Int32, value, null, false);

    public static ControlScriptOscArgument String(string value) =>
        new(ControlScriptOscArgumentType.String, 0, value, false);

    public static ControlScriptOscArgument Boolean(bool value) =>
        new(value ? ControlScriptOscArgumentType.True : ControlScriptOscArgumentType.False, 0, null, value);
}

public enum ControlScriptOscArgumentType
{
    Float32,
    Double64,
    Int32,
    String,
    True,
    False,
}

public sealed record ControlScriptMidiMessage(
    string DeviceKey,
    ControlScriptMidiMessageKind Kind,
    int Channel,
    int? Controller = null,
    int? Note = null,
    int? Velocity = null,
    int? Value = null,
    bool HighResolution14Bit = false);

public enum ControlScriptMidiMessageKind
{
    ControlChange,
    NoteOn,
    NoteOff,
    ProgramChange,
    PitchBend,
}

public sealed class ControlScriptApiLibrary : IMondLibrary
{
    private readonly ControlScriptRuntimeServices _services;

    public ControlScriptApiLibrary(ControlScriptRuntimeServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IEnumerable<KeyValuePair<string, MondValue>> GetDefinitions(MondState state)
    {
        yield return new KeyValuePair<string, MondValue>("osc", CreateOscApi(state));
        yield return new KeyValuePair<string, MondValue>("midi", CreateMidiApi(state));
        yield return new KeyValuePair<string, MondValue>("x32", CreateX32Api(state));
        yield return new KeyValuePair<string, MondValue>("math", CreateMathApi(state));
        yield return new KeyValuePair<string, MondValue>("state", CreateStateApi(state));
        yield return new KeyValuePair<string, MondValue>("monitor", CreateMonitorApi(state));
        yield return new KeyValuePair<string, MondValue>("devices", CreateDevicesApi(state));
    }

    private MondValue CreateDevicesApi(MondState state)
    {
        var devices = MondValue.Object(state);

        devices["list"] = (MondFunction)((s, _) =>
            MondValue.Array(_services.Devices.Select(device => ToMondDevice(s, device))));
        devices["get"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("devices.get requires a device key.");

            var device = ResolveDevice((string)args[offset]);
            return device is null ? MondValue.Null : ToMondDevice(s, device);
        });
        devices["isEnabled"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("devices.isEnabled requires a device key.");

            return ResolveDevice((string)args[offset])?.IsEnabled ?? false;
        });
        devices["health"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("devices.health requires a device key.");

            var device = ResolveDevice((string)args[offset]);
            return device is null ? "Unknown" : HealthStateName(device.Id);
        });

        return devices;
    }

    private MondValue ToMondDevice(MondState state, HaPlay.Models.ControlDeviceInstanceConfig device)
    {
        var obj = MondValue.Object(state);
        obj["id"] = device.Id.ToString();
        obj["name"] = device.Name;
        obj["alias"] = device.Binding.Alias ?? string.Empty;
        obj["profileId"] = device.ProfileId;
        obj["protocol"] = device.Protocol.ToString();
        obj["enabled"] = device.IsEnabled;
        obj["health"] = HealthStateName(device.Id);
        return obj;
    }

    private string HealthStateName(Guid deviceInstanceId) =>
        _services.DeviceHealth.TryGet(deviceInstanceId)?.State.ToString() ?? "Unknown";

    // Lenient key resolution: id, alias, name, or profile id (case-insensitive); first match wins.
    private HaPlay.Models.ControlDeviceInstanceConfig? ResolveDevice(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        if (Guid.TryParse(key, out var id))
        {
            var byId = _services.Devices.FirstOrDefault(d => d.Id == id);
            if (byId is not null)
                return byId;
        }

        return _services.Devices.FirstOrDefault(d =>
            string.Equals(d.Binding.Alias, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.ProfileId, key, StringComparison.OrdinalIgnoreCase));
    }

    private MondValue CreateMonitorApi(MondState state)
    {
        var monitor = MondValue.Object(state);

        monitor["log"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("monitor.log requires a message.");

            _services.Monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Internal,
                Protocol = ControlMonitorProtocol.Script,
                Result = ControlMonitorResult.Logged,
                ScriptId = _services.StateStore.CurrentScriptId,
                Message = ToDisplayString(args[offset]),
            });
            return MondValue.Undefined;
        });
        monitor["error"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("monitor.error requires a message.");

            _services.Monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Script,
                Result = ControlMonitorResult.Failed,
                ScriptId = _services.StateStore.CurrentScriptId,
                ErrorMessage = ToDisplayString(args[offset]),
            });
            return MondValue.Undefined;
        });

        return monitor;
    }

    private static string ToDisplayString(MondValue value) =>
        value.Type == MondValueType.String ? (string)value : value.ToString();

    private MondValue CreateOscApi(MondState state)
    {
        var osc = MondValue.Object(state);

        osc["float32"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "float32", args.Length > offset ? args[offset] : 0.0);
        });
        osc["double64"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "double64", args.Length > offset ? args[offset] : 0.0);
        });
        osc["int32"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "int32", args.Length > offset ? args[offset] : 0.0);
        });
        osc["string"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "string", args.Length > offset ? args[offset] : string.Empty);
        });
        osc["boolean"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "boolean", args.Length > offset ? args[offset] : false);
        });
        osc["send"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("osc.send requires device key and address.");

            var message = new ControlScriptOscMessage(
                (string)args[offset],
                (string)args[offset + 1],
                ReadOscArguments(args[(offset + 2)..]));
            _services.CommandSink.SendOsc(message);
            return MondValue.Undefined;
        });
        osc["cacheFloat"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("osc.cacheFloat requires device key and address.");

            var defaultValue = args.Length > offset + 2 && args[offset + 2].Type == MondValueType.Number
                ? (double)args[offset + 2]
                : 0.0;

            return _services.OscCache.GetNumberOrDefault((string)args[offset], (string)args[offset + 1], defaultValue);
        });
        osc["cacheSet"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 3)
                throw new MondRuntimeException("osc.cacheSet requires device key, address, and value.");

            var deviceKey = (string)args[offset];
            var address = (string)args[offset + 1];
            var value = args[offset + 2];

            switch (value.Type)
            {
                case MondValueType.Number:
                    _services.OscCache.SetNumber(deviceKey, address, (double)value, ControlValueCacheSource.Script);
                    break;
                case MondValueType.String:
                    _services.OscCache.SetString(deviceKey, address, (string)value, ControlValueCacheSource.Script);
                    break;
                case MondValueType.True:
                    _services.OscCache.SetBoolean(deviceKey, address, true, ControlValueCacheSource.Script);
                    break;
                case MondValueType.False:
                    _services.OscCache.SetBoolean(deviceKey, address, false, ControlValueCacheSource.Script);
                    break;
            }

            return value;
        });

        return osc;
    }

    private MondValue CreateMidiApi(MondState state)
    {
        var midi = MondValue.Object(state);

        midi["sendCc"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 4)
                throw new MondRuntimeException("midi.sendCc requires device key, channel, controller, and value.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.ControlChange,
                ToInt(args[offset + 1]),
                Controller: ToInt(args[offset + 2]),
                Value: ToInt(args[offset + 3]),
                HighResolution14Bit: args.Length > offset + 4 && ToBool(args[offset + 4])));
            return MondValue.Undefined;
        });
        midi["sendNoteOn"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 4)
                throw new MondRuntimeException("midi.sendNoteOn requires device key, channel, note, and velocity.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.NoteOn,
                ToInt(args[offset + 1]),
                Note: ToInt(args[offset + 2]),
                Velocity: ToInt(args[offset + 3])));
            return MondValue.Undefined;
        });
        midi["sendNoteOff"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 3)
                throw new MondRuntimeException("midi.sendNoteOff requires device key, channel, and note.");

            var velocity = args.Length > offset + 3 ? ToInt(args[offset + 3]) : 0;
            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.NoteOff,
                ToInt(args[offset + 1]),
                Note: ToInt(args[offset + 2]),
                Velocity: velocity));
            return MondValue.Undefined;
        });
        midi["sendProgramChange"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 3)
                throw new MondRuntimeException("midi.sendProgramChange requires device key, channel, and program.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.ProgramChange,
                ToInt(args[offset + 1]),
                Value: ToInt(args[offset + 2])));
            return MondValue.Undefined;
        });
        midi["sendPitchBend"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 3)
                throw new MondRuntimeException("midi.sendPitchBend requires device key, channel, and value.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.PitchBend,
                ToInt(args[offset + 1]),
                Value: ToInt(args[offset + 2])));
            return MondValue.Undefined;
        });

        return midi;
    }

    private static MondValue CreateX32Api(MondState state)
    {
        var x32 = MondValue.Object(state);

        x32["channelFaderAddress"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("x32.channelFaderAddress requires a channel number.");

            var channel = (int)(double)args[offset];
            if (channel is < 1 or > 32)
                throw new MondRuntimeException("X32 channel must be between 1 and 32.");

            return $"/ch/{channel:00}/mix/fader";
        });
        x32["faderToDb"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            return args.Length > offset ? X32Fader.FromNormalized((double)args[offset]) : MondValue.Undefined;
        });
        x32["dbToFader"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            return args.Length > offset ? X32Fader.ToNormalized((double)args[offset]) : MondValue.Undefined;
        });

        return x32;
    }

    private MondValue CreateStateApi(MondState state)
    {
        var store = _services.StateStore;

        // Top-level state.get/set/... operate on the current script's private scope (the common case);
        // state.project and state.device reach the shared and per-device scopes.
        var stateApi = CreateStateScope(
            state,
            () => store.Script ?? throw new MondRuntimeException("state requires an executing script context."));
        stateApi["project"] = CreateStateScope(state, () => store.Project);
        stateApi["script"] = CreateStateScope(
            state,
            () => store.Script ?? throw new MondRuntimeException("state.script requires an executing script context."));
        stateApi["device"] = CreateStateScope(
            state,
            () => store.Device ?? throw new MondRuntimeException("state.device requires a device-scoped trigger context."));

        return stateApi;
    }

    private static MondValue CreateStateScope(MondState state, Func<IDictionary<string, object?>> resolve)
    {
        var scope = MondValue.Object(state);

        scope["get"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("state.get requires a key.");

            var key = (string)args[offset];
            if (resolve().TryGetValue(key, out var value))
                return ToMondStateValue(value);

            return args.Length > offset + 1 ? args[offset + 1] : MondValue.Undefined;
        });
        scope["set"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("state.set requires a key and value.");

            var value = args[offset + 1];
            resolve()[(string)args[offset]] = FromMondStateValue(value);
            return value;
        });
        scope["has"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("state.has requires a key.");

            return resolve().ContainsKey((string)args[offset]);
        });
        scope["remove"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("state.remove requires a key.");

            return resolve().Remove((string)args[offset]);
        });
        scope["keys"] = (MondFunction)((s, _) =>
            MondValue.Array(resolve().Keys.Select(key => (MondValue)key)));

        return scope;
    }

    private static MondValue ToMondStateValue(object? value) =>
        value switch
        {
            null => MondValue.Null,
            double number => number,
            bool boolean => boolean ? MondValue.True : MondValue.False,
            string text => text,
            _ => MondValue.Undefined,
        };

    private static object? FromMondStateValue(MondValue value) =>
        value.Type switch
        {
            MondValueType.Number => (double)value,
            MondValueType.String => (string)value,
            MondValueType.True => true,
            MondValueType.False => false,
            MondValueType.Null or MondValueType.Undefined => null,
            _ => throw new MondRuntimeException("state values must be a number, string, boolean, or null."),
        };

    private static MondValue CreateMathApi(MondState state)
    {
        var math = MondValue.Object(state);
        math["clamp"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 3)
                return MondValue.Undefined;

            return Math.Clamp((double)args[offset], (double)args[offset + 1], (double)args[offset + 2]);
        });
        return math;
    }

    private static MondValue CreateTypedOscArgument(MondState state, string type, MondValue value)
    {
        var obj = MondValue.Object(state);
        obj["type"] = type;
        obj["value"] = value;
        return obj;
    }

    private static IReadOnlyList<ControlScriptOscArgument> ReadOscArguments(Span<MondValue> values)
    {
        var result = new List<ControlScriptOscArgument>(values.Length);
        foreach (var value in values)
        {
            if (value.Type == MondValueType.Array)
            {
                foreach (var item in value.AsList)
                    result.Add(ReadOscArgument(item));
                continue;
            }

            result.Add(ReadOscArgument(value));
        }

        return result;
    }

    private static ControlScriptOscArgument ReadOscArgument(MondValue value)
    {
        if (value.Type == MondValueType.Object)
        {
            var type = (string)value["type"];
            var argumentValue = value["value"];
            return type switch
            {
                "float32" => ControlScriptOscArgument.Float32((double)argumentValue),
                "double64" => ControlScriptOscArgument.Double64((double)argumentValue),
                "int32" => ControlScriptOscArgument.Int32((int)(double)argumentValue),
                "string" => ControlScriptOscArgument.String((string)argumentValue),
                "boolean" => ControlScriptOscArgument.Boolean(argumentValue),
                _ => ControlScriptOscArgument.String(value.Serialize()),
            };
        }

        return value.Type switch
        {
            MondValueType.Number => ControlScriptOscArgument.Float32((double)value),
            MondValueType.String => ControlScriptOscArgument.String((string)value),
            MondValueType.True => ControlScriptOscArgument.Boolean(true),
            MondValueType.False => ControlScriptOscArgument.Boolean(false),
            _ => ControlScriptOscArgument.String(value.Serialize()),
        };
    }

    private static int ArgumentOffset(Span<MondValue> args) =>
        args.Length > 0 && args[0].Type == MondValueType.Object ? 1 : 0;

    private static int ToInt(MondValue value) =>
        checked((int)(double)value);

    private static bool ToBool(MondValue value) =>
        value.Type switch
        {
            MondValueType.True => true,
            MondValueType.False => false,
            MondValueType.Number => Math.Abs((double)value) > double.Epsilon,
            _ => false,
        };
}
