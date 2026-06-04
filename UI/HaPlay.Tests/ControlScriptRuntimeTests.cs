using HaPlay.ControlGraph;
using HaPlay.Models;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptRuntimeTests
{
    [Fact]
    public void DispatchControlEvent_DoesNotRunScriptsWhenControlSystemIsDisarmed()
    {
        var midiDeviceId = Guid.NewGuid();
        var trigger = MidiCcTrigger(midiDeviceId, "onMidi", midiController: 16);
        var script = Script("Scripts/main.mnd", trigger);
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = false,
                Devices = [MidiDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMidi(event, context) {
                        osc.send("x32", "/should-not-send", osc.float32(1));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 10));

        Assert.Empty(result.Invocations);
        Assert.Empty(sink.OscMessages);
    }

    [Fact]
    public void DispatchControlEvent_InvokesMatchingMidiCcTrigger()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script("Scripts/main.mnd", MidiCcTrigger(midiDeviceId, "onMidi", midiController: 16));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMidi(event, context) {
                        osc.send("x32", "/cc", osc.float32(event.midi.value));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 10));

        var invocation = Assert.Single(result.Invocations);
        Assert.True(invocation.Succeeded);
        var message = Assert.Single(sink.OscMessages);
        Assert.Equal("/cc", message.Address);
        Assert.Equal(10, Assert.Single(message.Arguments).NumberValue);
    }

    [Fact]
    public void DispatchControlEvent_DoesNotMatchDifferentMidiDeviceOrController()
    {
        var firstDeviceId = Guid.NewGuid();
        var secondDeviceId = Guid.NewGuid();
        var script = Script("Scripts/main.mnd", MidiCcTrigger(firstDeviceId, "onMidi", midiController: 16));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(firstDeviceId, "X-Touch Mini A"), MidiDevice(secondDeviceId, "X-Touch Mini B")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMidi(event, context) {
                        osc.send("x32", "/cc", osc.float32(event.midi.value));
                    }
                    """,
            },
            sink);

        var wrongDevice = runtime.DispatchControlEvent(MidiCcEvent(secondDeviceId, controller: 16, value: 10));
        var wrongController = runtime.DispatchControlEvent(MidiCcEvent(firstDeviceId, controller: 17, value: 10));

        Assert.Empty(wrongDevice.Invocations);
        Assert.Empty(wrongController.Invocations);
        Assert.Empty(sink.OscMessages);
    }

    [Fact]
    public void DispatchControlEvent_InvokesMatchingMidiNoteTrigger()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script("Scripts/main.mnd", MidiNoteTrigger(midiDeviceId, "onMidiNote", midiNote: 84));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMidiNote(event, context) {
                        osc.send("x32", "/note", osc.int32(event.midi.note), osc.int32(event.midi.velocity));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(MidiNoteEvent(midiDeviceId, note: 84, velocity: 127, isNoteOn: true));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var message = Assert.Single(sink.OscMessages);
        Assert.Equal("/note", message.Address);
        Assert.Collection(
            message.Arguments,
            arg => Assert.Equal(84, arg.NumberValue),
            arg => Assert.Equal(127, arg.NumberValue));
    }

    [Fact]
    public void DispatchControlEvent_MidiMessageTriggerMatchesCcAndNote()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script(
            "Scripts/main.mnd",
            new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MidiMessage,
                FunctionName = "onMidi",
                DeviceInstanceId = midiDeviceId,
                MidiChannel = 1,
            });
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMidi(event, context) {
                        osc.send("x32", "/" + event.midi.message, osc.int32(event.value));
                    }
                    """,
            },
            sink);

        runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 10));
        runtime.DispatchControlEvent(MidiNoteEvent(midiDeviceId, note: 84, velocity: 127, isNoteOn: true));

        Assert.Collection(
            sink.OscMessages,
            message =>
            {
                Assert.Equal("/controlChange", message.Address);
                Assert.Equal(10, Assert.Single(message.Arguments).NumberValue);
            },
            message =>
            {
                Assert.Equal("/noteOn", message.Address);
                Assert.Equal(127, Assert.Single(message.Arguments).NumberValue);
            });
    }

    [Fact]
    public void DispatchControlEvent_ExecutesBuiltInXTouchMiniX32FaderScriptThroughTriggerRuntime()
    {
        var midiDeviceId = Guid.NewGuid();
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.XTouchMiniX32FadersTemplateId);
        Assert.NotNull(template);

        var script = Script(
            template.SuggestedPath,
            MidiCcTrigger(midiDeviceId, "onXTouchFaderEncoder", midiController: 16));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            sink);

        var result = runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 10));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var message = Assert.Single(sink.OscMessages);
        Assert.Equal("x32", message.DeviceKey);
        Assert.Equal("/ch/01/mix/fader", message.Address);
        Assert.Equal(0.75 + 10.0 / 1023.0, Assert.Single(message.Arguments).NumberValue, precision: 12);
    }

    [Fact]
    public void DispatchControlEvent_UpdatesOscCacheBeforeInvokingOscTrigger()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OscMessage,
            FunctionName = "onOsc",
            DeviceInstanceId = oscDeviceId,
            OscAddressPattern = "/ch/*/mix/fader",
        };
        var script = Script("Scripts/main.mnd", trigger);
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(oscDeviceId, "x32")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onOsc(event, context) {
                        var current = osc.cacheFloat("x32", event.osc.address, 0.0);
                        osc.send("x32", "/seen", osc.float32(current));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(new OscControlEvent(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            oscDeviceId,
            Guid.NewGuid(),
            "/ch/01/mix/fader",
            [OSCArgument.Float32(0.6f)]));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var message = Assert.Single(sink.OscMessages);
        Assert.Equal("/seen", message.Address);
        Assert.Equal(0.6, Assert.Single(message.Arguments).NumberValue, precision: 6);
    }

    [Fact]
    public void DispatchControlEvent_FiresOscCacheChangedTriggerOnIncomingValueChange()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OscCacheChanged,
            FunctionName = "onCache",
            DeviceInstanceId = oscDeviceId,
            OscAddressPattern = "/ch/*/mix/fader",
        };
        var script = Script("Scripts/main.mnd", trigger);
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(oscDeviceId, "x32")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onCache(event, context) {
                        osc.send("x32", "/feedback" + event.osc.address, osc.float32(event.value));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(OscEvent(oscDeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.6f)));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var change = Assert.Single(result.CacheChanges);
        Assert.Equal("/ch/01/mix/fader", change.Key.Address);
        var message = Assert.Single(sink.OscMessages);
        Assert.Equal("/feedback/ch/01/mix/fader", message.Address);
        Assert.Equal(0.6, Assert.Single(message.Arguments).NumberValue, precision: 6);
    }

    [Fact]
    public void DispatchControlEvent_DoesNotFireOscCacheChangedWhenValueIsUnchanged()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OscCacheChanged,
            FunctionName = "onCache",
            DeviceInstanceId = oscDeviceId,
            OscAddressPattern = "/ch/*/mix/fader",
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(oscDeviceId, "x32")],
                Scripts = [Script("Scripts/main.mnd", trigger)],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onCache(event, context) {
                        osc.send("x32", "/feedback", osc.float32(event.value));
                    }
                    """,
            },
            sink);

        runtime.DispatchControlEvent(OscEvent(oscDeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.6f)));
        var second = runtime.DispatchControlEvent(OscEvent(oscDeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.6f)));

        Assert.Empty(second.CacheChanges);
        Assert.Empty(second.Invocations);
        Assert.Single(sink.OscMessages);
    }

    [Fact]
    public void DispatchControlEvent_OscCacheChangedRespectsAddressPatternButStillUpdatesCache()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OscCacheChanged,
            FunctionName = "onCache",
            DeviceInstanceId = oscDeviceId,
            OscAddressPattern = "/ch/*/mix/fader",
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(oscDeviceId, "x32")],
                Scripts = [Script("Scripts/main.mnd", trigger)],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onCache(event, context) {
                        osc.send("x32", "/feedback", osc.float32(event.value));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(OscEvent(oscDeviceId, "/ch/01/mix/pan", OSCArgument.Float32(0.6f)));

        Assert.Single(result.CacheChanges);
        Assert.Empty(result.Invocations);
        Assert.Empty(sink.OscMessages);
    }

    [Fact]
    public void DispatchControlEvent_DoesNotFireOscCacheChangedTriggerWhenDisarmedButStillUpdatesCache()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OscCacheChanged,
            FunctionName = "onCache",
            DeviceInstanceId = oscDeviceId,
            OscAddressPattern = "/ch/*/mix/fader",
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = false,
                Devices = [OscDevice(oscDeviceId, "x32")],
                Scripts = [Script("Scripts/main.mnd", trigger)],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onCache(event, context) {
                        osc.send("x32", "/feedback", osc.float32(event.value));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(OscEvent(oscDeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.6f)));

        Assert.Single(result.CacheChanges);
        Assert.Empty(result.Invocations);
        Assert.Empty(sink.OscMessages);
        Assert.True(runtime.RuntimeServices.OscCache.TryGetNumber("x32", "/ch/01/mix/fader", out var cached));
        Assert.Equal(0.6, cached, precision: 6);
    }

    [Fact]
    public void DispatchDeviceHealthChanged_RunsDeviceHealthTriggerWithState()
    {
        var deviceId = Guid.NewGuid();
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Health",
            ScriptPath = "Scripts/main.mnd",
            Scope = ControlScriptScope.Device,
            DeviceInstanceId = deviceId,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.DeviceHealthChanged,
                    FunctionName = "onHealth",
                    DeviceInstanceId = deviceId,
                },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(deviceId, "x32")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onHealth(event, context) {
                        osc.send("x32", "/health/" + event.state, osc.int32(1));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchDeviceHealthChanged(
            deviceId,
            ControlSessionHealth.Faulted("boom"),
            ControlSessionState.Running);

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.Equal("/health/Faulted", Assert.Single(sink.OscMessages).Address);
    }

    [Fact]
    public void DispatchDeviceHealthChanged_DoesNotRunForDifferentDevice()
    {
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var script = Script(
            "Scripts/main.mnd",
            new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.DeviceHealthChanged,
                FunctionName = "onHealth",
                DeviceInstanceId = deviceA,
            });
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(deviceA, "a"), OscDevice(deviceB, "b")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onHealth(event, context) {
                        osc.send("a", "/health", osc.int32(1));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchDeviceHealthChanged(
            deviceB,
            ControlSessionHealth.Running(),
            ControlSessionState.Starting);

        Assert.Empty(result.Invocations);
        Assert.Empty(sink.OscMessages);
    }

    [Fact]
    public void State_ScriptScopedValuePersistsAcrossInvocations()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script("Scripts/main.mnd", MidiCcTrigger(midiDeviceId, "onMidi", midiController: 16));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMidi(event, context) {
                        var count = state.get("count", 0) + 1;
                        state.set("count", count);
                        osc.send("x32", "/count", osc.int32(count));
                    }
                    """,
            },
            sink);

        runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 1));
        runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 1));

        Assert.Collection(
            sink.OscMessages,
            message => Assert.Equal(1, Assert.Single(message.Arguments).NumberValue),
            message => Assert.Equal(2, Assert.Single(message.Arguments).NumberValue));
    }

    [Fact]
    public void State_ProjectScopeIsSharedAcrossScripts()
    {
        var midiDeviceId = Guid.NewGuid();
        var writer = Script("Scripts/writer.mnd", MidiCcTrigger(midiDeviceId, "onWrite", midiController: 16));
        var reader = Script("Scripts/reader.mnd", MidiCcTrigger(midiDeviceId, "onRead", midiController: 17));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [writer, reader],
            },
            new Dictionary<string, string>
            {
                ["Scripts/writer.mnd"] =
                    """
                    export fun onWrite(event, context) {
                        state.project.set("scene", 7);
                    }
                    """,
                ["Scripts/reader.mnd"] =
                    """
                    export fun onRead(event, context) {
                        osc.send("x32", "/scene", osc.int32(state.project.get("scene", -1)));
                    }
                    """,
            },
            sink);

        runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 1));
        runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 17, value: 1));

        Assert.Equal(7, Assert.Single(Assert.Single(sink.OscMessages).Arguments).NumberValue);
    }

    [Fact]
    public void State_ScriptScopeIsIsolatedBetweenScripts()
    {
        var midiDeviceId = Guid.NewGuid();
        var first = Script("Scripts/first.mnd", MidiCcTrigger(midiDeviceId, "onMidi", midiController: 16));
        var second = Script("Scripts/second.mnd", MidiCcTrigger(midiDeviceId, "onMidi", midiController: 17));
        var sink = new RecordingControlScriptCommandSink();
        const string body =
            """
            export fun onMidi(event, context) {
                var count = state.get("count", 0) + 1;
                state.set("count", count);
                osc.send("x32", "/count", osc.int32(count));
            }
            """;
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [first, second],
            },
            new Dictionary<string, string>
            {
                ["Scripts/first.mnd"] = body,
                ["Scripts/second.mnd"] = body,
            },
            sink);

        runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 1));
        runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 17, value: 1));
        runtime.DispatchControlEvent(MidiCcEvent(midiDeviceId, controller: 16, value: 1));

        Assert.Collection(
            sink.OscMessages,
            message => Assert.Equal(1, Assert.Single(message.Arguments).NumberValue),
            message => Assert.Equal(1, Assert.Single(message.Arguments).NumberValue),
            message => Assert.Equal(2, Assert.Single(message.Arguments).NumberValue));
    }

    [Fact]
    public void State_DeviceScopeIsIsolatedPerDevice()
    {
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var script = Script(
            "Scripts/main.mnd",
            new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.OscMessage,
                FunctionName = "onOsc",
                OscAddressPattern = "*",
            });
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(deviceA, "a"), OscDevice(deviceB, "b")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onOsc(event, context) {
                        var n = state.device.get("n", 0) + 1;
                        state.device.set("n", n);
                        osc.send("out", "/n", osc.int32(n));
                    }
                    """,
            },
            sink);

        runtime.DispatchControlEvent(OscEvent(deviceA, "/x", OSCArgument.Float32(1f)));
        runtime.DispatchControlEvent(OscEvent(deviceA, "/x", OSCArgument.Float32(1f)));
        runtime.DispatchControlEvent(OscEvent(deviceB, "/x", OSCArgument.Float32(1f)));

        Assert.Collection(
            sink.OscMessages,
            message => Assert.Equal(1, Assert.Single(message.Arguments).NumberValue),
            message => Assert.Equal(2, Assert.Single(message.Arguments).NumberValue),
            message => Assert.Equal(1, Assert.Single(message.Arguments).NumberValue));
    }

    [Fact]
    public void DispatchLayerDisabled_RunsLayerScopedDisabledHook()
    {
        var layerId = Guid.NewGuid();
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Layer off",
            ScriptPath = "Scripts/main.mnd",
            Scope = ControlScriptScope.Layer,
            LayerId = layerId,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.LayerDisabled,
                    FunctionName = "onLayerDisabled",
                    LayerId = layerId,
                },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Layers = [new ControlLayerConfig { Id = layerId, Name = "A", IsEnabled = false }],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onLayerDisabled(event, context) {
                        osc.send("x32", "/layer/off", osc.float32(1));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchLayerDisabled(layerId);

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.Equal("/layer/off", Assert.Single(sink.OscMessages).Address);
    }

    [Fact]
    public void DispatchManual_DisablesScriptAfterConfiguredConsecutiveFailures()
    {
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Failing script",
            ScriptPath = "Scripts/main.mnd",
            FailurePolicy = new ControlScriptFailurePolicy
            {
                Mode = ControlScriptFailureMode.DisableScript,
                MaxConsecutiveFailures = 3,
            },
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.Manual,
                    FunctionName = "fail",
                },
            ],
        };
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun fail(event, context) {
                        error("boom");
                    }
                    """,
            });

        runtime.DispatchManual(script.Id);
        runtime.DispatchManual(script.Id);
        var third = runtime.DispatchManual(script.Id);
        var fourth = runtime.DispatchManual(script.Id);

        Assert.False(Assert.Single(third.Invocations).Succeeded);
        Assert.Empty(fourth.Invocations);
        Assert.Equal(3, runtime.Diagnostics.Count(d => d.Stage == ControlScriptDiagnosticStage.Runtime));
        var status = Assert.Single(runtime.ScriptStatuses);
        Assert.True(status.DisabledByFailure);
        Assert.False(status.IsRunnable);
        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.Contains("boom", status.LastError);
    }

    private static ControlScriptRuntime CreateRuntime(
        ControlSystemConfig config,
        IReadOnlyDictionary<string, string> scripts,
        RecordingControlScriptCommandSink? sink = null) =>
        new(
            config,
            new InMemoryControlScriptSourceProvider(scripts),
            new ControlScriptRuntimeServices(sink ?? new RecordingControlScriptCommandSink(), new ControlValueCache()));

    private static ControlDeviceInstanceConfig MidiDevice(Guid id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Protocol = ControlDeviceProtocol.Midi,
            IsEnabled = true,
        };

    private static ControlDeviceInstanceConfig OscDevice(Guid id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = "x32",
            Protocol = ControlDeviceProtocol.Osc,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = name,
                OscHost = "192.168.2.76",
                OscPort = 10023,
            },
        };

    private static ControlScriptConfig Script(string scriptPath, ControlScriptTriggerConfig trigger) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "Script",
            ScriptPath = scriptPath,
            Triggers = [trigger],
        };

    private static ControlScriptTriggerConfig MidiCcTrigger(Guid deviceId, string functionName, int midiController) =>
        new()
        {
            Kind = ControlScriptTriggerKind.MidiControlChange,
            FunctionName = functionName,
            DeviceInstanceId = deviceId,
            MidiChannel = 1,
            MidiController = midiController,
        };

    private static ControlScriptTriggerConfig MidiNoteTrigger(Guid deviceId, string functionName, int midiNote) =>
        new()
        {
            Kind = ControlScriptTriggerKind.MidiNote,
            FunctionName = functionName,
            DeviceInstanceId = deviceId,
            MidiChannel = 1,
            MidiNote = midiNote,
        };

    private static MidiControlEvent MidiCcEvent(Guid deviceId, int controller, int value) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            Channel: 1,
            Controller: controller,
            Value: value,
            HighResolution14Bit: false);

    private static MidiNoteControlEvent MidiNoteEvent(Guid deviceId, int note, int velocity, bool isNoteOn) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            Channel: 1,
            Note: note,
            Velocity: velocity,
            IsNoteOn: isNoteOn);

    private static OscControlEvent OscEvent(Guid deviceId, string address, params OSCArgument[] arguments) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            address,
            arguments);

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
    }
}
