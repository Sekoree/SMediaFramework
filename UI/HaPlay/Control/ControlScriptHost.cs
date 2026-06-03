using Mond;
using Mond.Debugger;
using HaPlay.Models;
using OSCLib;

namespace HaPlay.ControlGraph;

public sealed class ControlScriptHost
{
    private readonly Dictionary<string, MondValue> _state = new(StringComparer.Ordinal);

    public ControlScriptExecutionResult ExecuteWithDiagnostics(
        ScriptTransformControlNodeSettings settings,
        ControlEvent input,
        Guid outputNodeId)
    {
        try
        {
            return new ControlScriptExecutionResult(Execute(settings, input, outputNodeId), []);
        }
        catch (MondCompilerException ex)
        {
            return new ControlScriptExecutionResult(
                [],
                [new ControlScriptDiagnostic(ControlScriptDiagnosticStage.Compile, ex.Message)]);
        }
        catch (MondRuntimeException ex)
        {
            return new ControlScriptExecutionResult(
                [],
                [new ControlScriptDiagnostic(ControlScriptDiagnosticStage.Runtime, ex.Message)]);
        }
    }

    public IReadOnlyList<ControlEvent> Execute(
        ScriptTransformControlNodeSettings settings,
        ControlEvent input,
        Guid outputNodeId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(input);

        var state = CreateState(settings.InstructionLimit);
        var source = WrapSource(settings.Source);
        state.Run(source, "control-script");
        var transform = state["__haplay_transform"];
        if (transform.Type != MondValueType.Function)
            throw new ControlScriptException("Script did not compile to a transform function.");

        var result = state.Call(
            transform,
            ToMondEvent(input, state),
            CreateStateApi(state),
            CreateEmitApi(state),
            CreateMathApi(state),
            CreateX32Api(state));

        return ConvertResult(result, input, outputNodeId);
    }

    private static MondState CreateState(int instructionLimit)
    {
        var state = new MondState
        {
            Libraries = null,
            Options = new MondCompilerOptions
            {
                DebugInfo = MondDebugInfoLevel.Full,
                MakeRootDeclarationsGlobal = true,
                UseImplicitGlobals = true,
            },
            Debugger = new InstructionLimitDebugger(instructionLimit <= 0 ? 100_000 : instructionLimit),
        };
        return state;
    }

    private static string WrapSource(string source) =>
        "fun __haplay_transform(event, state, emit, math, x32) {\n"
        + (source ?? string.Empty)
        + "\n}";

    private static MondValue ToMondEvent(ControlEvent evt, MondState state)
    {
        var obj = MondValue.Object(state);
        obj["type"] = evt switch
        {
            MidiControlEvent => "midi",
            OscControlEvent => "osc",
            ScalarControlEvent => "scalar",
            TextControlEvent => "text",
            BlobControlEvent => "blob",
            _ => "unknown",
        };
        obj["sourceNodeId"] = evt.SourceNodeId.ToString();
        obj["originId"] = evt.OriginId.ToString();
        obj["correlationId"] = evt.CorrelationId.ToString();

        switch (evt)
        {
            case MidiControlEvent midi:
                var midiObj = MondValue.Object(state);
                midiObj["channel"] = midi.Channel;
                midiObj["controller"] = midi.Controller;
                midiObj["value"] = midi.Value;
                midiObj["highResolution14Bit"] = midi.HighResolution14Bit;
                obj["midi"] = midiObj;
                obj["value"] = midi.Value;
                break;
            case OscControlEvent osc:
                var oscObj = MondValue.Object(state);
                oscObj["address"] = osc.Address;
                oscObj["args"] = MondValue.Array(osc.Arguments.Select(ToMondOscArgument));
                obj["osc"] = oscObj;
                if (TryGetOscScalar(osc.Arguments.FirstOrDefault(), out var oscScalar))
                    obj["value"] = oscScalar;
                break;
            case ScalarControlEvent scalar:
                var scalarObj = MondValue.Object(state);
                scalarObj["value"] = scalar.Value;
                obj["scalar"] = scalarObj;
                obj["value"] = scalar.Value;
                break;
            case TextControlEvent text:
                var textObj = MondValue.Object(state);
                textObj["value"] = text.Value;
                obj["text"] = textObj;
                obj["value"] = text.Value;
                break;
        }

        return obj;
    }

    private MondValue CreateStateApi(MondState state)
    {
        var obj = MondValue.Object(state);
        obj["get"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            var name = args.Length > offset ? (string)args[offset] : string.Empty;
            return _state.TryGetValue(name, out var value) ? value : MondValue.Undefined;
        });
        obj["set"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 2)
                return MondValue.Undefined;
            _state[(string)args[offset]] = args[offset + 1];
            return args[offset + 1];
        });
        return obj;
    }

    private static MondValue CreateEmitApi(MondState state)
    {
        var obj = MondValue.Object(state);
        obj["scalar"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateEmitObject(s, "scalar", args.Length > offset ? args[offset] : MondValue.Undefined);
        });
        obj["text"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            return CreateEmitObject(s, "text", args.Length > offset ? args[offset] : string.Empty);
        });
        obj["osc"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            var emit = CreateEmitObject(s, "osc", MondValue.Undefined);
            emit["address"] = args.Length > offset ? args[offset] : string.Empty;
            emit["args"] = args.Length > offset + 1 && args[offset + 1].Type == MondValueType.Array ? args[offset + 1] : MondValue.Array();
            return emit;
        });
        obj["midiCc"] = (MondFunction)((s, args) =>
        {
            var offset = ArgumentOffset(args);
            var emit = CreateEmitObject(s, "midiCc", MondValue.Undefined);
            emit["channel"] = args.Length > offset ? args[offset] : 1;
            emit["controller"] = args.Length > offset + 1 ? args[offset + 1] : 0;
            emit["value"] = args.Length > offset + 2 ? args[offset + 2] : 0;
            emit["highResolution14Bit"] = args.Length > offset + 3 ? args[offset + 3] : false;
            return emit;
        });
        return obj;
    }

    private static MondValue CreateMathApi(MondState state)
    {
        var obj = MondValue.Object(state);
        obj["map"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            if (args.Length < offset + 5)
                return MondValue.Undefined;
            return ControlMath.MapRange((double)args[offset], (double)args[offset + 1], (double)args[offset + 2], (double)args[offset + 3], (double)args[offset + 4], clamp: true);
        });
        return obj;
    }

    private static MondValue CreateX32Api(MondState state)
    {
        var obj = MondValue.Object(state);
        obj["faderToDb"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            return args.Length > offset ? X32Fader.FromNormalized((double)args[offset]) : MondValue.Undefined;
        });
        obj["dbToFader"] = (MondFunction)((_, args) =>
        {
            var offset = ArgumentOffset(args);
            return args.Length > offset ? X32Fader.ToNormalized((double)args[offset]) : MondValue.Undefined;
        });
        return obj;
    }

    private static int ArgumentOffset(Span<MondValue> args) =>
        args.Length > 0 && args[0].Type == MondValueType.Object ? 1 : 0;

    private static MondValue CreateEmitObject(MondState state, string type, MondValue value)
    {
        var obj = MondValue.Object(state);
        obj["type"] = type;
        if (value.Type != MondValueType.Undefined)
            obj["value"] = value;
        return obj;
    }

    private static IReadOnlyList<ControlEvent> ConvertResult(MondValue result, ControlEvent input, Guid outputNodeId)
    {
        if (result.Type == MondValueType.Undefined || result.Type == MondValueType.Null)
            return [];
        if (result.Type == MondValueType.Array)
            return result.AsList.SelectMany(v => ConvertResult(v, input, outputNodeId)).ToArray();
        if (result.Type != MondValueType.Object)
            return [];

        var type = (string)result["type"];
        var now = DateTimeOffset.UtcNow;
        return type switch
        {
            "scalar" => [new ScalarControlEvent(now, outputNodeId, input.OriginId, input.CorrelationId, GetNumber(result, "value"), input.Path)],
            "text" => [new TextControlEvent(now, outputNodeId, input.OriginId, input.CorrelationId, (string)result["value"], input.Path)],
            "osc" => [new OscControlEvent(now, outputNodeId, input.OriginId, input.CorrelationId, (string)result["address"], ReadOscArguments(result["args"]), input.Path)],
            "midiCc" => [new MidiControlEvent(now, outputNodeId, input.OriginId, input.CorrelationId, (int)GetNumber(result, "channel"), (int)GetNumber(result, "controller"), (int)GetNumber(result, "value"), GetBool(result, "highResolution14Bit"), input.Path)],
            _ => [],
        };
    }

    private static double GetNumber(MondValue obj, string field)
    {
        var value = obj[field];
        return value.Type == MondValueType.Number ? (double)value : 0;
    }

    private static bool GetBool(MondValue obj, string field)
    {
        var value = obj[field];
        return value.Type switch
        {
            MondValueType.True => true,
            MondValueType.False => false,
            _ => false,
        };
    }

    private static IReadOnlyList<OSCArgument> ReadOscArguments(MondValue args)
    {
        if (args.Type != MondValueType.Array)
            return [];
        return args.AsList.Select(ReadOscArgument).ToArray();
    }

    private static OSCArgument ReadOscArgument(MondValue value) =>
        value.Type switch
        {
            MondValueType.Number => OSCArgument.Float32((float)(double)value),
            MondValueType.String => OSCArgument.String((string)value),
            MondValueType.True => OSCArgument.True(),
            MondValueType.False => OSCArgument.False(),
            _ => OSCArgument.Nil(),
        };

    private static MondValue ToMondOscArgument(OSCArgument argument) =>
        argument.Type switch
        {
            OSCArgumentType.Float32 => argument.AsFloat32(),
            OSCArgumentType.Double64 => argument.AsDouble64(),
            OSCArgumentType.Int32 => argument.AsInt32(),
            OSCArgumentType.Int64 => argument.AsInt64(),
            OSCArgumentType.String or OSCArgumentType.Symbol => argument.AsString(),
            OSCArgumentType.True => MondValue.True,
            OSCArgumentType.False => MondValue.False,
            _ => MondValue.Undefined,
        };

    private static bool TryGetOscScalar(OSCArgument argument, out double value)
    {
        switch (argument.Type)
        {
            case OSCArgumentType.Float32:
                value = argument.AsFloat32();
                return true;
            case OSCArgumentType.Double64:
                value = argument.AsDouble64();
                return true;
            case OSCArgumentType.Int32:
                value = argument.AsInt32();
                return true;
            case OSCArgumentType.Int64:
                value = argument.AsInt64();
                return true;
            case OSCArgumentType.True:
                value = 1;
                return true;
            case OSCArgumentType.False:
                value = 0;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}

public sealed class ControlScriptException : Exception
{
    public ControlScriptException(string message)
        : base(message)
    {
    }
}

public sealed record ControlScriptExecutionResult(
    IReadOnlyList<ControlEvent> Events,
    IReadOnlyList<ControlScriptDiagnostic> Diagnostics);

public sealed record ControlScriptDiagnostic(
    ControlScriptDiagnosticStage Stage,
    string Message);

public enum ControlScriptDiagnosticStage
{
    Compile,
    Runtime,
}

internal sealed class InstructionLimitDebugger : MondDebugger
{
    private readonly int _instructionLimit;
    private int _count;

    public InstructionLimitDebugger(int instructionLimit)
    {
        _instructionLimit = instructionLimit;
    }

    protected override bool ShouldBreak(MondProgram program, int address) =>
        ++_count >= _instructionLimit;

    protected override MondDebugAction OnBreak(MondDebugContext context, int address) =>
        throw new MondRuntimeException("Script execution timed out.");
}
