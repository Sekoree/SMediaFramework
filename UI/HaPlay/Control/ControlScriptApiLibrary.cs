using Mond;
using Mond.Libraries;

namespace HaPlay.ControlGraph;

public sealed class ControlScriptRuntimeServices
{
    public ControlScriptRuntimeServices(
        IControlScriptCommandSink? commandSink = null,
        ControlValueCache? oscCache = null)
    {
        CommandSink = commandSink ?? NullControlScriptCommandSink.Instance;
        OscCache = oscCache ?? new ControlValueCache();
    }

    public IControlScriptCommandSink CommandSink { get; }

    public ControlValueCache OscCache { get; }
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
    }

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
