using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptRuntimeSessionTests
{
    [Fact]
    public async Task DispatchControlEventAsync_RunsStarterScriptAndRoutesOscToConfiguredX32()
    {
        var midiDeviceId = Guid.NewGuid();
        var x32DeviceId = Guid.NewGuid();
        var template = BuiltInControlScriptTemplateRepository.Instance.FindById(
            BuiltInControlScriptTemplateRepository.XTouchMiniX32FadersTemplateId);
        Assert.NotNull(template);

        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    MidiDevice(midiDeviceId, "X-Touch Mini"),
                    OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023),
                ],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "X-Touch faders",
                        ScriptPath = template.SuggestedPath,
                        DeviceInstanceId = midiDeviceId,
                        Scope = ControlScriptScope.Device,
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.MidiControlChange,
                                FunctionName = "onXTouchFaderEncoder",
                                DeviceInstanceId = midiDeviceId,
                                MidiChannel = 1,
                                MidiController = 16,
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string> { [template.SuggestedPath] = template.Source },
            sender);

        var result = await session.DispatchControlEventAsync(MidiCcEvent(midiDeviceId, controller: 16, value: 10));

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.True(Assert.Single(result.OscRoutes).Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("192.168.2.76", sent.Host);
        Assert.Equal(10023, sent.Port);
        Assert.Equal("/ch/01/mix/fader", sent.Address);
        Assert.Equal(0.75 + 10.0 / 1023.0, Assert.Single(sent.Arguments).AsFloat32(), precision: 6);
    }

    [Fact]
    public async Task TickPeriodicAsync_RunsDuePeriodicTriggerAndRoutesAtInterval()
    {
        var x32DeviceId = Guid.NewGuid();
        var triggerId = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "XRemote",
                        ScriptPath = "Scripts/xremote.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Id = triggerId,
                                Kind = ControlScriptTriggerKind.Periodic,
                                FunctionName = "tick",
                                IntervalMs = 8000,
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/xremote.mnd"] =
                    """
                    export fun tick(event, context) {
                        osc.send("x32", "/xremote");
                    }
                    """,
            },
            sender);
        var now = DateTimeOffset.Parse("2026-06-04T10:00:00Z");

        var first = await session.TickPeriodicAsync(now);
        var tooSoon = await session.TickPeriodicAsync(now.AddMilliseconds(7999));
        var second = await session.TickPeriodicAsync(now.AddMilliseconds(8000));

        Assert.True(Assert.Single(first.Invocations).Succeeded);
        Assert.Empty(tooSoon.Invocations);
        Assert.True(Assert.Single(second.Invocations).Succeeded);
        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, sent => Assert.Equal("/xremote", sent.Address));
    }

    [Fact]
    public async Task DispatchControlEventAsync_SerializesConcurrentScriptDispatches()
    {
        var midiDeviceId = Guid.NewGuid();
        var x32DeviceId = Guid.NewGuid();
        var sender = new DelayedOscSender(TimeSpan.FromMilliseconds(50));
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    MidiDevice(midiDeviceId, "X-Touch Mini"),
                    OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023),
                ],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Concurrent",
                        ScriptPath = "Scripts/concurrent.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.MidiControlChange,
                                FunctionName = "onCc",
                                DeviceInstanceId = midiDeviceId,
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/concurrent.mnd"] =
                    """
                    export fun onCc(event, context) {
                        osc.send("x32", "/cc", osc.int32(event.value));
                    }
                    """,
            },
            sender);

        var first = session.DispatchControlEventAsync(MidiCcEvent(midiDeviceId, controller: 16, value: 1)).AsTask();
        var second = session.DispatchControlEventAsync(MidiCcEvent(midiDeviceId, controller: 17, value: 2)).AsTask();

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(Assert.Single(result.Invocations).Succeeded));
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(1, sender.MaxConcurrentSends);
    }

    [Fact]
    public async Task DispatchDeviceEnabledAsync_RoutesStartupRequestScript()
    {
        var x32DeviceId = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Startup request",
                        ScriptPath = "Scripts/startup.mnd",
                        Scope = ControlScriptScope.Device,
                        DeviceInstanceId = x32DeviceId,
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.DeviceEnabled,
                                FunctionName = "onDeviceEnabled",
                                DeviceInstanceId = x32DeviceId,
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/startup.mnd"] =
                    """
                    export fun onDeviceEnabled(event, context) {
                        osc.send("x32", "/ch/01/mix/fader");
                    }
                    """,
            },
            sender);

        var result = await session.DispatchDeviceEnabledAsync(x32DeviceId);

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.True(Assert.Single(result.OscRoutes).Succeeded);
        Assert.Equal("/ch/01/mix/fader", Assert.Single(sender.Sent).Address);
    }

    [Fact]
    public async Task DispatchManualAsync_ReturnsRouteFailureForUnknownOscDevice()
    {
        var sender = new RecordingOscSender();
        var scriptId = Guid.NewGuid();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = scriptId,
                        Name = "Bad route",
                        ScriptPath = "Scripts/bad-route.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.Manual,
                                FunctionName = "run",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/bad-route.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("missing", "/test", osc.float32(1));
                    }
                    """,
            },
            sender);

        var result = await session.DispatchManualAsync(scriptId);

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var route = Assert.Single(result.OscRoutes);
        Assert.False(route.Succeeded);
        Assert.Contains("No enabled OSC device matches key 'missing'", route.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task DispatchManualAsync_SharesOptimisticCacheBetweenRuntimeAndRouter()
    {
        var x32Id = Guid.NewGuid();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                OscCacheUpdateMode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                Devices = [OscDevice(x32Id, "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Cache send",
                        ScriptPath = "Scripts/cache-send.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.Manual,
                                FunctionName = "run",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/cache-send.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("x32", "/ch/01/mix/fader", osc.float32(0.42));
                    }
                    """,
            },
            new RecordingOscSender());

        await session.DispatchManualAsync();

        Assert.True(session.OscCache.TryGetNumber("x32", "/ch/01/mix/fader", out var value));
        Assert.Equal(0.42, value, precision: 12);
    }

    [Fact]
    public async Task DispatchManualAsync_RoutesMidiOutputAndRecordsMonitorEntry()
    {
        var xtouchId = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var midiSender = new RecordingMidiSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(xtouchId, "X-Touch Mini", alias: "xtouch", outputName: "X-Touch MINI")],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "MIDI send",
                        ScriptPath = "Scripts/midi-send.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.Manual,
                                FunctionName = "run",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/midi-send.mnd"] =
                    """
                    export fun run(event, context) {
                        midi.sendNoteOn("xtouch", 1, 89, 127);
                        midi.sendCc("xtouch", 1, 16, 64);
                    }
                    """,
            },
            new RecordingOscSender(),
            monitor,
            midiSender);

        var result = await session.DispatchManualAsync();

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        Assert.Empty(result.OscRoutes);
        Assert.All(result.MidiRoutes, route => Assert.True(route.Succeeded));
        Assert.Collection(
            midiSender.Sent,
            sent =>
            {
                Assert.Equal(ControlScriptMidiMessageKind.NoteOn, sent.Kind);
                Assert.Equal(xtouchId, sent.EndpointId);
                Assert.Equal(1, sent.Channel);
                Assert.Equal(89, sent.Note);
                Assert.Equal(127, sent.Value);
            },
            sent =>
            {
                Assert.Equal(ControlScriptMidiMessageKind.ControlChange, sent.Kind);
                Assert.Equal(xtouchId, sent.EndpointId);
                Assert.Equal(16, sent.Controller);
                Assert.Equal(64, sent.Value);
            });

        var midiOutputRecords = monitor.Records
            .Where(r => r.Protocol == ControlMonitorProtocol.Midi && r.Direction == ControlMonitorDirection.Output)
            .ToArray();
        Assert.Equal(2, midiOutputRecords.Length);
        Assert.Contains(midiOutputRecords, r => r.Message == nameof(ControlScriptMidiMessageKind.NoteOn) && r.MidiNote == 89);
        Assert.Contains(midiOutputRecords, r => r.Message == nameof(ControlScriptMidiMessageKind.ControlChange) && r.MidiController == 16);
    }

    [Fact]
    public async Task DispatchControlEventAsync_RecordsCacheUpdateAndFiresCacheChangedFeedback()
    {
        var x32DeviceId = Guid.NewGuid();
        var xtouchId = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 20);
        var midiSender = new RecordingMidiSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023),
                    MidiDevice(xtouchId, "X-Touch Mini", alias: "xtouch", outputName: "X-Touch MINI"),
                ],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Fader feedback",
                        ScriptPath = "Scripts/feedback.mnd",
                        Scope = ControlScriptScope.Device,
                        DeviceInstanceId = x32DeviceId,
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.OscCacheChanged,
                                FunctionName = "onFaderChanged",
                                DeviceInstanceId = x32DeviceId,
                                OscAddressPattern = "/ch/01/mix/fader",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/feedback.mnd"] =
                    """
                    export fun onFaderChanged(event, context) {
                        midi.sendCc("xtouch", 1, 16, 64);
                    }
                    """,
            },
            new RecordingOscSender(),
            monitor,
            midiSender);

        var result = await session.DispatchControlEventAsync(
            OscEvent(x32DeviceId, "/ch/01/mix/fader", OSCArgument.Float32(0.5f)));

        var cacheUpdate = Assert.Single(result.CacheUpdates);
        Assert.Equal("/ch/01/mix/fader", cacheUpdate.Key.Address);
        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var midiSent = Assert.Single(midiSender.Sent);
        Assert.Equal(ControlScriptMidiMessageKind.ControlChange, midiSent.Kind);
        Assert.Equal(xtouchId, midiSent.EndpointId);

        var cacheRecord = Assert.Single(
            monitor.Records,
            r => r.Protocol == ControlMonitorProtocol.Cache);
        Assert.Equal(ControlMonitorResult.Cached, cacheRecord.Result);
        Assert.Equal(ControlMonitorDirection.Internal, cacheRecord.Direction);
        Assert.Equal(x32DeviceId, cacheRecord.DeviceInstanceId);
        Assert.Equal("/ch/01/mix/fader", cacheRecord.Address);
    }

    [Fact]
    public async Task ReportDeviceHealthAsync_FiresOnTransitionRecordsMonitorAndDedupesSameState()
    {
        var x32DeviceId = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 20);
        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Health log",
                        ScriptPath = "Scripts/health.mnd",
                        Scope = ControlScriptScope.Device,
                        DeviceInstanceId = x32DeviceId,
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.DeviceHealthChanged,
                                FunctionName = "onHealth",
                                DeviceInstanceId = x32DeviceId,
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/health.mnd"] =
                    """
                    export fun onHealth(event, context) {
                        osc.send("x32", "/state/" + event.state, osc.int32(1));
                    }
                    """,
            },
            sender,
            monitor);

        var first = await session.ReportDeviceHealthAsync(x32DeviceId, ControlSessionHealth.Running("listening"));
        var repeat = await session.ReportDeviceHealthAsync(x32DeviceId, ControlSessionHealth.Running("still listening"));
        var faulted = await session.ReportDeviceHealthAsync(x32DeviceId, ControlSessionHealth.Faulted("socket closed"));

        Assert.True(Assert.Single(first.Invocations).Succeeded);
        Assert.Empty(repeat.Invocations);
        Assert.True(Assert.Single(faulted.Invocations).Succeeded);

        Assert.Collection(
            sender.Sent,
            sent => Assert.Equal("/state/Running", sent.Address),
            sent => Assert.Equal("/state/Faulted", sent.Address));

        var healthRows = monitor.Records
            .Where(r => r.Protocol == ControlMonitorProtocol.Runtime && r.DeviceInstanceId == x32DeviceId)
            .ToArray();
        Assert.Equal(2, healthRows.Length);
        Assert.Contains(healthRows, r => r.Message == "Running" && r.Direction == ControlMonitorDirection.Internal);
        Assert.Contains(
            healthRows,
            r => r.Message == "Running -> Faulted"
                && r.Direction == ControlMonitorDirection.Error
                && r.ErrorMessage == "socket closed");
    }

    [Fact]
    public async Task DispatchManualAsync_ScriptLogsToMonitorWithScriptAttribution()
    {
        var scriptId = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 20);
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = scriptId,
                        Name = "Logger",
                        ScriptPath = "Scripts/log.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.Manual,
                                FunctionName = "run",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/log.mnd"] =
                    """
                    export fun run(event, context) {
                        monitor.log("hello from script");
                        monitor.error("something failed");
                    }
                    """,
            },
            new RecordingOscSender(),
            monitor);

        var result = await session.DispatchManualAsync(scriptId);

        Assert.True(Assert.Single(result.Invocations).Succeeded);

        var logRecord = Assert.Single(
            monitor.Records,
            r => r.Protocol == ControlMonitorProtocol.Script && r.Result == ControlMonitorResult.Logged);
        Assert.Equal(ControlMonitorDirection.Internal, logRecord.Direction);
        Assert.Equal(scriptId, logRecord.ScriptId);
        Assert.Equal("hello from script", logRecord.Message);

        var errorRecord = Assert.Single(
            monitor.Records,
            r => r.Protocol == ControlMonitorProtocol.Script
                && r.Direction == ControlMonitorDirection.Error
                && r.ErrorMessage == "something failed");
        Assert.Equal(scriptId, errorRecord.ScriptId);
    }

    [Fact]
    public async Task DispatchManualAsync_DevicesLibraryExposesConfiguredDevicesAndReportedHealth()
    {
        var x32Id = Guid.NewGuid();
        var spareId = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    OscDevice(x32Id, "X32", "x32", "192.168.2.76", 10023),
                    new ControlDeviceInstanceConfig
                    {
                        Id = spareId,
                        Name = "Spare",
                        Protocol = ControlDeviceProtocol.Osc,
                        IsEnabled = false,
                        Binding = new ControlDeviceBindingConfig
                        {
                            Alias = "spare",
                            OscHost = "192.168.2.99",
                            OscPort = 10023,
                        },
                    },
                ],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Inspect",
                        ScriptPath = "Scripts/inspect.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.Manual,
                                FunctionName = "run",
                            },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/inspect.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("x32", "/count", osc.int32(devices.list().length()));
                        osc.send("x32", "/enabled", osc.int32(devices.isEnabled("x32") ? 1 : 0));
                        osc.send("x32", "/spareEnabled", osc.int32(devices.isEnabled("spare") ? 1 : 0));
                        osc.send("x32", "/health/" + devices.health("x32"), osc.int32(1));
                        osc.send("x32", "/profile/" + devices.get("x32").profileId, osc.int32(1));
                    }
                    """,
            },
            sender);

        await session.ReportDeviceHealthAsync(x32Id, ControlSessionHealth.Running("listening"));
        var result = await session.DispatchManualAsync();

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var sent = sender.Sent;
        Assert.Equal(5, sent.Count);
        Assert.Equal("/count", sent[0].Address);
        Assert.Equal(2, Assert.Single(sent[0].Arguments).AsInt32());
        Assert.Equal("/enabled", sent[1].Address);
        Assert.Equal(1, Assert.Single(sent[1].Arguments).AsInt32());
        Assert.Equal("/spareEnabled", sent[2].Address);
        Assert.Equal(0, Assert.Single(sent[2].Arguments).AsInt32());
        Assert.Equal("/health/Running", sent[3].Address);
        Assert.Equal("/profile/x32", sent[4].Address);
    }

    [Fact]
    public async Task DispatchManualAsync_RoutesExtendedOscArgumentTypesAndValueRequest()
    {
        var x32Id = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(x32Id, "X32", "x32", "192.168.2.76", 10023)],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Args",
                        ScriptPath = "Scripts/args.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.Manual, FunctionName = "run" },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/args.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("x32", "/types", osc.int64(42), osc.symbol("sym"), osc.nil());
                        osc.request("x32", "/ch/01/mix/fader");
                    }
                    """,
            },
            sender);

        await session.DispatchManualAsync();

        Assert.Collection(
            sender.Sent,
            sent =>
            {
                Assert.Equal("/types", sent.Address);
                Assert.Collection(
                    sent.Arguments,
                    a =>
                    {
                        Assert.Equal(OSCArgumentType.Int64, a.Type);
                        Assert.Equal(42, a.AsInt64());
                    },
                    a =>
                    {
                        Assert.Equal(OSCArgumentType.Symbol, a.Type);
                        Assert.Equal("sym", a.AsString());
                    },
                    a => Assert.Equal(OSCArgumentType.Nil, a.Type));
            },
            sent =>
            {
                Assert.Equal("/ch/01/mix/fader", sent.Address);
                Assert.Empty(sent.Arguments);
            });
    }

    [Fact]
    public async Task DispatchManualAsync_RoutesHighResolutionCc()
    {
        var xtouchId = Guid.NewGuid();
        var midiSender = new RecordingMidiSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [MidiDevice(xtouchId, "X-Touch Mini", alias: "xtouch", outputName: "X-Touch MINI")],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "HiRes",
                        ScriptPath = "Scripts/hires.mnd",
                        Triggers =
                        [
                            new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.Manual, FunctionName = "run" },
                        ],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/hires.mnd"] =
                    """
                    export fun run(event, context) {
                        midi.sendHighResCc("xtouch", 1, 16, 8000);
                    }
                    """,
            },
            new RecordingOscSender(),
            midiSender: midiSender);

        var result = await session.DispatchManualAsync();

        Assert.True(Assert.Single(result.Invocations).Succeeded);
        var sent = Assert.Single(midiSender.Sent);
        Assert.Equal(ControlScriptMidiMessageKind.ControlChange, sent.Kind);
        Assert.True(sent.HighResolution14Bit);
        Assert.Equal(16, sent.Controller);
        Assert.Equal(8000, sent.Value);
    }

    [Fact]
    public async Task ScriptLayersActivate_SwitchesLayerAndRunsLayerEnabledHook()
    {
        var midiDeviceId = Guid.NewGuid();
        var x32DeviceId = Guid.NewGuid();
        var layerB = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var session = CreateSession(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    MidiDevice(midiDeviceId, "X-Touch Mini"),
                    OscDevice(x32DeviceId, "X32", "x32", "192.168.2.76", 10023),
                ],
                Layers = [new ControlLayerConfig { Id = layerB, Name = "B", IsEnabled = false }],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "Switcher",
                        ScriptPath = "Scripts/switch.mnd",
                        Scope = ControlScriptScope.Project,
                        Triggers = [new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.MidiControlChange, FunctionName = "onSwitch", DeviceInstanceId = midiDeviceId }],
                    },
                    new ControlScriptConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "B on",
                        ScriptPath = "Scripts/b.mnd",
                        Scope = ControlScriptScope.Layer,
                        LayerId = layerB,
                        Triggers = [new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.LayerEnabled, FunctionName = "onOn", LayerId = layerB }],
                    },
                ],
            },
            new Dictionary<string, string>
            {
                ["Scripts/switch.mnd"] = """ export fun onSwitch(event, context) { layers.activate("B"); } """,
                ["Scripts/b.mnd"] = """ export fun onOn(event, context) { osc.send("x32", "/layer/b/on", osc.float32(1)); } """,
            },
            sender);

        Assert.Null(session.ActiveLayerId);

        await session.DispatchControlEventAsync(MidiCcEvent(midiDeviceId, controller: 20, value: 5));

        Assert.Equal(layerB, session.ActiveLayerId);
        Assert.Equal("/layer/b/on", Assert.Single(sender.Sent).Address);
    }

    private static ControlScriptRuntimeSession CreateSession(
        ControlSystemConfig config,
        IReadOnlyDictionary<string, string> scripts,
        IControlOscSender sender,
        IControlMonitorSink? monitor = null,
        IControlMidiSender? midiSender = null) =>
        new(config, new InMemoryControlScriptSourceProvider(scripts), sender, monitor: monitor, midiSender: midiSender);

    private static ControlDeviceInstanceConfig MidiDevice(
        Guid id,
        string name,
        string? alias = null,
        string? outputName = null) =>
        new()
        {
            Id = id,
            Name = name,
            Protocol = ControlDeviceProtocol.Midi,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                MidiOutputDeviceName = outputName,
            },
        };

    private static ControlDeviceInstanceConfig OscDevice(
        Guid id,
        string name,
        string alias,
        string host,
        int port) =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = "x32",
            Protocol = ControlDeviceProtocol.Osc,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OscHost = host,
                OscPort = port,
            },
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

    private static OscControlEvent OscEvent(Guid deviceId, string address, params OSCArgument[] arguments) =>
        new(
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            deviceId,
            Guid.NewGuid(),
            address,
            arguments);

    private sealed class RecordingOscSender : IControlOscSender
    {
        public List<SentOscMessage> Sent { get; } = new();

        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentOscMessage(host, port, address, arguments.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentOscMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);

    private sealed class DelayedOscSender : IControlOscSender
    {
        private readonly TimeSpan _delay;
        private readonly object _sentGate = new();
        private readonly List<SentOscMessage> _sent = new();
        private int _currentSends;
        private int _maxConcurrentSends;

        public DelayedOscSender(TimeSpan delay)
        {
            _delay = delay;
        }

        public IReadOnlyList<SentOscMessage> Sent
        {
            get
            {
                lock (_sentGate)
                    return _sent.ToArray();
            }
        }

        public int MaxConcurrentSends => Volatile.Read(ref _maxConcurrentSends);

        public async ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _currentSends);
            try
            {
                UpdateMaxConcurrentSends(current);
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                lock (_sentGate)
                    _sent.Add(new SentOscMessage(host, port, address, arguments.ToArray()));
            }
            finally
            {
                Interlocked.Decrement(ref _currentSends);
            }
        }

        private void UpdateMaxConcurrentSends(int current)
        {
            while (true)
            {
                var max = Volatile.Read(ref _maxConcurrentSends);
                if (current <= max)
                    return;
                if (Interlocked.CompareExchange(ref _maxConcurrentSends, current, max) == max)
                    return;
            }
        }
    }

    private sealed class RecordingMidiSender : IControlMidiSender
    {
        public List<SentMidiMessage> Sent { get; } = new();

        public ValueTask SendControlChangeAsync(
            Guid? endpointId,
            int channel,
            int controller,
            int value,
            bool highResolution14Bit,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.ControlChange,
                endpointId,
                channel,
                Controller: controller,
                Note: null,
                Value: value,
                HighResolution14Bit: highResolution14Bit));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendNoteAsync(
            Guid? endpointId,
            int channel,
            int note,
            int velocity,
            bool isNoteOn,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                isNoteOn ? ControlScriptMidiMessageKind.NoteOn : ControlScriptMidiMessageKind.NoteOff,
                endpointId,
                channel,
                Controller: null,
                Note: note,
                Value: velocity,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendProgramChangeAsync(
            Guid? endpointId,
            int channel,
            int program,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.ProgramChange,
                endpointId,
                channel,
                Controller: null,
                Note: null,
                Value: program,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendPitchBendAsync(
            Guid? endpointId,
            int channel,
            int value,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                ControlScriptMidiMessageKind.PitchBend,
                endpointId,
                channel,
                Controller: null,
                Note: null,
                Value: value,
                HighResolution14Bit: false));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendMidiMessageAsync(
            Guid? endpointId,
            ControlMidiMessagePayload message,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentMidiMessage(
                ToScriptMessageKind(message.MessageType),
                endpointId,
                message.Channel ?? 0,
                Controller: message.Controller,
                Note: message.Note,
                Value: message.Value ?? 0,
                HighResolution14Bit: message.HighResolution14Bit));
            return ValueTask.CompletedTask;
        }

        private static ControlScriptMidiMessageKind ToScriptMessageKind(ControlMidiMessageType messageType) =>
            messageType switch
            {
                ControlMidiMessageType.ControlChange => ControlScriptMidiMessageKind.ControlChange,
                ControlMidiMessageType.NoteOn => ControlScriptMidiMessageKind.NoteOn,
                ControlMidiMessageType.NoteOff => ControlScriptMidiMessageKind.NoteOff,
                ControlMidiMessageType.PolyphonicAftertouch => ControlScriptMidiMessageKind.PolyphonicAftertouch,
                ControlMidiMessageType.ProgramChange => ControlScriptMidiMessageKind.ProgramChange,
                ControlMidiMessageType.ChannelAftertouch => ControlScriptMidiMessageKind.ChannelAftertouch,
                ControlMidiMessageType.PitchBend => ControlScriptMidiMessageKind.PitchBend,
                ControlMidiMessageType.SysEx => ControlScriptMidiMessageKind.SysEx,
                ControlMidiMessageType.MIDITimeCode => ControlScriptMidiMessageKind.MIDITimeCode,
                ControlMidiMessageType.SongPosition => ControlScriptMidiMessageKind.SongPosition,
                ControlMidiMessageType.SongSelect => ControlScriptMidiMessageKind.SongSelect,
                ControlMidiMessageType.TuneRequest => ControlScriptMidiMessageKind.TuneRequest,
                ControlMidiMessageType.TimingClock => ControlScriptMidiMessageKind.TimingClock,
                ControlMidiMessageType.Start => ControlScriptMidiMessageKind.Start,
                ControlMidiMessageType.Continue => ControlScriptMidiMessageKind.Continue,
                ControlMidiMessageType.Stop => ControlScriptMidiMessageKind.Stop,
                ControlMidiMessageType.ActiveSensing => ControlScriptMidiMessageKind.ActiveSensing,
                ControlMidiMessageType.Reset => ControlScriptMidiMessageKind.Reset,
                ControlMidiMessageType.NRPN => ControlScriptMidiMessageKind.NRPN,
                ControlMidiMessageType.RPN => ControlScriptMidiMessageKind.RPN,
                _ => throw new InvalidOperationException($"Unexpected MIDI message type '{messageType}'."),
            };
    }

    private sealed record SentMidiMessage(
        ControlScriptMidiMessageKind Kind,
        Guid? EndpointId,
        int Channel,
        int? Controller,
        int? Note,
        int Value,
        bool HighResolution14Bit);
}
