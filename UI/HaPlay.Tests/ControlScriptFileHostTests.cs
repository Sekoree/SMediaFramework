using S.Control;
using Mond;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptFileHostTests
{
    [Fact]
    public void LoadModule_DetectsExportedFunctions()
    {
        var host = CreateHost(new Dictionary<string, string>
        {
            ["Scripts/main.mnd"] =
                """
                export fun first() -> 1;
                export fun second() -> 2;
                export const notAFunction = 3;
                """,
        });

        var module = host.LoadModule("Scripts/main.mnd");

        Assert.Equal(["first", "second"], module.ExportedFunctionNames);
        Assert.True(module.TryGetExportedFunction("first", out var first));
        Assert.Equal(MondValueType.Function, first.Type);
        Assert.False(module.TryGetExportedFunction("notAFunction", out _));
    }

    [Fact]
    public void Invoke_UsesMondImportsFromProjectRelativeScriptFiles()
    {
        var host = CreateHost(new Dictionary<string, string>
        {
            ["Scripts/helpers/faders.mnd"] =
                """
                export const step = 1.0 / 1023.0;

                export fun apply(current, deltaSteps) {
                    return current + deltaSteps * step;
                }
                """,
            ["Scripts/main.mnd"] =
                """
                from 'helpers/faders' import { apply };

                export fun moveFader(current, deltaSteps) {
                    return apply(current, deltaSteps);
                }
                """,
        });

        var result = host.Invoke("Scripts/main.mnd", "moveFader", 0.75, 10.0);

        Assert.Equal(0.75 + 10.0 / 1023.0, (double)result, precision: 12);
    }

    [Fact]
    public void Invoke_AddsMondExtensionWhenOmitted()
    {
        var host = CreateHost(new Dictionary<string, string>
        {
            ["Scripts/main.mnd"] = "export fun value() -> 42;",
        });

        var result = host.Invoke("Scripts/main", "value");

        Assert.Equal(42, (double)result);
    }

    [Fact]
    public void LoadModule_RejectsUnsafeProjectPaths()
    {
        var host = CreateHost(new Dictionary<string, string>
        {
            ["Scripts/main.mnd"] = "export fun value() -> 42;",
        });

        Assert.Throws<ControlScriptException>(() => host.LoadModule("../outside.mnd"));
        Assert.Throws<ControlScriptException>(() => host.LoadModule("/tmp/outside.mnd"));
    }

    [Fact]
    public void Invoke_MissingExportedFunctionThrows()
    {
        var host = CreateHost(new Dictionary<string, string>
        {
            ["Scripts/main.mnd"] = "export fun value() -> 42;",
        });

        var ex = Assert.Throws<ControlScriptException>(() => host.Invoke("Scripts/main.mnd", "missing"));

        Assert.Contains("does not export function 'missing'", ex.Message);
    }

    [Fact]
    public void Invoke_EnforcesInstructionLimit()
    {
        var host = CreateHost(
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun spin() {
                        while (true) { }
                    }
                    """,
            },
            instructionLimit: 100);

        Assert.Throws<MondRuntimeException>(() => host.Invoke("Scripts/main.mnd", "spin"));
    }

    [Fact]
    public void BuiltInXTouchMiniX32FaderTemplate_CompilesAsExportedScriptModule()
    {
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.XTouchMiniX32FadersTemplateId);
        Assert.NotNull(template);

        var host = CreateHost(new Dictionary<string, string>
        {
            [template.SuggestedPath] = template.Source,
        });

        var module = host.LoadModule(template.SuggestedPath);

        Assert.Contains("onXTouchFaderEncoder", module.ExportedFunctionNames);
    }

    [Fact]
    public void BuiltInXTouchMiniX32FaderTemplate_SendsX32FaderOscFromMidiEncoder()
    {
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.XTouchMiniX32FadersTemplateId);
        Assert.NotNull(template);

        var sink = new RecordingControlScriptCommandSink();
        var cache = new ControlValueCache();
        var runtimeServices = new ControlScriptRuntimeServices(sink, cache);
        var host = CreateHost(
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            runtimeServices: runtimeServices);
        var module = host.LoadModule(template.SuggestedPath);

        module.Invoke("onXTouchFaderEncoder", CreateMidiCcEvent(module.State, 16, 10), MondValue.Object(module.State));

        var message = Assert.Single(sink.OscMessages);
        Assert.Equal("x32", message.DeviceKey);
        Assert.Equal("/ch/01/mix/fader", message.Address);
        var argument = Assert.Single(message.Arguments);
        Assert.Equal(ControlScriptOscArgumentType.Float32, argument.Type);
        Assert.Equal(0.75 + 10.0 / 1023.0, argument.NumberValue, precision: 12);
        Assert.Equal(argument.NumberValue, cache.GetNumberOrDefault("x32", "/ch/01/mix/fader", 0), precision: 12);
    }

    [Fact]
    public void BuiltInXTouchMiniX32FaderTemplate_UsesCachedFaderValueAndNegativeEncoderDelta()
    {
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.XTouchMiniX32FadersTemplateId);
        Assert.NotNull(template);

        var sink = new RecordingControlScriptCommandSink();
        var cache = new ControlValueCache();
        cache.SetNumber("x32", "/ch/08/mix/fader", 0.5, ControlValueCacheSource.Incoming);
        var host = CreateHost(
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            runtimeServices: new ControlScriptRuntimeServices(sink, cache));
        var module = host.LoadModule(template.SuggestedPath);

        module.Invoke("onXTouchFaderEncoder", CreateMidiCcEvent(module.State, 23, 72), MondValue.Object(module.State));

        var message = Assert.Single(sink.OscMessages);
        Assert.Equal("/ch/08/mix/fader", message.Address);
        var argument = Assert.Single(message.Arguments);
        Assert.Equal(0.5 - 8.0 / 1023.0, argument.NumberValue, precision: 12);
    }

    [Fact]
    public void ControlValueCache_MarksDeviceValuesStale()
    {
        var cache = new ControlValueCache();
        cache.SetNumber("x32", "/ch/01/mix/fader", 0.25, ControlValueCacheSource.Incoming);

        cache.MarkDeviceStale("x32");

        Assert.False(cache.TryGetNumber("x32", "/ch/01/mix/fader", out _));
        var entry = Assert.Single(cache.Entries);
        Assert.True(entry.IsStale);
    }

    private static ControlScriptFileHost CreateHost(
        IReadOnlyDictionary<string, string> scripts,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit,
        ControlScriptRuntimeServices? runtimeServices = null) =>
        new(new InMemoryControlScriptSourceProvider(scripts), instructionLimit, runtimeServices);

    private static MondValue CreateMidiCcEvent(MondState state, int controller, int value)
    {
        var evt = MondValue.Object(state);
        var midi = MondValue.Object(state);
        midi["controller"] = controller;
        midi["value"] = value;
        evt["midi"] = midi;
        return evt;
    }

    private sealed class RecordingControlScriptCommandSink : IControlScriptCommandSink
    {
        public List<ControlScriptOscMessage> OscMessages { get; } = new();

        public List<ControlScriptMidiMessage> MidiMessages { get; } = new();

        public void SendOsc(ControlScriptOscMessage message)
        {
            OscMessages.Add(message);
        }

        public void SendMidi(ControlScriptMidiMessage message)
        {
            MidiMessages.Add(message);
        }

        public void RequestActivateLayer(string layerKey)
        {
        }
    }
}
