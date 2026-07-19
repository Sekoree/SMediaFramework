using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptRuntimeTests
{
    [Fact]
    public void DispatchControlEvent_DoesNotRunScriptsWhenControlSystemIsDisarmed()
    {
        var midiDeviceId = Guid.NewGuid();
        var trigger = MIDICcTrigger(midiDeviceId, "onMIDI", midiController: 16);
        var script = Script("Scripts/main.mnd", trigger);
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = false,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMIDI(event, context) {
                        osc.send("x32", "/should-not-send", osc.float32(1));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 10));

        Assert.Empty(result.Invocations);
        Assert.Empty(sink.OSCMessages);
    }

    [Fact]
    public void DispatchControlEvent_InvokesMatchingMIDICcTrigger()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script("Scripts/main.mnd", MIDICcTrigger(midiDeviceId, "onMIDI", midiController: 16));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMIDI(event, context) {
                        osc.send("x32", "/cc", osc.float32(event.midi.value));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 10));

        var invocation = Assert.Single(result.Invocations);
        Assert.True(invocation.Succeeded);
        var message = Assert.Single(sink.OSCMessages);
        Assert.Equal("/cc", message.Address);
        Assert.Equal(10, Assert.Single(message.Arguments).NumberValue);
    }

    [Fact]
    public void DispatchControlEvent_MIDICcTriggerRespectsValueRange()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script(
            "Scripts/main.mnd",
            new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MIDIControlChange,
                FunctionName = "onMIDI",
                DeviceInstanceId = midiDeviceId,
                MIDIController = 16,
                MIDIValueMin = 100,
                MIDIValueMax = 200,
            });
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "Fader")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMIDI(event, context) {
                        osc.send("x32", "/cc", osc.float32(event.midi.value));
                    }
                    """,
            },
            sink);

        Assert.Empty(runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 99)).Invocations);
        Assert.Empty(runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 201)).Invocations);
        var result = runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 150));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.Equal(150, Assert.Single(Assert.Single(sink.OSCMessages).Arguments).NumberValue);
    }

    [Fact]
    public void DispatchControlEvent_DoesNotMatchDifferentMIDIDeviceOrController()
    {
        var firstDeviceId = Guid.NewGuid();
        var secondDeviceId = Guid.NewGuid();
        var script = Script("Scripts/main.mnd", MIDICcTrigger(firstDeviceId, "onMIDI", midiController: 16));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(firstDeviceId, "X-Touch Mini A"), MIDIDevice(secondDeviceId, "X-Touch Mini B")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMIDI(event, context) {
                        osc.send("x32", "/cc", osc.float32(event.midi.value));
                    }
                    """,
            },
            sink);

        var wrongDevice = runtime.DispatchControlEvent(MIDICcEvent(secondDeviceId, controller: 16, value: 10));
        var wrongController = runtime.DispatchControlEvent(MIDICcEvent(firstDeviceId, controller: 17, value: 10));

        Assert.Empty(wrongDevice.Invocations);
        Assert.Empty(wrongController.Invocations);
        Assert.Empty(sink.OSCMessages);
    }

    [Fact]
    public void DispatchControlEvent_InvokesMatchingMIDINoteTrigger()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script("Scripts/main.mnd", MIDINoteTrigger(midiDeviceId, "onMIDINote", midiNote: 84));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMIDINote(event, context) {
                        osc.send("x32", "/note", osc.int32(event.midi.note), osc.int32(event.midi.velocity));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(MIDINoteEvent(midiDeviceId, note: 84, velocity: 127, isNoteOn: true));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var message = Assert.Single(sink.OSCMessages);
        Assert.Equal("/note", message.Address);
        Assert.Collection(
            message.Arguments,
            arg => Assert.Equal(84, arg.NumberValue),
            arg => Assert.Equal(127, arg.NumberValue));
    }

    [Fact]
    public void DispatchControlEvent_MIDIMessageTriggerMatchesCcAndNote()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script(
            "Scripts/main.mnd",
            new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MIDIMessage,
                FunctionName = "onMIDI",
                DeviceInstanceId = midiDeviceId,
                MIDIChannel = 1,
            });
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMIDI(event, context) {
                        osc.send("x32", "/" + event.midi.message, osc.int32(event.value));
                    }
                    """,
            },
            sink);

        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 10));
        runtime.DispatchControlEvent(MIDINoteEvent(midiDeviceId, note: 84, velocity: 127, isNoteOn: true));

        Assert.Collection(
            sink.OSCMessages,
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
    public void DispatchControlEvent_MIDIMessageTriggerMatchesProgramChangeAndExposesPayload()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script(
            "Scripts/main.mnd",
            new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MIDIMessage,
                FunctionName = "onMIDI",
                DeviceInstanceId = midiDeviceId,
                MIDIMessageType = ControlMIDIMessageType.ProgramChange,
                MIDIChannel = 1,
                MIDIValue = 5,
            });
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "Program Surface")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMIDI(event, context) {
                        osc.send("x32", "/" + event.midi.message, osc.int32(event.midi.program), osc.string(event.midi.messageType));
                    }
                    """,
            },
            sink);

        var ignored = runtime.DispatchControlEvent(MIDIMessageEvent(
            midiDeviceId,
            new ControlMIDIMessagePayload
            {
                MessageType = ControlMIDIMessageType.ProgramChange,
                Channel = 1,
                Program = 6,
                Value = 6,
            }));
        var result = runtime.DispatchControlEvent(MIDIMessageEvent(
            midiDeviceId,
            new ControlMIDIMessagePayload
            {
                MessageType = ControlMIDIMessageType.ProgramChange,
                Channel = 1,
                Program = 5,
                Value = 5,
            }));

        Assert.Empty(ignored.Invocations);
        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var message = Assert.Single(sink.OSCMessages);
        Assert.Equal("/programChange", message.Address);
        Assert.Collection(
            message.Arguments,
            arg => Assert.Equal(5, arg.NumberValue),
            arg => Assert.Equal("ProgramChange", arg.StringValue));
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
            MIDICcTrigger(midiDeviceId, "onXTouchFaderEncoder", midiController: 16));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            sink);

        var result = runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 10));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var message = Assert.Single(sink.OSCMessages);
        Assert.Equal("x32", message.DeviceKey);
        Assert.Equal("/ch/01/mix/fader", message.Address);
        Assert.Equal(0.75 + 10.0 / 1023.0, Assert.Single(message.Arguments).NumberValue, precision: 12);
    }

    [Fact]
    public void DispatchControlEvent_ExecutesBuiltInXTouchMiniX32MuteScriptThroughTriggerRuntime()
    {
        var midiDeviceId = Guid.NewGuid();
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.XTouchMiniX32MutesTemplateId);
        Assert.NotNull(template);

        var script = Script(
            template.SuggestedPath,
            MIDINoteTrigger(midiDeviceId, "onXTouchMuteButton", midiNote: 89));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            sink);

        var first = runtime.DispatchControlEvent(MIDINoteEvent(midiDeviceId, note: 89, velocity: 127, isNoteOn: true));
        var second = runtime.DispatchControlEvent(MIDINoteEvent(midiDeviceId, note: 89, velocity: 127, isNoteOn: true));
        var noteOff = runtime.DispatchControlEvent(MIDINoteEvent(midiDeviceId, note: 89, velocity: 0, isNoteOn: false));

        Assert.True(Assert.Single(first.Invocations).Succeeded);
        Assert.True(Assert.Single(second.Invocations).Succeeded);
        Assert.True(Assert.Single(noteOff.Invocations).Succeeded);
        Assert.Collection(
            sink.OSCMessages,
            message =>
            {
                Assert.Equal("x32", message.DeviceKey);
                Assert.Equal("/ch/01/mix/on", message.Address);
                Assert.Equal(0, Assert.Single(message.Arguments).NumberValue);
            },
            message =>
            {
                Assert.Equal("x32", message.DeviceKey);
                Assert.Equal("/ch/01/mix/on", message.Address);
                Assert.Equal(1, Assert.Single(message.Arguments).NumberValue);
            });
    }

    [Fact]
    public void DispatchControlEvent_GettingStartedEncoderArrayScriptRequestsThenMovesFader()
    {
        // Mirrors Doc/HaPlay-Control-Getting-Started.md - one handler, a CC-indexed
        // array of channels, and request-on-miss instead of assuming a value. Keep
        // this in sync with the doc's script.
        const string source =
            """
            const channels = [1, 2, 3, 4, 5, 6, 7, 8];
            const faderStep = 1.0 / 1023.0;

            fun encoderDelta(value) {
                if (value >= 1 && value <= 10)
                    return value;
                if (value >= 65 && value <= 72)
                    return -(value - 64);
                return 0;
            }

            export fun onEncoder(event, context) {
                var index = event.midi.controller - 16;
                if (index < 0 || index >= channels.length())
                    return;

                var delta = encoderDelta(event.midi.value);
                if (delta == 0)
                    return;

                var channel = channels[index];
                var address = x32.channelFaderAddress(channel);

                if (!osc.has("x32", address)) {
                    osc.request("x32", address);
                    return;
                }

                var current = osc.cacheFloat("x32", address, 0.0);
                var next = math.clamp(current + delta * faderStep, 0.0, 1.0);

                osc.send("x32", address, osc.float32(next));
                osc.cacheSet("x32", address, next);
            }
            """;

        var midiDeviceId = Guid.NewGuid();
        const string path = "Scripts/xtouch-faders.mnd";
        var script = Script(
            path,
            new ControlScriptTriggerConfig
            {
                Kind = ControlScriptTriggerKind.MIDIControlChange,
                FunctionName = "onEncoder",
                DeviceInstanceId = midiDeviceId,
            });
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string> { [path] = source },
            sink);

        // First turn, empty cache (CC18 -> index 2 -> channel 3): the script requests
        // the current value (an address-only message) and skips the move.
        var first = runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 18, value: 10));
        Assert.True(Assert.Single(first.Invocations).Succeeded);
        var request = Assert.Single(sink.OSCMessages);
        Assert.Equal("/ch/03/mix/fader", request.Address);
        Assert.Empty(request.Arguments);

        // Simulate the X32's reply landing in the cache, then turn again -> the fader moves.
        runtime.RuntimeServices.OSCCache.SetNumber("x32", "/ch/03/mix/fader", 0.5, ControlValueCacheSource.Incoming);
        var second = runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 18, value: 10));

        Assert.True(Assert.Single(second.Invocations).Succeeded);
        Assert.Equal(2, sink.OSCMessages.Count);
        var move = sink.OSCMessages[1];
        Assert.Equal("x32", move.DeviceKey);
        Assert.Equal("/ch/03/mix/fader", move.Address);
        Assert.Equal(0.5 + 10.0 / 1023.0, Assert.Single(move.Arguments).NumberValue, precision: 12);
    }

    [Fact]
    public void DispatchControlEvent_UpdatesOSCCacheBeforeInvokingOSCTrigger()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OSCMessage,
            FunctionName = "onOSC",
            DeviceInstanceId = oscDeviceId,
            OSCAddressPattern = "/ch/*/mix/fader",
        };
        var script = Script("Scripts/main.mnd", trigger);
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(oscDeviceId, "x32")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onOSC(event, context) {
                        var current = osc.cacheFloat("x32", event.osc.address, 0.0);
                        osc.send("x32", "/seen", osc.float32(current));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(new OSCControlEvent(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            oscDeviceId,
            Guid.NewGuid(),
            "/ch/01/mix/fader",
            [OSCArgument.Float32(0.6f)]));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var message = Assert.Single(sink.OSCMessages);
        Assert.Equal("/seen", message.Address);
        Assert.Equal(0.6, Assert.Single(message.Arguments).NumberValue, precision: 6);
    }

    [Fact]
    public void DispatchControlEvent_EndpointScopeMatchesOSCListenerSource()
    {
        var firstListenerId = Guid.NewGuid();
        var secondListenerId = Guid.NewGuid();
        var oscDeviceId = Guid.NewGuid();
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Endpoint script",
            ScriptPath = "Scripts/main.mnd",
            Scope = ControlScriptScope.Endpoint,
            EndpointInstanceId = firstListenerId,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.OSCMessage,
                    FunctionName = "onOSC",
                    EndpointInstanceId = firstListenerId,
                    OSCAddressPattern = "/ch/*/mix/fader",
                },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(oscDeviceId, "x32")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onOSC(event, context) {
                        osc.send("x32", "/endpoint/" + context.endpointInstanceId, osc.float32(event.value));
                    }
                    """,
            },
            sink);

        var first = runtime.DispatchControlEvent(new OSCControlEvent(
            DateTimeOffset.UtcNow,
            firstListenerId,
            oscDeviceId,
            Guid.NewGuid(),
            "/ch/01/mix/fader",
            [OSCArgument.Float32(0.6f)]));
        var second = runtime.DispatchControlEvent(new OSCControlEvent(
            DateTimeOffset.UtcNow,
            secondListenerId,
            oscDeviceId,
            Guid.NewGuid(),
            "/ch/01/mix/fader",
            [OSCArgument.Float32(0.7f)]));

        Assert.True(Assert.Single(first.Invocations).Succeeded);
        Assert.Empty(second.Invocations);
        var message = Assert.Single(sink.OSCMessages);
        Assert.Equal($"/endpoint/{firstListenerId}", message.Address);
        Assert.Equal(0.6, Assert.Single(message.Arguments).NumberValue, precision: 6);
    }

    [Fact]
    public void DispatchControlEvent_FiresOSCCacheChangedTriggerOnIncomingValueChange()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OSCCacheChanged,
            FunctionName = "onCache",
            DeviceInstanceId = oscDeviceId,
            OSCAddressPattern = "/ch/*/mix/fader",
        };
        var script = Script("Scripts/main.mnd", trigger);
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(oscDeviceId, "x32")],
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

        var result = runtime.DispatchControlEvent(OSCEvent(oscDeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.6f)));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var change = Assert.Single(result.CacheChanges);
        Assert.Equal("/ch/01/mix/fader", change.Key.Address);
        var message = Assert.Single(sink.OSCMessages);
        Assert.Equal("/feedback/ch/01/mix/fader", message.Address);
        Assert.Equal(0.6, Assert.Single(message.Arguments).NumberValue, precision: 6);
    }

    [Fact]
    public void DispatchControlEvent_DecodesX32MeterBlobIntoIndexedCacheAddresses()
    {
        var oscDeviceId = Guid.NewGuid();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(oscDeviceId, "x32")],
            },
            new Dictionary<string, string>());

        Span<byte> blob = stackalloc byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob[..4], 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(4, 4), 253);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(8, 4), BitConverter.SingleToInt32Bits(0.25f));

        var result = runtime.DispatchControlEvent(OSCEvent(
            oscDeviceId,
            "/meters",
            OSCArgument.String("/meters/6"),
            OSCArgument.Blob(blob.ToArray())));

        Assert.Contains(
            result.CacheChanges,
            change => change.Key.Address == "/meters/6/0" && change.Value.NumberValue == 0.25);
        Assert.True(runtime.RuntimeServices.OSCCache.TryGetNumber("x32", "/meters/6/0", out var value));
        Assert.Equal(0.25, value, precision: 6);
    }

    [Fact]
    public void DispatchControlEvent_EndpointScopedCacheChangedPreservesOriginalSource()
    {
        var listenerId = Guid.NewGuid();
        var oscDeviceId = Guid.NewGuid();
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Endpoint cache",
            ScriptPath = "Scripts/main.mnd",
            Scope = ControlScriptScope.Endpoint,
            EndpointInstanceId = listenerId,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.OSCCacheChanged,
                    FunctionName = "onCache",
                    EndpointInstanceId = listenerId,
                    OSCAddressPattern = "/ch/*/mix/fader",
                },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(oscDeviceId, "x32")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onCache(event, context) {
                        osc.send("x32", "/cache/" + context.endpointInstanceId, osc.float32(event.value));
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchControlEvent(new OSCControlEvent(
            DateTimeOffset.UtcNow,
            listenerId,
            oscDeviceId,
            Guid.NewGuid(),
            "/ch/01/mix/fader",
            [OSCArgument.Float32(0.6f)]));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var message = Assert.Single(sink.OSCMessages);
        Assert.Equal($"/cache/{listenerId}", message.Address);
        Assert.Equal(0.6, Assert.Single(message.Arguments).NumberValue, precision: 6);
    }

    [Fact]
    public void DispatchControlEvent_DoesNotFireOSCCacheChangedWhenValueIsUnchanged()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OSCCacheChanged,
            FunctionName = "onCache",
            DeviceInstanceId = oscDeviceId,
            OSCAddressPattern = "/ch/*/mix/fader",
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(oscDeviceId, "x32")],
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

        runtime.DispatchControlEvent(OSCEvent(oscDeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.6f)));
        var second = runtime.DispatchControlEvent(OSCEvent(oscDeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.6f)));

        Assert.Empty(second.CacheChanges);
        Assert.Empty(second.Invocations);
        Assert.Single(sink.OSCMessages);
    }

    [Fact]
    public void DispatchControlEvent_OSCCacheChangedRespectsAddressPatternButStillUpdatesCache()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OSCCacheChanged,
            FunctionName = "onCache",
            DeviceInstanceId = oscDeviceId,
            OSCAddressPattern = "/ch/*/mix/fader",
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(oscDeviceId, "x32")],
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

        var result = runtime.DispatchControlEvent(OSCEvent(oscDeviceId, "/ch/01/mix/pan", OSCArgument.Float32(0.6f)));

        Assert.Single(result.CacheChanges);
        Assert.Empty(result.Invocations);
        Assert.Empty(sink.OSCMessages);
    }

    [Fact]
    public void DispatchControlEvent_DoesNotFireOSCCacheChangedTriggerWhenDisarmedButStillUpdatesCache()
    {
        var oscDeviceId = Guid.NewGuid();
        var trigger = new ControlScriptTriggerConfig
        {
            Kind = ControlScriptTriggerKind.OSCCacheChanged,
            FunctionName = "onCache",
            DeviceInstanceId = oscDeviceId,
            OSCAddressPattern = "/ch/*/mix/fader",
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = false,
                Devices = [OSCDevice(oscDeviceId, "x32")],
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

        var result = runtime.DispatchControlEvent(OSCEvent(oscDeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.6f)));

        Assert.Single(result.CacheChanges);
        Assert.Empty(result.Invocations);
        Assert.Empty(sink.OSCMessages);
        Assert.True(runtime.RuntimeServices.OSCCache.TryGetNumber("x32", "/ch/01/mix/fader", out var cached));
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
                Devices = [OSCDevice(deviceId, "x32")],
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
        Assert.Equal("/health/Faulted", Assert.Single(sink.OSCMessages).Address);
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
                Devices = [OSCDevice(deviceA, "a"), OSCDevice(deviceB, "b")],
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
        Assert.Empty(sink.OSCMessages);
    }

    [Fact]
    public void State_ScriptScopedValuePersistsAcrossInvocations()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = Script("Scripts/main.mnd", MIDICcTrigger(midiDeviceId, "onMIDI", midiController: 16));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onMIDI(event, context) {
                        var count = state.get("count", 0) + 1;
                        state.set("count", count);
                        osc.send("x32", "/count", osc.int32(count));
                    }
                    """,
            },
            sink);

        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 1));
        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 1));

        Assert.Collection(
            sink.OSCMessages,
            message => Assert.Equal(1, Assert.Single(message.Arguments).NumberValue),
            message => Assert.Equal(2, Assert.Single(message.Arguments).NumberValue));
    }

    [Fact]
    public void State_ProjectScopeIsSharedAcrossScripts()
    {
        var midiDeviceId = Guid.NewGuid();
        var writer = Script("Scripts/writer.mnd", MIDICcTrigger(midiDeviceId, "onWrite", midiController: 16));
        var reader = Script("Scripts/reader.mnd", MIDICcTrigger(midiDeviceId, "onRead", midiController: 17));
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
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

        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 1));
        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 17, value: 1));

        Assert.Equal(7, Assert.Single(Assert.Single(sink.OSCMessages).Arguments).NumberValue);
    }

    [Fact]
    public void State_ScriptScopeIsIsolatedBetweenScripts()
    {
        var midiDeviceId = Guid.NewGuid();
        var first = Script("Scripts/first.mnd", MIDICcTrigger(midiDeviceId, "onMIDI", midiController: 16));
        var second = Script("Scripts/second.mnd", MIDICcTrigger(midiDeviceId, "onMIDI", midiController: 17));
        var sink = new RecordingControlScriptCommandSink();
        const string body =
            """
            export fun onMIDI(event, context) {
                var count = state.get("count", 0) + 1;
                state.set("count", count);
                osc.send("x32", "/count", osc.int32(count));
            }
            """;
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Scripts = [first, second],
            },
            new Dictionary<string, string>
            {
                ["Scripts/first.mnd"] = body,
                ["Scripts/second.mnd"] = body,
            },
            sink);

        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 1));
        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 17, value: 1));
        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 1));

        Assert.Collection(
            sink.OSCMessages,
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
                Kind = ControlScriptTriggerKind.OSCMessage,
                FunctionName = "onOSC",
                OSCAddressPattern = "*",
            });
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(deviceA, "a"), OSCDevice(deviceB, "b")],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun onOSC(event, context) {
                        var n = state.device.get("n", 0) + 1;
                        state.device.set("n", n);
                        osc.send("out", "/n", osc.int32(n));
                    }
                    """,
            },
            sink);

        runtime.DispatchControlEvent(OSCEvent(deviceA, "/x", OSCArgument.Float32(1f)));
        runtime.DispatchControlEvent(OSCEvent(deviceA, "/x", OSCArgument.Float32(1f)));
        runtime.DispatchControlEvent(OSCEvent(deviceB, "/x", OSCArgument.Float32(1f)));

        Assert.Collection(
            sink.OSCMessages,
            message => Assert.Equal(1, Assert.Single(message.Arguments).NumberValue),
            message => Assert.Equal(2, Assert.Single(message.Arguments).NumberValue),
            message => Assert.Equal(1, Assert.Single(message.Arguments).NumberValue));
    }

    [Fact]
    public void X32_AddressBuildersMatchProfileConventions()
    {
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "X32 addresses",
            ScriptPath = "Scripts/main.mnd",
            Triggers =
            [
                new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.Manual, FunctionName = "run" },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig { IsArmed = true, Scripts = [script] },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("x32", x32.channelMuteAddress(1), osc.int32(1));
                        osc.send("x32", x32.channelPanAddress(5), osc.float32(0.5));
                        osc.send("x32", x32.channelSoloAddress(6), osc.int32(1));
                        osc.send("x32", x32.dcaFaderAddress(2), osc.float32(0.5));
                        osc.send("x32", x32.busMuteAddress(3), osc.int32(0));
                        osc.send("x32", x32.matrixFaderAddress(4), osc.float32(0.5));
                        osc.send("x32", x32.mainMuteAddress(), osc.int32(1));
                    }
                    """,
            },
            sink);

        runtime.DispatchManual(script.Id);

        Assert.Collection(
            sink.OSCMessages,
            m => Assert.Equal("/ch/01/mix/on", m.Address),
            m => Assert.Equal("/ch/05/mix/pan", m.Address),
            m => Assert.Equal("/-stat/solosw/06", m.Address),
            m => Assert.Equal("/dca/2/fader", m.Address),
            m => Assert.Equal("/bus/03/mix/on", m.Address),
            m => Assert.Equal("/mtx/04/mix/fader", m.Address),
            m => Assert.Equal("/main/st/mix/on", m.Address));
    }

    [Fact]
    public void OSC_CacheStringReadsStoredValueOrDefault()
    {
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Cache string",
            ScriptPath = "Scripts/main.mnd",
            Triggers =
            [
                new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.Manual, FunctionName = "run" },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var cache = new ControlValueCache();
        cache.SetString("x32", "/ch/01/config/name", "Vocals", ControlValueCacheSource.Incoming);
        var runtime = new ControlScriptRuntime(
            new ControlSystemConfig { IsArmed = true, Scripts = [script] },
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("x32", "/name", osc.cacheString("x32", "/ch/01/config/name", "?"));
                        osc.send("x32", "/missing", osc.cacheString("x32", "/nope", "fallback"));
                    }
                    """,
            }),
            new ControlScriptRuntimeServices(sink, cache));

        runtime.DispatchManual(script.Id);

        Assert.Collection(
            sink.OSCMessages,
            m =>
            {
                Assert.Equal("/name", m.Address);
                Assert.Equal("Vocals", Assert.Single(m.Arguments).StringValue);
            },
            m =>
            {
                Assert.Equal("/missing", m.Address);
                Assert.Equal("fallback", Assert.Single(m.Arguments).StringValue);
            });
    }

    [Fact]
    public void Time_NowAndNowIsoReadTheHostClock()
    {
        var fixedTime = DateTimeOffset.Parse("2026-06-04T10:00:00Z");
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Clock",
            ScriptPath = "Scripts/main.mnd",
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.Manual,
                    FunctionName = "run",
                },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = new ControlScriptRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Scripts = [script],
            },
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("x32", "/now", osc.double64(time.now()));
                        osc.send("x32", "/iso", time.nowIso());
                    }
                    """,
            }),
            new ControlScriptRuntimeServices(sink, new ControlValueCache(), clock: () => fixedTime));

        runtime.DispatchManual(script.Id);

        Assert.Collection(
            sink.OSCMessages,
            message =>
            {
                Assert.Equal("/now", message.Address);
                Assert.Equal((double)fixedTime.ToUnixTimeMilliseconds(), Assert.Single(message.Arguments).NumberValue);
            },
            message =>
            {
                Assert.Equal("/iso", message.Address);
                Assert.Equal(fixedTime.ToString("O"), Assert.Single(message.Arguments).StringValue);
            });
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
        Assert.Equal("/layer/off", Assert.Single(sink.OSCMessages).Address);
    }

    [Fact]
    public void DispatchLayerEnabled_ExecutesBuiltInX32InitialRequestTemplate()
    {
        var layerId = Guid.NewGuid();
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.X32LayerInitialRequestsTemplateId);
        Assert.NotNull(template);
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Layer startup",
            ScriptPath = template.SuggestedPath,
            Scope = ControlScriptScope.Layer,
            LayerId = layerId,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.LayerEnabled,
                    FunctionName = "onX32LayerEnabled",
                    LayerId = layerId,
                },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Layers = [new ControlLayerConfig { Id = layerId, Name = "A", IsEnabled = true }],
                Scripts = [script],
            },
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            sink);

        var result = runtime.DispatchLayerEnabled(layerId);

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.Equal(16, sink.OSCMessages.Count);
        Assert.Collection(
            sink.OSCMessages.Take(4),
            message => Assert.Equal("/ch/01/mix/fader", message.Address),
            message => Assert.Equal("/ch/01/mix/on", message.Address),
            message => Assert.Equal("/ch/02/mix/fader", message.Address),
            message => Assert.Equal("/ch/02/mix/on", message.Address));
        Assert.All(sink.OSCMessages, message =>
        {
            Assert.Equal("x32", message.DeviceKey);
            Assert.Empty(message.Arguments);
        });
    }

    [Fact]
    public void SetActiveLayer_IsMutuallyExclusiveAndFiresLayerTriggers()
    {
        var layerA = Guid.NewGuid();
        var layerB = Guid.NewGuid();
        var scriptA = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "A off",
            ScriptPath = "Scripts/a.mnd",
            Scope = ControlScriptScope.Layer,
            LayerId = layerA,
            Triggers = [new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.LayerDisabled, FunctionName = "onOff", LayerId = layerA }],
        };
        var scriptB = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "B on",
            ScriptPath = "Scripts/b.mnd",
            Scope = ControlScriptScope.Layer,
            LayerId = layerB,
            Triggers = [new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.LayerEnabled, FunctionName = "onOn", LayerId = layerB }],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Layers =
                [
                    new ControlLayerConfig { Id = layerA, Name = "A", IsEnabled = true },
                    new ControlLayerConfig { Id = layerB, Name = "B", IsEnabled = false },
                ],
                Scripts = [scriptA, scriptB],
            },
            new Dictionary<string, string>
            {
                ["Scripts/a.mnd"] = """ export fun onOff(event, context) { osc.send("x32", "/a/off", osc.float32(1)); } """,
                ["Scripts/b.mnd"] = """ export fun onOn(event, context) { osc.send("x32", "/b/on", osc.float32(1)); } """,
            },
            sink);

        Assert.Equal(layerA, runtime.ActiveLayerId); // seeded from the enabled config layer

        var result = runtime.SetActiveLayer(layerB);

        Assert.Equal(layerB, runtime.ActiveLayerId);
        Assert.Equal(2, result.Invocations.Count(i => i.Succeeded)); // A's LayerDisabled + B's LayerEnabled
        Assert.Collection(
            sink.OSCMessages.OrderBy(m => m.Address),
            m => Assert.Equal("/a/off", m.Address),
            m => Assert.Equal("/b/on", m.Address));

        // Re-activating the same layer is a no-op.
        Assert.Empty(runtime.SetActiveLayer(layerB).Invocations);
    }

    [Fact]
    public void SetActiveLayer_GatesLayerScopedEventScriptsToTheActiveLayer()
    {
        var layerA = Guid.NewGuid();
        var layerB = Guid.NewGuid();
        var midiDeviceId = Guid.NewGuid();
        ControlScriptConfig LayerCcScript(Guid layer, string path, string fn) => new()
        {
            Id = Guid.NewGuid(),
            Name = path,
            ScriptPath = path,
            Scope = ControlScriptScope.Layer,
            LayerId = layer,
            Triggers = [new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.MIDIControlChange, FunctionName = fn, DeviceInstanceId = midiDeviceId, LayerId = layer }],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Layers =
                [
                    new ControlLayerConfig { Id = layerA, Name = "A", IsEnabled = true },
                    new ControlLayerConfig { Id = layerB, Name = "B", IsEnabled = false },
                ],
                Scripts =
                [
                    LayerCcScript(layerA, "Scripts/a.mnd", "onCcA"),
                    LayerCcScript(layerB, "Scripts/b.mnd", "onCcB"),
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/a.mnd"] = """ export fun onCcA(event, context) { osc.send("x32", "/from/a", osc.float32(1)); } """,
                ["Scripts/b.mnd"] = """ export fun onCcB(event, context) { osc.send("x32", "/from/b", osc.float32(1)); } """,
            },
            sink);

        // With A active, the CC reaches only A's script.
        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 5));
        Assert.Equal("/from/a", Assert.Single(sink.OSCMessages).Address);

        // Switch to B; now the same CC reaches only B's script.
        sink.OSCMessages.Clear();
        runtime.SetActiveLayer(layerB);
        runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 5));
        Assert.Equal("/from/b", Assert.Single(sink.OSCMessages).Address);
    }

    [Fact]
    public void LayerScopedScript_WithNoLayerAssigned_StaysInert()
    {
        var midiDeviceId = Guid.NewGuid();
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Orphan",
            ScriptPath = "Scripts/orphan.mnd",
            Scope = ControlScriptScope.Layer,
            LayerId = null, // unconfigured layer script must not fire globally
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.MIDIControlChange,
                    FunctionName = "onCc",
                    DeviceInstanceId = midiDeviceId,
                },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MIDIDevice(midiDeviceId, "X-Touch Mini")],
                Layers = [new ControlLayerConfig { Id = Guid.NewGuid(), Name = "A", IsEnabled = true }],
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/orphan.mnd"] = """ export fun onCc(event, context) { osc.send("x32", "/orphan", osc.float32(1)); } """,
            },
            sink);

        var result = runtime.DispatchControlEvent(MIDICcEvent(midiDeviceId, controller: 16, value: 5));

        Assert.Empty(result.Invocations);
        Assert.Empty(sink.OSCMessages);
    }

    [Fact]
    public void DispatchControlEvent_ExecutesBuiltInXTouchMiniX32MuteFeedbackTemplate()
    {
        var oscDeviceId = Guid.NewGuid();
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.XTouchMiniX32MuteFeedbackTemplateId);
        Assert.NotNull(template);
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Mute feedback",
            ScriptPath = template.SuggestedPath,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.OSCCacheChanged,
                    FunctionName = "onX32MuteCacheChanged",
                    DeviceInstanceId = oscDeviceId,
                    OSCAddressPattern = "/ch/*/mix/on",
                },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(oscDeviceId, "x32")],
                Scripts = [script],
            },
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            sink);

        runtime.DispatchControlEvent(OSCEvent(oscDeviceId, "/ch/01/mix/on", OSCArgument.Int32(0)));
        runtime.DispatchControlEvent(OSCEvent(oscDeviceId, "/ch/01/mix/on", OSCArgument.Int32(1)));
        runtime.DispatchControlEvent(OSCEvent(oscDeviceId, "/ch/09/mix/on", OSCArgument.Int32(0)));

        Assert.Collection(
            sink.MIDIMessages,
            message =>
            {
                Assert.Equal("xtouch", message.DeviceKey);
                Assert.Equal(ControlScriptMIDIMessageKind.NoteOn, message.Kind);
                Assert.Equal(1, message.Channel);
                Assert.Equal(89, message.Note);
                Assert.Equal(127, message.Velocity);
            },
            message =>
            {
                Assert.Equal("xtouch", message.DeviceKey);
                Assert.Equal(ControlScriptMIDIMessageKind.NoteOn, message.Kind);
                Assert.Equal(1, message.Channel);
                Assert.Equal(89, message.Note);
                Assert.Equal(0, message.Velocity);
            });
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

    [Fact]
    public void DispatchManual_BoundsKeepRunningDiagnosticHistory()
    {
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Always failing script",
            ScriptPath = "Scripts/main.mnd",
            FailurePolicy = new ControlScriptFailurePolicy
            {
                Mode = ControlScriptFailureMode.KeepRunning,
                MaxConsecutiveFailures = 1,
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
            new ControlSystemConfig { IsArmed = true, Scripts = [script] },
            new Dictionary<string, string>
            {
                ["Scripts/main.mnd"] =
                    """
                    export fun fail(event, context) {
                        error("boom");
                    }
                    """,
            });

        ControlScriptDispatchResult last = null!;
        for (var i = 0; i < 4_200; i++)
            last = runtime.DispatchManual(script.Id);

        Assert.InRange(runtime.Diagnostics.Count, 1, 4_096);
        Assert.True(runtime.DroppedDiagnostics > 0);
        Assert.Single(last.Diagnostics); // trimming the retained history must not hide this dispatch's error
    }

    [Fact]
    public void DispatchManual_QueuesExtendedMIDIHelpers()
    {
        var script = new ControlScriptConfig
        {
            Id = Guid.NewGuid(),
            Name = "Extended MIDI",
            ScriptPath = "Scripts/midi.mnd",
            Triggers =
            [
                new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.Manual, FunctionName = "run" },
            ],
        };
        var sink = new RecordingControlScriptCommandSink();
        var runtime = CreateRuntime(
            new ControlSystemConfig
            {
                IsArmed = true,
                Scripts = [script],
            },
            new Dictionary<string, string>
            {
                ["Scripts/midi.mnd"] =
                    """
                    export fun run(event, context) {
                        midi.sendPolyphonicAftertouch("synth", 1, 60, 70);
                        midi.sendPolyAftertouch("synth", 1, 61, 71);
                        midi.sendChannelAftertouch("synth", 1, 72);
                        midi.sendSysEx("synth", [125, 1]);
                        midi.sendMIDITimeCodeQuarterFrame("synth", 1, 2);
                        midi.sendMIDITimeCode("synth", 19);
                        midi.sendSongPosition("synth", 96);
                        midi.sendSongSelect("synth", 3);
                        midi.sendTuneRequest("synth");
                        midi.sendClock("synth");
                        midi.sendTimingClock("synth");
                        midi.sendStart("synth");
                        midi.sendContinue("synth");
                        midi.sendStop("synth");
                        midi.sendActiveSensing("synth");
                        midi.sendReset("synth");
                        midi.sendNrpn("synth", 1, 100, 200);
                        midi.sendRpn("synth", 1, 101, 201);
                    }
                    """,
            },
            sink);

        var result = runtime.DispatchManual(script.Id);

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.Equal(18, sink.MIDIMessages.Count);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.PolyphonicAftertouch && message.Note == 60 && message.Value == 70);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.PolyphonicAftertouch && message.Note == 61 && message.Value == 71);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.ChannelAftertouch && message.Value == 72);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.SysEx && message.Data?.SequenceEqual(new byte[] { 0xF0, 0x7D, 0x01, 0xF7 }) == true);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.MIDITimeCode && message.Value == 0x12);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.MIDITimeCode && message.Value == 19);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.SongPosition && message.Value == 96);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.SongSelect && message.Value == 3);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.TuneRequest);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.TimingClock);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.Start);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.Continue);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.Stop);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.ActiveSensing);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.Reset);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.NRPN && message.Parameter == 100 && message.Value == 200);
        Assert.Contains(sink.MIDIMessages, message => message.Kind == ControlScriptMIDIMessageKind.RPN && message.Parameter == 101 && message.Value == 201);
    }

    private static ControlScriptRuntime CreateRuntime(
        ControlSystemConfig config,
        IReadOnlyDictionary<string, string> scripts,
        RecordingControlScriptCommandSink? sink = null) =>
        new(
            config,
            new InMemoryControlScriptSourceProvider(scripts),
            new ControlScriptRuntimeServices(sink ?? new RecordingControlScriptCommandSink(), new ControlValueCache()));

    private static ControlDeviceInstanceConfig MIDIDevice(Guid id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Protocol = ControlDeviceProtocol.MIDI,
            IsEnabled = true,
        };

    private static ControlDeviceInstanceConfig OSCDevice(Guid id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = "behringer.x32.osc",
            Protocol = ControlDeviceProtocol.OSC,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = name,
                OSCHost = "192.168.2.76",
                OSCPort = 10023,
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

    private static ControlScriptTriggerConfig MIDICcTrigger(Guid deviceId, string functionName, int midiController) =>
        new()
        {
            Kind = ControlScriptTriggerKind.MIDIControlChange,
            FunctionName = functionName,
            DeviceInstanceId = deviceId,
            MIDIChannel = 1,
            MIDIController = midiController,
        };

    private static ControlScriptTriggerConfig MIDINoteTrigger(Guid deviceId, string functionName, int midiNote) =>
        new()
        {
            Kind = ControlScriptTriggerKind.MIDINote,
            FunctionName = functionName,
            DeviceInstanceId = deviceId,
            MIDIChannel = 1,
            MIDINote = midiNote,
        };

    private static MIDIControlEvent MIDICcEvent(Guid deviceId, int controller, int value) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            Channel: 1,
            Controller: controller,
            Value: value,
            HighResolution14Bit: false);

    private static MIDINoteControlEvent MIDINoteEvent(Guid deviceId, int note, int velocity, bool isNoteOn) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            Channel: 1,
            Note: note,
            Velocity: velocity,
            IsNoteOn: isNoteOn);

    private static MIDIMessageControlEvent MIDIMessageEvent(Guid deviceId, ControlMIDIMessagePayload message) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            message);

    private static OSCControlEvent OSCEvent(Guid deviceId, string address, params OSCArgument[] arguments) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            address,
            arguments);

    private sealed class RecordingControlScriptCommandSink : IControlScriptCommandSink
    {
        public List<ControlScriptOSCMessage> OSCMessages { get; } = new();

        public List<ControlScriptMIDIMessage> MIDIMessages { get; } = new();

        public List<string> LayerActivations { get; } = new();

        public void SendOSC(ControlScriptOSCMessage message)
        {
            OSCMessages.Add(message);
        }

        public void SendMIDI(ControlScriptMIDIMessage message)
        {
            MIDIMessages.Add(message);
        }

        public void RequestActivateLayer(string layerKey)
        {
            LayerActivations.Add(layerKey);
        }
    }
}
