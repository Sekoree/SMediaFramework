using Mond;
using Mond.Libraries;

namespace S.Control;

public sealed class ControlScriptRuntimeServices
{
    public ControlScriptRuntimeServices(
        IControlScriptCommandSink? commandSink = null,
        ControlValueCache? oscCache = null,
        ControlScriptStateStore? stateStore = null,
        IControlMonitorSink? monitor = null,
        IReadOnlyList<ControlDeviceInstanceConfig>? devices = null,
        ControlDeviceHealthRegistry? deviceHealth = null,
        Func<DateTimeOffset>? clock = null,
        IReadOnlyList<ControlLayerConfig>? layers = null,
        IReadOnlyList<ControlDeviceProfile>? profiles = null)
    {
        CommandSink = commandSink ?? NullControlScriptCommandSink.Instance;
        OscCache = oscCache ?? new ControlValueCache();
        StateStore = stateStore ?? new ControlScriptStateStore();
        Monitor = monitor ?? NullControlMonitorSink.Instance;
        Devices = devices ?? [];
        DeviceHealth = deviceHealth ?? new ControlDeviceHealthRegistry();
        Clock = clock ?? (() => DateTimeOffset.UtcNow);
        Layers = layers ?? [];
        // Default to the built-in device profiles so their embedded helpers (e.g. x32) and command data are
        // available to scripts out of the box — the old always-on C# x32 module behaved this way. The session
        // passes an explicit resolved set (built-ins + config overrides).
        Profiles = profiles ?? BuiltInProfileLoader.Load();
    }

    public IControlScriptCommandSink CommandSink { get; }

    public ControlValueCache OscCache { get; }

    public ControlScriptStateStore StateStore { get; }

    public IControlMonitorSink Monitor { get; }

    public IReadOnlyList<ControlDeviceInstanceConfig> Devices { get; }

    public ControlDeviceHealthRegistry DeviceHealth { get; }

    /// <summary>Host clock backing the <c>HaPlay.Time</c> library; injectable so script time is testable.</summary>
    public Func<DateTimeOffset> Clock { get; }

    /// <summary>Configured layers, surfaced to the script <c>layers</c> library.</summary>
    public IReadOnlyList<ControlLayerConfig> Layers { get; }

    /// <summary>Loaded device profiles (built-in JSON + config overrides), surfaced to scripts via
    /// <c>devices.command(id)</c> so profile-embedded helpers can read address data instead of hardcoding it.</summary>
    public IReadOnlyList<ControlDeviceProfile> Profiles { get; }

    /// <summary>Returns the currently active layer id; set by the runtime so <c>layers.active()</c> can read it.</summary>
    public Func<Guid?>? ActiveLayerProvider { get; set; }
}

public interface IControlScriptCommandSink
{
    void SendOsc(ControlScriptOscMessage message);

    void SendMidi(ControlScriptMidiMessage message);

    /// <summary>Requests a mutually-exclusive layer switch (by id or name). Applied by the session after the
    /// current dispatch completes, so it never re-enters the runtime mid-script.</summary>
    void RequestActivateLayer(string layerKey);
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

    public void RequestActivateLayer(string layerKey)
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

    public static ControlScriptOscArgument Int64(long value) =>
        new(ControlScriptOscArgumentType.Int64, value, null, false);

    public static ControlScriptOscArgument String(string value) =>
        new(ControlScriptOscArgumentType.String, 0, value, false);

    public static ControlScriptOscArgument Symbol(string value) =>
        new(ControlScriptOscArgumentType.Symbol, 0, value, false);

    public static ControlScriptOscArgument Nil() =>
        new(ControlScriptOscArgumentType.Nil, 0, null, false);

    public static ControlScriptOscArgument Boolean(bool value) =>
        new(value ? ControlScriptOscArgumentType.True : ControlScriptOscArgumentType.False, 0, null, value);
}

public enum ControlScriptOscArgumentType
{
    Float32,
    Double64,
    Int32,
    Int64,
    String,
    Symbol,
    True,
    False,
    Nil,
}

public sealed record ControlScriptMidiMessage(
    string DeviceKey,
    ControlScriptMidiMessageKind Kind,
    int? Channel = null,
    int? Controller = null,
    int? Note = null,
    int? Velocity = null,
    int? Value = null,
    bool HighResolution14Bit = false,
    int? Parameter = null,
    byte[]? Data = null);

public enum ControlScriptMidiMessageKind
{
    ControlChange,
    NoteOn,
    NoteOff,
    ProgramChange,
    PitchBend,
    PolyphonicAftertouch,
    ChannelAftertouch,
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
    NRPN,
    RPN,
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
        yield return new KeyValuePair<string, MondValue>("math", CreateMathApi(state));
        yield return new KeyValuePair<string, MondValue>("state", CreateStateApi(state));
        yield return new KeyValuePair<string, MondValue>("monitor", CreateMonitorApi(state));
        yield return new KeyValuePair<string, MondValue>("devices", CreateDevicesApi(state));
        yield return new KeyValuePair<string, MondValue>("time", CreateTimeApi(state));
        yield return new KeyValuePair<string, MondValue>("layers", CreateLayersApi(state));
    }

    // HaPlay owns mutually-exclusive layers. activate() queues a switch that the host applies after the
    // current script finishes (so it never re-enters the runtime mid-dispatch); LayerEnabled/LayerDisabled
    // triggers then fire for the new/previous layer.
    private MondValue CreateLayersApi(MondState state)
    {
        var layers = MondValue.Object(state);

        layers["activate"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("layers.activate requires a layer id or name.");

            _services.CommandSink.RequestActivateLayer((string)args[offset]);
            return MondValue.Undefined;
        });
        layers["list"] = (MondFunction)((_, _) =>
            MondValue.Array(_services.Layers.Select(layer => (MondValue)layer.Name)));
        layers["active"] = (MondFunction)((_, _) =>
        {
            var activeId = _services.ActiveLayerProvider?.Invoke();
            var layer = activeId is { } id ? _services.Layers.FirstOrDefault(l => l.Id == id) : null;
            return layer is null ? MondValue.Null : layer.Name;
        });

        return layers;
    }

    // Host-controlled clock for scripts. Recurring/delayed execution is provided by the declarative
    // Periodic trigger (bind an exported function to it) so scripts never spin loops; this library is
    // the time-reading surface for debounce/elapsed/timestamp logic.
    private MondValue CreateTimeApi(MondState state)
    {
        var time = MondValue.Object(state);

        time["now"] = (MondFunction)((_, _) =>
            (double)_services.Clock().ToUnixTimeMilliseconds());
        time["nowIso"] = (MondFunction)((_, _) =>
            _services.Clock().ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        return time;
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
        // Look up a profile command by its (globally-unique) id and expose its data — address, value kind, cache
        // key, range. Profile-embedded helper scripts call this instead of re-deriving device-specific addresses,
        // so the address string lives once in the profile's command data and the runtime stays device-agnostic.
        devices["command"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("devices.command requires a command id.");

            var id = (string)args[offset];
            var command = _services.Profiles
                .SelectMany(profile => profile.Commands)
                .FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            return command is null ? MondValue.Null : ToMondCommand(s, command);
        });

        return devices;
    }

    private static MondValue ToMondCommand(MondState state, ControlCommandProfile command)
    {
        var obj = MondValue.Object(state);
        obj["id"] = command.Id;
        obj["displayName"] = command.DisplayName;
        obj["address"] = command.Address;
        obj["valueKind"] = command.ValueKind.ToString();
        obj["access"] = command.Access.ToString();
        obj["cacheKey"] = command.CacheKey;
        obj["minValue"] = command.MinValue.HasValue ? command.MinValue.Value : MondValue.Null;
        obj["maxValue"] = command.MaxValue.HasValue ? command.MaxValue.Value : MondValue.Null;
        return obj;
    }

    private MondValue ToMondDevice(MondState state, ControlDeviceInstanceConfig device)
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
    private ControlDeviceInstanceConfig? ResolveDevice(string key)
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
        osc["int64"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "int64", args.Length > offset ? args[offset] : 0.0);
        });
        osc["string"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "string", args.Length > offset ? args[offset] : string.Empty);
        });
        osc["symbol"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "symbol", args.Length > offset ? args[offset] : string.Empty);
        });
        osc["boolean"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateTypedOscArgument(s, "boolean", args.Length > offset ? args[offset] : false);
        });
        osc["nil"] = (MondFunction)((s, _) =>
            CreateTypedOscArgument(s, "nil", MondValue.Null));
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
        osc["request"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("osc.request requires device key and address.");

            // A value request is an OSC message with no arguments (e.g. X32 replies with the current value).
            _services.CommandSink.SendOsc(new ControlScriptOscMessage(
                (string)args[offset],
                (string)args[offset + 1],
                []));
            return MondValue.Undefined;
        });
        osc["has"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("osc.has requires device key and address.");

            return _services.OscCache.Has((string)args[offset], (string)args[offset + 1]);
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
        osc["cacheString"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("osc.cacheString requires device key and address.");

            var defaultValue = args.Length > offset + 2 && args[offset + 2].Type == MondValueType.String
                ? (string)args[offset + 2]
                : string.Empty;

            return _services.OscCache.GetStringOrDefault((string)args[offset], (string)args[offset + 1], defaultValue);
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
        midi["sendHighResCc"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 4)
                throw new MondRuntimeException("midi.sendHighResCc requires device key, channel, controller, and 14-bit value.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.ControlChange,
                ToInt(args[offset + 1]),
                Controller: ToInt(args[offset + 2]),
                Value: ToInt(args[offset + 3]),
                HighResolution14Bit: true));
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
        midi["sendPolyphonicAftertouch"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 4)
                throw new MondRuntimeException("midi.sendPolyphonicAftertouch requires device key, channel, note, and pressure.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.PolyphonicAftertouch,
                ToInt(args[offset + 1]),
                Note: ToInt(args[offset + 2]),
                Value: ToInt(args[offset + 3])));
            return MondValue.Undefined;
        });
        midi["sendPolyAftertouch"] = midi["sendPolyphonicAftertouch"];
        midi["sendChannelAftertouch"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 3)
                throw new MondRuntimeException("midi.sendChannelAftertouch requires device key, channel, and pressure.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.ChannelAftertouch,
                ToInt(args[offset + 1]),
                Value: ToInt(args[offset + 2])));
            return MondValue.Undefined;
        });
        midi["sendSysEx"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("midi.sendSysEx requires device key and one or more bytes.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.SysEx,
                Data: NormalizeSysExBytes(ReadMidiBytes(args[(offset + 1)..], "midi.sendSysEx"))));
            return MondValue.Undefined;
        });
        midi["sendMidiTimeCode"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("midi.sendMidiTimeCode requires device key and data byte.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.MIDITimeCode,
                Value: ToInt(args[offset + 1])));
            return MondValue.Undefined;
        });
        midi["sendMidiTimeCodeQuarterFrame"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 3)
                throw new MondRuntimeException("midi.sendMidiTimeCodeQuarterFrame requires device key, quarter-frame type, and nibble.");

            var messageType = ToInt(args[offset + 1]);
            var nibble = ToInt(args[offset + 2]);
            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.MIDITimeCode,
                Value: ((messageType & 0x07) << 4) | (nibble & 0x0F)));
            return MondValue.Undefined;
        });
        midi["sendSongPosition"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("midi.sendSongPosition requires device key and beat position.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.SongPosition,
                Value: ToInt(args[offset + 1])));
            return MondValue.Undefined;
        });
        midi["sendSongSelect"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                throw new MondRuntimeException("midi.sendSongSelect requires device key and song number.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.SongSelect,
                Value: ToInt(args[offset + 1])));
            return MondValue.Undefined;
        });
        midi["sendTuneRequest"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("midi.sendTuneRequest requires device key.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.TuneRequest));
            return MondValue.Undefined;
        });
        midi["sendTimingClock"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("midi.sendTimingClock requires device key.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.TimingClock));
            return MondValue.Undefined;
        });
        midi["sendClock"] = midi["sendTimingClock"];
        midi["sendStart"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("midi.sendStart requires device key.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.Start));
            return MondValue.Undefined;
        });
        midi["sendContinue"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("midi.sendContinue requires device key.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.Continue));
            return MondValue.Undefined;
        });
        midi["sendStop"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("midi.sendStop requires device key.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.Stop));
            return MondValue.Undefined;
        });
        midi["sendActiveSensing"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("midi.sendActiveSensing requires device key.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.ActiveSensing));
            return MondValue.Undefined;
        });
        midi["sendReset"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length <= offset)
                throw new MondRuntimeException("midi.sendReset requires device key.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.Reset));
            return MondValue.Undefined;
        });
        midi["sendNrpn"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 4)
                throw new MondRuntimeException("midi.sendNrpn requires device key, channel, parameter, and value.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.NRPN,
                ToInt(args[offset + 1]),
                Parameter: ToInt(args[offset + 2]),
                Value: ToInt(args[offset + 3])));
            return MondValue.Undefined;
        });
        midi["sendRpn"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 4)
                throw new MondRuntimeException("midi.sendRpn requires device key, channel, parameter, and value.");

            _services.CommandSink.SendMidi(new ControlScriptMidiMessage(
                (string)args[offset],
                ControlScriptMidiMessageKind.RPN,
                ToInt(args[offset + 1]),
                Parameter: ToInt(args[offset + 2]),
                Value: ToInt(args[offset + 3])));
            return MondValue.Undefined;
        });

        return midi;
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

    private static byte[] ReadMidiBytes(Span<MondValue> values, string helperName)
    {
        var result = new List<byte>(values.Length);
        foreach (var value in values)
        {
            if (value.Type == MondValueType.Array)
            {
                foreach (var item in value.AsList)
                    result.Add(ToMidiByte(item, helperName));
                continue;
            }

            result.Add(ToMidiByte(value, helperName));
        }

        if (result.Count == 0)
            throw new MondRuntimeException($"{helperName} requires at least one MIDI byte.");

        return result.ToArray();
    }

    private static byte ToMidiByte(MondValue value, string helperName)
    {
        var number = ToInt(value);
        if (number is < 0 or > 255)
            throw new MondRuntimeException($"{helperName} byte values must be in the range 0..255.");

        return (byte)number;
    }

    private static byte[] NormalizeSysExBytes(byte[] data)
    {
        var needsStart = data[0] != 0xF0;
        var needsEnd = data[^1] != 0xF7;
        if (!needsStart && !needsEnd)
            return data;

        var result = new byte[data.Length + (needsStart ? 1 : 0) + (needsEnd ? 1 : 0)];
        var index = 0;
        if (needsStart)
            result[index++] = 0xF0;
        Array.Copy(data, 0, result, index, data.Length);
        if (needsEnd)
            result[^1] = 0xF7;
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
                "int64" => ControlScriptOscArgument.Int64((long)(double)argumentValue),
                "string" => ControlScriptOscArgument.String((string)argumentValue),
                "symbol" => ControlScriptOscArgument.Symbol((string)argumentValue),
                "boolean" => ControlScriptOscArgument.Boolean(argumentValue),
                "nil" => ControlScriptOscArgument.Nil(),
                _ => ControlScriptOscArgument.String(value.Serialize()),
            };
        }

        return value.Type switch
        {
            MondValueType.Number => ControlScriptOscArgument.Float32((double)value),
            MondValueType.String => ControlScriptOscArgument.String((string)value),
            MondValueType.True => ControlScriptOscArgument.Boolean(true),
            MondValueType.False => ControlScriptOscArgument.Boolean(false),
            MondValueType.Null or MondValueType.Undefined => ControlScriptOscArgument.Nil(),
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
