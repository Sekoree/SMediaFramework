using HaPlay.ControlGraph;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlWorkspaceViewModelTests
{
    [Fact]
    public async Task AddOrUpdateMidiDevices_MergesInputAndOutputByDeviceName()
    {
        await using var vm = new ControlWorkspaceViewModel();

        vm.AddOrUpdateMidiInputDevice(1, "X-Touch MINI");
        vm.AddOrUpdateMidiOutputDevice(2, "X-Touch MINI");

        var device = Assert.Single(vm.BuildSnapshot().Devices);
        Assert.Equal(ControlDeviceProtocol.Midi, device.Protocol);
        Assert.Equal("X-Touch MINI", device.Name);
        Assert.Equal("x-touch-mini", device.Binding.Alias);
        Assert.Equal(1, device.Binding.MidiInputDeviceId);
        Assert.Equal("X-Touch MINI", device.Binding.MidiInputDeviceName);
        Assert.Equal(2, device.Binding.MidiOutputDeviceId);
        Assert.Equal("X-Touch MINI", device.Binding.MidiOutputDeviceName);
        Assert.Equal(1, vm.DeviceCount);
    }

    [Fact]
    public async Task RemoveMidiBinding_RemovesOnlySelectedDirection()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.AddOrUpdateMidiInputDevice(1, "X-Touch MINI");
        vm.AddOrUpdateMidiOutputDevice(2, "X-Touch MINI");
        var deviceId = Assert.Single(vm.BuildSnapshot().Devices).Id;

        Assert.True(vm.RemoveMidiInputDevice(deviceId));

        var outputOnly = Assert.Single(vm.BuildSnapshot().Devices);
        Assert.Null(outputOnly.Binding.MidiInputDeviceId);
        Assert.Null(outputOnly.Binding.MidiInputDeviceName);
        Assert.Equal(2, outputOnly.Binding.MidiOutputDeviceId);
        Assert.Equal("X-Touch MINI", outputOnly.Binding.MidiOutputDeviceName);

        Assert.True(vm.RemoveMidiOutputDevice(deviceId));
        Assert.Empty(vm.BuildSnapshot().Devices);
    }

    [Fact]
    public async Task AddOrUpdateMidiDevices_CreatesSeparateDevicesForDifferentNames()
    {
        await using var vm = new ControlWorkspaceViewModel();

        vm.AddOrUpdateMidiInputDevice(1, "X-Touch MINI");
        vm.AddOrUpdateMidiOutputDevice(2, "Backup MIDI Out");

        var snapshot = vm.BuildSnapshot();

        Assert.Equal(2, snapshot.Devices.Count);
        Assert.Contains(snapshot.Devices, d => d.Binding.MidiInputDeviceName == "X-Touch MINI");
        Assert.Contains(snapshot.Devices, d => d.Binding.MidiOutputDeviceName == "Backup MIDI Out");
    }

    [Fact]
    public async Task LoadConfig_WithProjectRoot_LoadsAndSavesSelectedScriptFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "haplay-script-editor-" + Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(root, "Scripts", "control.mnd");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "export fun run() { return 1; }");

        try
        {
            await using var vm = new ControlWorkspaceViewModel();
            vm.SetProjectRoot(root);
            vm.LoadConfig(new ControlSystemConfig
            {
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Name = "Control",
                        ScriptPath = "Scripts/control.mnd",
                    },
                ],
            });

            Assert.Equal("Control", Assert.Single(vm.ScriptRows).Name);
            Assert.Contains("return 1", vm.SelectedScriptText);
            Assert.Equal("run", vm.ExportedFunctionsSummary);
            Assert.Empty(vm.ScriptDiagnostics);

            vm.SelectedScriptText = "export fun run() { return 2; }";
            vm.SaveSelectedScriptCommand.Execute(null);

            Assert.Contains("return 2", await File.ReadAllTextAsync(scriptPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptAnalysis_ReportsMissingTriggerExportsFromUnsavedEditorText()
    {
        var root = Path.Combine(Path.GetTempPath(), "haplay-script-diagnostics-" + Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(root, "Scripts", "control.mnd");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, "export fun run() { return 1; }");

        try
        {
            await using var vm = new ControlWorkspaceViewModel();
            vm.SetProjectRoot(root);
            vm.LoadConfig(new ControlSystemConfig
            {
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Name = "Control",
                        ScriptPath = "Scripts/control.mnd",
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
            });

            Assert.Equal("run", vm.ExportedFunctionsSummary);
            Assert.Empty(vm.ScriptDiagnostics);

            vm.SelectedScriptText = "export fun other() { return 2; }";

            Assert.Equal("other", vm.ExportedFunctionsSummary);
            var diagnostic = Assert.Single(vm.ScriptDiagnostics);
            Assert.Equal("Compile", diagnostic.Stage);
            Assert.Contains("missing export 'run'", diagnostic.Message);
            Assert.True(diagnostic.IsError);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptRowEdits_UpdateSnapshotAndSaveTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "haplay-script-metadata-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using var vm = new ControlWorkspaceViewModel();
            vm.SetProjectRoot(root);
            vm.LoadConfig(new ControlSystemConfig
            {
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Name = "Original",
                        ScriptPath = string.Empty,
                        Scope = ControlScriptScope.Project,
                        FailurePolicy = new ControlScriptFailurePolicy
                        {
                            Mode = ControlScriptFailureMode.DisableScript,
                            MaxConsecutiveFailures = 3,
                        },
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.MidiControlChange,
                                FunctionName = "onEncoder",
                                MidiController = 16,
                            },
                        ],
                    },
                ],
            });

            var row = Assert.Single(vm.ScriptRows);
            Assert.False(vm.SaveSelectedScriptCommand.CanExecute(null));
            Assert.Contains("onEncoder", row.TriggerSummary);

            row.Name = "Edited";
            row.ScriptPath = "Scripts/edited.mnd";
            row.IsEnabled = false;
            row.Scope = ControlScriptScope.Layer;
            row.FailureMode = ControlScriptFailureMode.KeepRunning;
            row.MaxConsecutiveFailures = 7;

            Assert.True(vm.SaveSelectedScriptCommand.CanExecute(null));

            var script = Assert.Single(vm.BuildSnapshot().Scripts);
            Assert.Equal("Edited", script.Name);
            Assert.Equal("Scripts/edited.mnd", script.ScriptPath);
            Assert.False(script.IsEnabled);
            Assert.Equal(ControlScriptScope.Layer, script.Scope);
            Assert.Equal(ControlScriptFailureMode.KeepRunning, script.FailurePolicy.Mode);
            Assert.Equal(7, script.FailurePolicy.MaxConsecutiveFailures);

            vm.SelectedScriptText = "export fun run() { return 42; }";
            vm.SaveSelectedScriptCommand.Execute(null);

            var savedPath = Path.Combine(root, "Scripts", "edited.mnd");
            Assert.Contains("return 42", await File.ReadAllTextAsync(savedPath));
            Assert.Equal("run", vm.ExportedFunctionsSummary);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddScript_AppendsAndSelectsNewScript()
    {
        await using var vm = new ControlWorkspaceViewModel();
        Assert.False(vm.HasSelectedScript);
        Assert.False(vm.RemoveSelectedScriptCommand.CanExecute(null));

        vm.AddScriptCommand.Execute(null);

        Assert.Equal(1, vm.ScriptCount);
        var row = Assert.Single(vm.ScriptRows);
        Assert.Same(row, vm.SelectedScriptRow);
        Assert.True(vm.HasSelectedScript);
        Assert.True(vm.RemoveSelectedScriptCommand.CanExecute(null));

        vm.AddScriptCommand.Execute(null);
        Assert.Equal(2, vm.ScriptCount);
        Assert.Equal(2, vm.BuildSnapshot().Scripts.Count);
    }

    [Fact]
    public async Task RemoveSelectedScript_DropsSelectedScript()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts =
            [
                new ControlScriptConfig { Name = "Keep" },
                new ControlScriptConfig { Name = "Drop" },
            ],
        });

        vm.SelectedScriptRow = vm.ScriptRows.Single(r => r.Name == "Drop");
        vm.RemoveSelectedScriptCommand.Execute(null);

        var remaining = Assert.Single(vm.BuildSnapshot().Scripts);
        Assert.Equal("Keep", remaining.Name);
        Assert.DoesNotContain(vm.ScriptRows, r => r.Name == "Drop");
    }

    [Fact]
    public async Task AddTrigger_AppendsManualTriggerToSelectedScript()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts = [new ControlScriptConfig { Name = "Script" }],
        });

        var row = Assert.Single(vm.ScriptRows);
        Assert.Empty(row.Triggers);

        row.AddTriggerCommand.Execute(null);

        var triggerRow = Assert.Single(row.Triggers);
        Assert.Equal(ControlScriptTriggerKind.Manual, triggerRow.Kind);

        var script = Assert.Single(vm.BuildSnapshot().Scripts);
        Assert.Single(script.Triggers);
        Assert.Equal(ControlScriptTriggerKind.Manual, script.Triggers[0].Kind);
    }

    [Fact]
    public async Task TriggerRowEdits_FlowIntoScriptSnapshot()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts = [new ControlScriptConfig { Name = "Script" }],
        });

        var row = Assert.Single(vm.ScriptRows);
        row.AddTriggerCommand.Execute(null);
        var triggerRow = Assert.Single(row.Triggers);

        triggerRow.Kind = ControlScriptTriggerKind.MidiControlChange;
        triggerRow.FunctionName = "  onEncoder  ";
        triggerRow.MidiChannelText = "1";
        triggerRow.MidiControllerText = "16";

        Assert.True(triggerRow.ShowMidiController);
        Assert.False(triggerRow.ShowOscAddress);

        var trigger = Assert.Single(Assert.Single(vm.BuildSnapshot().Scripts).Triggers);
        Assert.Equal(ControlScriptTriggerKind.MidiControlChange, trigger.Kind);
        Assert.Equal("onEncoder", trigger.FunctionName);
        Assert.Equal(1, trigger.MidiChannel);
        Assert.Equal(16, trigger.MidiController);
        Assert.Null(trigger.MidiNote);
        Assert.Contains("onEncoder", row.TriggerSummary);
    }

    [Fact]
    public async Task TriggerRow_EmptyMatchFieldClearsToMatchAny()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts =
            [
                new ControlScriptConfig
                {
                    Name = "Script",
                    Triggers =
                    [
                        new ControlScriptTriggerConfig
                        {
                            Kind = ControlScriptTriggerKind.MidiControlChange,
                            FunctionName = "onEncoder",
                            MidiController = 16,
                        },
                    ],
                },
            ],
        });

        var triggerRow = Assert.Single(Assert.Single(vm.ScriptRows).Triggers);
        Assert.Equal("16", triggerRow.MidiControllerText);

        triggerRow.MidiControllerText = string.Empty;

        Assert.Null(Assert.Single(Assert.Single(vm.BuildSnapshot().Scripts).Triggers).MidiController);
    }

    [Fact]
    public async Task RemoveTrigger_DropsTriggerFromScript()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts =
            [
                new ControlScriptConfig
                {
                    Name = "Script",
                    Triggers =
                    [
                        new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.Manual, FunctionName = "run" },
                    ],
                },
            ],
        });

        var row = Assert.Single(vm.ScriptRows);
        var triggerRow = Assert.Single(row.Triggers);

        triggerRow.RemoveCommand.Execute(null);

        Assert.Empty(row.Triggers);
        Assert.Empty(Assert.Single(vm.BuildSnapshot().Scripts).Triggers);
    }

    [Fact]
    public async Task ContextMenu_AddDeviceScript_AddsDeviceScopedScript()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var deviceId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = deviceId,
                    Name = "X-Touch MINI",
                    Protocol = ControlDeviceProtocol.Midi,
                    Binding = new ControlDeviceBindingConfig { MidiInputDeviceName = "X-Touch MINI" },
                },
            ],
        });

        var row = vm.StructureRows.Single(r => r.Kind == "MIDI" && r.DeviceInstanceId == deviceId);
        Assert.True(row.CanAddDeviceScript);
        Assert.True(row.CanTestMidi);
        Assert.False(row.CanTestOsc);

        row.AddDeviceScriptCommand!.Execute(null);

        var script = Assert.Single(vm.BuildSnapshot().Scripts);
        Assert.Equal(ControlScriptScope.Device, script.Scope);
        Assert.Equal(deviceId, script.DeviceInstanceId);
    }

    [Fact]
    public async Task ContextMenu_AddLayerScript_AddsLayerScopedScript()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var layerId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Layers = [new ControlLayerConfig { Id = layerId, Name = "Layer A" }],
        });

        var row = vm.StructureRows.Single(r => r.Kind == "Layer" && r.LayerId == layerId);
        Assert.True(row.CanAddLayerScript);

        row.AddLayerScriptCommand!.Execute(null);

        var script = Assert.Single(vm.BuildSnapshot().Scripts);
        Assert.Equal(ControlScriptScope.Layer, script.Scope);
        Assert.Equal(layerId, script.LayerId);
    }

    [Fact]
    public async Task ContextMenu_AddPeriodicSend_AddsSendToOscDevice()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var deviceId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = deviceId,
                    Name = "X32",
                    Protocol = ControlDeviceProtocol.Osc,
                    Binding = new ControlDeviceBindingConfig { OscHost = "192.168.2.76", OscPort = 10023 },
                },
            ],
        });

        var row = vm.StructureRows.Single(r => r.Kind == "OSC" && r.DeviceInstanceId == deviceId);
        Assert.True(row.CanAddPeriodicSend);
        Assert.True(row.CanTestOsc);
        Assert.False(row.CanTestMidi);

        row.AddPeriodicSendCommand!.Execute(null);

        var device = Assert.Single(vm.BuildSnapshot().Devices);
        var send = Assert.Single(device.PeriodicOscSends);
        Assert.Equal("/xremote", send.Address);
    }

    [Fact]
    public async Task ContextMenu_AddHelperFile_AddsProjectScriptWithPath()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig());

        var row = vm.StructureRows.First(r => r.IsGroup);
        row.AddHelperFileCommand!.Execute(null);

        var script = Assert.Single(vm.BuildSnapshot().Scripts);
        Assert.Equal(ControlScriptScope.Project, script.Scope);
        Assert.Equal("Scripts/helper.mnd", script.ScriptPath);
    }

    [Fact]
    public async Task ResolveMidiDevices_AppliesUserSelectionToBinding()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var deviceId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = deviceId,
                    Name = "X-Touch MINI",
                    Protocol = ControlDeviceProtocol.Midi,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig { MidiInputDeviceName = "X-Touch MINI" },
                },
            ],
        });

        var inputs = new[]
        {
            new ControlMidiPortInfo(1, "X-Touch MINI"),
            new ControlMidiPortInfo(2, "X-Touch MINI"),
        };
        vm.MidiCatalogProvider = () => new ControlMidiPortCatalog(inputs, Array.Empty<ControlMidiPortInfo>());

        IReadOnlyList<ControlMidiResolutionRequest>? captured = null;
        vm.MidiResolutionPrompt = requests =>
        {
            captured = requests;
            var map = new Dictionary<ControlMidiResolutionKey, ControlMidiPortInfo>
            {
                [new ControlMidiResolutionKey(deviceId, ControlMidiPortDirection.Input)] = new(2, "X-Touch MINI"),
            };
            return Task.FromResult<IReadOnlyDictionary<ControlMidiResolutionKey, ControlMidiPortInfo>?>(map);
        };

        await vm.ResolveMidiDevicesCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(ControlDeviceMatchStatus.Ambiguous, Assert.Single(captured!).Status);
        Assert.Equal(2, Assert.Single(vm.BuildSnapshot().Devices).Binding.MidiInputDeviceId);
    }

    [Fact]
    public async Task ResolveMidiDevices_WhenAllMatch_DoesNotPrompt()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Name = "X-Touch MINI",
                    Protocol = ControlDeviceProtocol.Midi,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig { MidiInputDeviceName = "X-Touch MINI" },
                },
            ],
        });
        vm.MidiCatalogProvider = () => new ControlMidiPortCatalog(
            [new ControlMidiPortInfo(1, "X-Touch MINI")],
            Array.Empty<ControlMidiPortInfo>());

        var prompted = false;
        vm.MidiResolutionPrompt = _ =>
        {
            prompted = true;
            return Task.FromResult<IReadOnlyDictionary<ControlMidiResolutionKey, ControlMidiPortInfo>?>(null);
        };

        await vm.ResolveMidiDevicesCommand.ExecuteAsync(null);

        Assert.False(prompted);
        Assert.Contains("resolve to a current port", vm.StatusMessage);
    }

    [Fact]
    public void FindLearnCapture_ReturnsFirstMidiInputAfterBaseline()
    {
        var since = DateTimeOffset.Parse("2026-06-05T10:00:00Z");
        var records = new[]
        {
            // before baseline — ignored
            new ControlMonitorRecord
            {
                TimestampUtc = since.AddSeconds(-1),
                Direction = ControlMonitorDirection.Input,
                Protocol = ControlMonitorProtocol.Midi,
                MidiChannel = 1,
                MidiController = 99,
            },
            // OSC — ignored
            new ControlMonitorRecord
            {
                TimestampUtc = since.AddSeconds(1),
                Direction = ControlMonitorDirection.Input,
                Protocol = ControlMonitorProtocol.Osc,
                Address = "/ch/01/mix/fader",
            },
            // output MIDI — ignored
            new ControlMonitorRecord
            {
                TimestampUtc = since.AddSeconds(2),
                Direction = ControlMonitorDirection.Output,
                Protocol = ControlMonitorProtocol.Midi,
                MidiController = 16,
            },
            // first matching input — captured
            new ControlMonitorRecord
            {
                TimestampUtc = since.AddSeconds(3),
                Direction = ControlMonitorDirection.Input,
                Protocol = ControlMonitorProtocol.Midi,
                MidiChannel = 1,
                MidiController = 16,
                MidiValue = 5,
            },
        };

        var capture = ControlWorkspaceViewModel.FindLearnCapture(records, since);

        Assert.NotNull(capture);
        Assert.Equal(16, capture!.MidiController);
        Assert.Equal(5, capture.MidiValue);
    }

    [Fact]
    public void BuildLearnedTrigger_MapsCcAndNoteRecords()
    {
        var cc = ControlWorkspaceViewModel.BuildLearnedTrigger(
            new ControlMonitorRecord { MidiChannel = 1, MidiController = 16 },
            "onCc16");
        Assert.Equal(ControlScriptTriggerKind.MidiControlChange, cc.Kind);
        Assert.Equal("onCc16", cc.FunctionName);
        Assert.Equal(1, cc.MidiChannel);
        Assert.Equal(16, cc.MidiController);
        Assert.Null(cc.MidiNote);

        var note = ControlWorkspaceViewModel.BuildLearnedTrigger(
            new ControlMonitorRecord { MidiChannel = 1, MidiNote = 40 },
            "onNote40");
        Assert.Equal(ControlScriptTriggerKind.MidiNote, note.Kind);
        Assert.Equal(40, note.MidiNote);
        Assert.Null(note.MidiController);
    }

    [Fact]
    public void HasExport_DetectsExistingExportedFunction()
    {
        const string script = "export fun onCc16(event, context) { return 1; }";
        Assert.True(ControlWorkspaceViewModel.HasExport(script, "onCc16"));
        Assert.False(ControlWorkspaceViewModel.HasExport(script, "onCc17"));
        Assert.False(ControlWorkspaceViewModel.HasExport(string.Empty, "onCc16"));
    }

    [Fact]
    public async Task ConfirmLearn_AddsTriggerAndAppendsStub()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts = [new ControlScriptConfig { Name = "Script" }],
        });
        var row = Assert.Single(vm.ScriptRows);
        vm.SelectedScriptText = "// existing";

        vm.ApplyLearnCapture(new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Midi,
            MidiChannel = 1,
            MidiController = 16,
            MidiValue = 5,
        });

        Assert.True(vm.HasLearnCandidate);
        Assert.Equal("onCc16", vm.LearnCandidate!.FunctionName);
        Assert.True(vm.ConfirmLearnCommand.CanExecute(null));

        vm.ConfirmLearnCommand.Execute(null);

        Assert.False(vm.HasLearnCandidate);
        var trigger = Assert.Single(Assert.Single(vm.BuildSnapshot().Scripts).Triggers);
        Assert.Equal(ControlScriptTriggerKind.MidiControlChange, trigger.Kind);
        Assert.Equal(16, trigger.MidiController);
        Assert.Equal("onCc16", trigger.FunctionName);
        Assert.Contains("export fun onCc16", vm.SelectedScriptText);
        Assert.Contains("onCc16", row.TriggerSummary);
    }

    [Fact]
    public async Task ConfirmLearn_DoesNotDuplicateExistingExport()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts = [new ControlScriptConfig { Name = "Script" }],
        });
        vm.SelectedScriptText = "export fun onNote40(event, context) { return 1; }";

        vm.ApplyLearnCapture(new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Midi,
            MidiChannel = 1,
            MidiNote = 40,
        });
        vm.ConfirmLearnCommand.Execute(null);

        // Only the original export should be present (no appended stub).
        var occurrences = vm.SelectedScriptText.Split("export fun onNote40").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.Equal(ControlScriptTriggerKind.MidiNote, Assert.Single(Assert.Single(vm.BuildSnapshot().Scripts).Triggers).Kind);
    }

    [Fact]
    public void BuildStructureRows_ListsConfiguredControlItems()
    {
        var midiId = Guid.NewGuid();
        var oscId = Guid.NewGuid();
        var scriptId = Guid.NewGuid();
        var listenerId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            OscListeners =
            [
                new ControlOscListenerConfig
                {
                    Id = listenerId,
                    Name = "Main",
                    LocalPort = 10020,
                },
            ],
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = midiId,
                    Name = "X-Touch MINI",
                    Protocol = ControlDeviceProtocol.Midi,
                    Binding = new ControlDeviceBindingConfig
                    {
                        MidiInputDeviceId = 1,
                        MidiInputDeviceName = "X-Touch MINI",
                        MidiOutputDeviceId = 2,
                        MidiOutputDeviceName = "X-Touch MINI",
                    },
                },
                new ControlDeviceInstanceConfig
                {
                    Id = oscId,
                    Name = "X32",
                    Protocol = ControlDeviceProtocol.Osc,
                    Binding = new ControlDeviceBindingConfig
                    {
                        OscHost = "192.168.2.76",
                        OscPort = 10023,
                        OscListenerId = listenerId,
                    },
                    PeriodicOscSends =
                    [
                        new ControlPeriodicOscSendConfig
                        {
                            Name = "/xremote",
                            Address = "/xremote",
                            IntervalMs = 8000,
                        },
                    ],
                },
            ],
            Layers =
            [
                new ControlLayerConfig
                {
                    Name = "Layer A",
                    IsEnabled = true,
                    Priority = 10,
                    ScriptIds = [scriptId],
                },
            ],
            Scripts =
            [
                new ControlScriptConfig
                {
                    Id = scriptId,
                    Name = "Fader script",
                    ScriptPath = "Scripts/faders.mnd",
                    Scope = ControlScriptScope.Device,
                    Triggers =
                    [
                        new ControlScriptTriggerConfig
                        {
                            Kind = ControlScriptTriggerKind.MidiControlChange,
                            MidiController = 16,
                        },
                    ],
                },
            ],
        };

        var rows = ControlWorkspaceViewModel.BuildStructureRows(config);

        Assert.Contains(rows, r => r.IsGroup && r.Name == "MIDI devices" && r.Detail == "1 configured");
        Assert.Contains(rows, r => r.Kind == "MIDI" && r.Name == "X-Touch MINI" && r.Detail.Contains("in: X-Touch MINI #1"));
        Assert.Contains(rows, r => r.Kind == "Listen" && r.Detail.Contains("port 10020"));
        Assert.Contains(rows, r => r.Kind == "OSC" && r.Detail.Contains("192.168.2.76:10023"));
        Assert.Contains(rows, r => r.Kind == "Layer" && r.Name == "Layer A" && r.Detail.Contains("priority 10"));
        Assert.Contains(rows, r => r.Kind == "Script" && r.Name == "Fader script" && r.Detail.Contains("Device"));
        Assert.Contains(rows, r => r.Kind == "Periodic" && r.Name == "/xremote" && r.Detail.Contains("8000 ms"));
    }

    [Fact]
    public void BuildProfileWarnings_ReportsMissingAndMismatchedProfilesAsSuggestions()
    {
        var config = new ControlSystemConfig
        {
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Name = "Known X32",
                    ProfileId = "behringer.x32.osc",
                    Protocol = ControlDeviceProtocol.Osc,
                },
                new ControlDeviceInstanceConfig
                {
                    Name = "Unknown Surface",
                    ProfileId = "missing.profile",
                    Protocol = ControlDeviceProtocol.Midi,
                },
                new ControlDeviceInstanceConfig
                {
                    Name = "Wrong Protocol",
                    ProfileId = "behringer.x32.osc",
                    Protocol = ControlDeviceProtocol.Midi,
                },
            ],
        };

        var warnings = ControlWorkspaceViewModel.BuildProfileWarnings(
            config,
            CompositeControlDeviceProfileRepository.ForProject(config));

        Assert.DoesNotContain(warnings, warning => warning.Contains("Known X32"));
        Assert.Contains(warnings, warning => warning.Contains("missing.profile") && warning.Contains("raw Midi scripting"));
        Assert.Contains(warnings, warning => warning.Contains("Wrong Protocol") && warning.Contains("device is Midi"));
    }

    [Fact]
    public void BuildX32CommandRows_ListsProfileCommandsWithCacheValues()
    {
        var x32Id = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = x32Id,
                    Name = "X32",
                    ProfileId = "behringer.x32.osc",
                    Protocol = ControlDeviceProtocol.Osc,
                    Binding = new ControlDeviceBindingConfig { Alias = "x32" },
                },
            ],
        };
        var cache = new ControlValueCache();
        cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming, timestamp: DateTimeOffset.Parse("2026-06-04T10:00:00Z"));

        var rows = ControlWorkspaceViewModel.BuildX32CommandRows(
            config,
            CompositeControlDeviceProfileRepository.ForProject(config),
            cache);

        Assert.Contains(rows, row =>
            row.DeviceName == "X32"
            && row.CommandName == "Ch 01 Fader"
            && row.Address == "/ch/01/mix/fader"
            && row.ValueKind == nameof(ControlCommandValueKind.NormalizedFloat)
            && row.CacheValue.Contains("0.5"));
        Assert.Contains(rows, row => row.CommandName == "Main Stereo Fader" && row.CacheValue == "(uncached)");
    }

    [Fact]
    public void ApplyMonitorFilters_FiltersByDirectionProtocolAndDevice()
    {
        var xtouchId = Guid.NewGuid();
        var records = new[]
        {
            new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Input,
                Protocol = ControlMonitorProtocol.Midi,
                DeviceInstanceId = xtouchId,
                DeviceKey = "xtouch",
                Endpoint = "X-Touch MINI",
                Message = "cc",
            },
            new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Output,
                Protocol = ControlMonitorProtocol.Osc,
                DeviceKey = "x32",
                Endpoint = "192.168.2.76:10023",
                Message = "send",
            },
            new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Input,
                Protocol = ControlMonitorProtocol.Osc,
                DeviceKey = "x32",
                Endpoint = "192.168.2.76:10023",
                Message = "receive",
            },
        };

        var filtered = ControlWorkspaceViewModel.ApplyMonitorFilters(
            records,
            new ControlMonitorFilterSettings(
                ErrorsOnly: false,
                Text: string.Empty,
                Direction: nameof(ControlMonitorDirection.Input),
                Protocol: nameof(ControlMonitorProtocol.Midi),
                DeviceText: "xtouch")).ToArray();

        var record = Assert.Single(filtered);
        Assert.Equal(xtouchId, record.DeviceInstanceId);
    }

    [Fact]
    public void ApplyMonitorFilters_ComposesErrorsOnlyAndTextSearch()
    {
        var records = new[]
        {
            new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Output,
                Protocol = ControlMonitorProtocol.Osc,
                Result = ControlMonitorResult.Sent,
                Address = "/ch/01/mix/fader",
            },
            new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Osc,
                Result = ControlMonitorResult.Failed,
                Address = "/ch/01/mix/fader",
                ErrorMessage = "timeout",
            },
            new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Midi,
                Result = ControlMonitorResult.Failed,
                ErrorMessage = "missing device",
            },
        };

        var filtered = ControlWorkspaceViewModel.ApplyMonitorFilters(
            records,
            new ControlMonitorFilterSettings(
                ErrorsOnly: true,
                Text: "fader",
                Direction: "All",
                Protocol: "All",
                DeviceText: string.Empty)).ToArray();

        var record = Assert.Single(filtered);
        Assert.Equal("timeout", record.ErrorMessage);
    }
}
