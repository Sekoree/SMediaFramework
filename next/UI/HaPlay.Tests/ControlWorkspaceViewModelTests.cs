using System.IO;
using S.Control;
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
    public async Task NewWorkspace_ShowsStructureGroupsBeforeAnyProjectOrDevice()
    {
        await using var vm = new ControlWorkspaceViewModel();

        // The structure must be present from construction — no project loaded, no MIDI/OSC device — so an
        // OSC-only or scripts/layers-only setup can be prepared straight away.
        Assert.Contains(vm.StructureRows, r => r.IsGroup && r.Name == "MIDI devices");
        Assert.Contains(vm.StructureRows, r => r.IsGroup && r.Name == "OSC devices");
        Assert.Contains(vm.StructureRows, r => r.IsGroup && r.Name == "Layers");
        Assert.Contains(vm.StructureRows, r => r.IsGroup && r.Name == "Scripts");
    }

    [Fact]
    public async Task AddScript_SeedsUniqueDefaultScriptPaths()
    {
        await using var vm = new ControlWorkspaceViewModel();

        vm.AddScriptCommand.Execute(null);
        Assert.Equal("Scripts/script-1.mnd", vm.SelectedScriptRow!.Script.ScriptPath);
        Assert.True(vm.SaveSelectedScriptCommand.CanExecute(null)); // a path means Save is reachable

        vm.AddScriptCommand.Execute(null);
        Assert.Equal("Scripts/script-2.mnd", vm.SelectedScriptRow!.Script.ScriptPath);
    }

    [Fact]
    public async Task SaveSelectedScript_WithUnsavedProject_PromptsToSaveThenWritesFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "haplay-save-prompt-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var vm = new ControlWorkspaceViewModel();
            vm.LoadConfig(new ControlSystemConfig()); // unsaved project: no project root
            vm.AddScriptCommand.Execute(null);
            vm.SelectedScriptText = "export fun run() { return 1; }";

            var prompted = false;
            vm.EnsureProjectSavedAsync = () =>
            {
                prompted = true;
                vm.SetProjectRoot(root); // simulate the user completing the Save-As flow
                return Task.FromResult(true);
            };

            await vm.SaveSelectedScriptCommand.ExecuteAsync(null);

            Assert.True(prompted);
            var path = Path.Combine(root, vm.SelectedScriptRow!.Script.ScriptPath);
            Assert.True(File.Exists(path));
            Assert.Contains("return 1", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSelectedScript_WhenProjectSaveDeclined_ReportsStatusAndDoesNotThrow()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig());
        vm.AddScriptCommand.Execute(null);
        vm.SelectedScriptText = "export fun run() { return 1; }";
        vm.EnsureProjectSavedAsync = () => Task.FromResult(false); // user cancels the project Save dialog

        await vm.SaveSelectedScriptCommand.ExecuteAsync(null);

        Assert.Contains("Save the project first", vm.ScriptEditorStatus);
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

        triggerRow.Kind = ControlScriptTriggerKind.MidiMessage;
        triggerRow.FunctionName = "  onProgram  ";
        triggerRow.MidiControllerText = "7";
        triggerRow.MidiNoteText = "60";
        triggerRow.MidiParameterText = "12";
        triggerRow.MidiMessageType = ControlMidiMessageType.ProgramChange;
        triggerRow.MidiChannelText = "1";
        triggerRow.MidiValueText = "5";

        Assert.True(triggerRow.ShowMidiMessageType);
        Assert.True(triggerRow.ShowMidiChannel);
        Assert.False(triggerRow.ShowMidiController);
        Assert.False(triggerRow.ShowMidiNote);
        Assert.True(triggerRow.ShowMidiValue);
        Assert.False(triggerRow.ShowMidiParameter);
        Assert.False(triggerRow.ShowOscAddress);

        var trigger = Assert.Single(Assert.Single(vm.BuildSnapshot().Scripts).Triggers);
        Assert.Equal(ControlScriptTriggerKind.MidiMessage, trigger.Kind);
        Assert.Equal("onProgram", trigger.FunctionName);
        Assert.Equal(ControlMidiMessageType.ProgramChange, trigger.MidiMessageType);
        Assert.Equal(1, trigger.MidiChannel);
        Assert.Equal(5, trigger.MidiValue);
        Assert.Null(trigger.MidiController);
        Assert.Null(trigger.MidiNote);
        Assert.Null(trigger.MidiParameter);
        Assert.Contains("onProgram", row.TriggerSummary);
        Assert.Contains(nameof(ControlMidiMessageType.ProgramChange), row.TriggerSummary);
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
    public async Task AddPeriodicSend_AddsSendWithChosenInterval()
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

        vm.PeriodicSendPrompt = dialog =>
        {
            Assert.Equal("/xremote", dialog.Address);
            Assert.Equal("8000", dialog.IntervalMsText);
            dialog.IntervalMsText = "5000";
            return Task.FromResult(true);
        };

        var deviceRow = vm.StructureRows.Single(r => r.Kind == "OSC" && r.DeviceInstanceId == deviceId);
        deviceRow.AddPeriodicSendCommand!.Execute(null);
        await Task.Yield();

        var send = Assert.Single(Assert.Single(vm.BuildSnapshot().Devices).PeriodicOscSends);
        Assert.Equal("/xremote", send.Address);
        Assert.Equal(5000, send.IntervalMs);
    }

    [Fact]
    public async Task EditAndRemovePeriodicSend_UpdateTheDeviceSendList()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var deviceId = Guid.NewGuid();
        var sendId = Guid.NewGuid();
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
                    PeriodicOscSends =
                    [
                        new ControlPeriodicOscSendConfig { Id = sendId, Name = "/xremote", Address = "/xremote", IntervalMs = 8000 },
                    ],
                },
            ],
        });

        vm.PeriodicSendPrompt = dialog =>
        {
            Assert.Equal(8000.ToString(), dialog.IntervalMsText);
            dialog.IntervalMsText = "5000";
            return Task.FromResult(true);
        };

        var periodicRow = vm.StructureRows.Single(r => r.Kind == "Periodic" && r.PeriodicSendId == sendId);
        Assert.True(periodicRow.CanEditPeriodicSend);
        Assert.False(periodicRow.CanEditOscDevice); // periodic rows don't offer device-level actions

        periodicRow.EditPeriodicSendCommand!.Execute(null);
        await Task.Yield();
        Assert.Equal(5000, Assert.Single(Assert.Single(vm.BuildSnapshot().Devices).PeriodicOscSends).IntervalMs);

        var refreshedRow = vm.StructureRows.Single(r => r.Kind == "Periodic" && r.PeriodicSendId == sendId);
        refreshedRow.RemovePeriodicSendCommand!.Execute(null);
        Assert.Empty(Assert.Single(vm.BuildSnapshot().Devices).PeriodicOscSends);
    }

    [Fact]
    public async Task AddOscDevice_CreatesOscDeviceFromDialogValues()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig());

        vm.OscDevicePrompt = dialog =>
        {
            // Verify the dialog is seeded with the X32 defaults, then accept.
            Assert.Equal("X32", dialog.Name);
            Assert.Equal("192.168.2.76", dialog.Host);
            Assert.Equal("10023", dialog.PortText);
            Assert.Equal("x32", dialog.Alias);
            return Task.FromResult(true);
        };

        await vm.AddOscDeviceCommand.ExecuteAsync(null);

        var device = Assert.Single(vm.BuildSnapshot().Devices);
        Assert.Equal(ControlDeviceProtocol.Osc, device.Protocol);
        Assert.Equal("X32", device.Name);
        Assert.Equal("behringer.x32.osc", device.ProfileId);
        Assert.Equal("x32", device.Binding.Alias);
        Assert.Equal("192.168.2.76", device.Binding.OscHost);
        Assert.Equal(10023, device.Binding.OscPort);
        Assert.Null(device.Binding.OscLocalPort); // blank local port = automatic/ephemeral
        Assert.True(device.IsEnabled);
    }

    [Fact]
    public async Task AddOscDevice_SetsFixedLocalPortFromDialog()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig());
        vm.OscDevicePrompt = dialog =>
        {
            dialog.LocalPortText = "10024";
            Assert.True(dialog.IsValid);
            return Task.FromResult(true);
        };

        await vm.AddOscDeviceCommand.ExecuteAsync(null);

        Assert.Equal(10024, Assert.Single(vm.BuildSnapshot().Devices).Binding.OscLocalPort);
    }

    [Fact]
    public async Task AddOscDevice_WhenCancelled_AddsNothing()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig());
        vm.OscDevicePrompt = _ => Task.FromResult(false);

        await vm.AddOscDeviceCommand.ExecuteAsync(null);

        Assert.Empty(vm.BuildSnapshot().Devices);
    }

    [Fact]
    public async Task AddOscListener_CreatesListenerFromDialogValues()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig()); // no listeners by default

        vm.OscListenerPrompt = dialog =>
        {
            Assert.Equal("10020", dialog.LocalPortText); // first listener seeds the conventional port
            dialog.Name = "Lighting in";
            dialog.LocalPortText = "9100";
            return Task.FromResult(true);
        };

        await vm.AddOscListenerCommand.ExecuteAsync(null);

        var listener = Assert.Single(vm.BuildSnapshot().OscListeners);
        Assert.Equal("Lighting in", listener.Name);
        Assert.Equal(9100, listener.LocalPort);
        Assert.True(listener.IsEnabled);
        Assert.Equal(1, vm.ListenerCount);
        Assert.Contains(vm.StructureRows, r => r.Kind == "Listen" && r.OscListenerId == listener.Id);
    }

    [Fact]
    public async Task AddOscListener_WhenCancelled_AddsNothing()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig());
        vm.OscListenerPrompt = _ => Task.FromResult(false);

        await vm.AddOscListenerCommand.ExecuteAsync(null);

        Assert.Empty(vm.BuildSnapshot().OscListeners);
    }

    [Fact]
    public async Task EditOscListener_UpdatesNamePortAndEnabled()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var listenerId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            OscListeners = [new ControlOscListenerConfig { Id = listenerId, Name = "Old", LocalPort = 10020, IsEnabled = true }],
        });

        vm.OscListenerPrompt = dialog =>
        {
            Assert.Equal("Old", dialog.Name);
            Assert.Equal("10020", dialog.LocalPortText);
            dialog.Name = "New";
            dialog.LocalPortText = "10030";
            dialog.IsEnabled = false;
            return Task.FromResult(true);
        };

        var row = vm.StructureRows.Single(r => r.Kind == "Listen" && r.OscListenerId == listenerId);
        Assert.True(row.CanEditOscListener);
        row.EditOscListenerCommand!.Execute(null);
        await Task.Yield();

        var listener = Assert.Single(vm.BuildSnapshot().OscListeners);
        Assert.Equal("New", listener.Name);
        Assert.Equal(10030, listener.LocalPort);
        Assert.False(listener.IsEnabled);
    }

    [Fact]
    public async Task RemoveOscListener_RemovesListener()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var listenerId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            OscListeners = [new ControlOscListenerConfig { Id = listenerId, Name = "Aux", LocalPort = 10021 }],
        });

        var row = vm.StructureRows.Single(r => r.Kind == "Listen" && r.OscListenerId == listenerId);
        Assert.True(row.CanEditOscListener);
        row.RemoveOscListenerCommand!.Execute(null);

        Assert.Empty(vm.BuildSnapshot().OscListeners);
    }

    [Fact]
    public async Task EditOscDevice_UpdatesHostAndPreservesPeriodicSends()
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
                    ProfileId = "behringer.x32.osc",
                    Protocol = ControlDeviceProtocol.Osc,
                    Binding = new ControlDeviceBindingConfig { Alias = "x32", OscHost = "192.168.2.76", OscPort = 10023 },
                    PeriodicOscSends = [new ControlPeriodicOscSendConfig()],
                },
            ],
        });

        vm.OscDevicePrompt = dialog =>
        {
            Assert.Equal("Edit OSC device", dialog.Title);
            Assert.Equal("192.168.2.76", dialog.Host);
            dialog.Host = "10.0.0.5";
            dialog.PortText = "10024";
            return Task.FromResult(true);
        };

        var row = vm.StructureRows.Single(r => r.Kind == "OSC" && r.DeviceInstanceId == deviceId);
        row.EditOscDeviceCommand!.Execute(null);
        await Task.Yield();

        var device = Assert.Single(vm.BuildSnapshot().Devices);
        Assert.Equal("10.0.0.5", device.Binding.OscHost);
        Assert.Equal(10024, device.Binding.OscPort);
        Assert.Single(device.PeriodicOscSends); // preserved across edit
    }

    [Fact]
    public async Task RemoveOscDevice_DropsTheDevice()
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
        Assert.True(row.CanEditOscDevice);
        row.RemoveOscDeviceCommand!.Execute(null);

        Assert.Empty(vm.BuildSnapshot().Devices);
    }

    [Fact]
    public async Task ContextMenu_AddEndpointScript_AddsEndpointScopedScriptForListener()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var listenerId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            OscListeners = [new ControlOscListenerConfig { Id = listenerId, Name = "Main", LocalPort = 10020 }],
        });

        var row = vm.StructureRows.Single(r => r.Kind == "Listen" && r.OscListenerId == listenerId);
        Assert.True(row.CanAddEndpointScript);
        Assert.False(row.CanAddDeviceScript);

        row.AddEndpointScriptCommand!.Execute(null);

        var script = Assert.Single(vm.BuildSnapshot().Scripts);
        Assert.Equal(ControlScriptScope.Endpoint, script.Scope);
        Assert.Equal(listenerId, script.EndpointInstanceId);
    }

    [Fact]
    public void WithActiveLayer_IsMutuallyExclusiveAndIdempotent()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Layers =
            [
                new ControlLayerConfig { Id = a, Name = "A", IsEnabled = true },
                new ControlLayerConfig { Id = b, Name = "B", IsEnabled = false },
            ],
        };

        var switched = ControlWorkspaceViewModel.WithActiveLayer(config, b);
        Assert.False(switched.Layers.Single(l => l.Id == a).IsEnabled);
        Assert.True(switched.Layers.Single(l => l.Id == b).IsEnabled);
        Assert.Same(switched, ControlWorkspaceViewModel.WithActiveLayer(switched, b));
        Assert.Same(config, ControlWorkspaceViewModel.WithActiveLayer(config, a));
    }

    [Fact]
    public void WithActiveLayer_CanDeactivateAllLayers()
    {
        var layerId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Layers = [new ControlLayerConfig { Id = layerId, Name = "A", IsEnabled = true }],
        };

        var cleared = ControlWorkspaceViewModel.WithActiveLayer(config, activeLayerId: null);
        Assert.All(cleared.Layers, layer => Assert.False(layer.IsEnabled));
    }

    [Fact]
    public async Task ActivateLayer_IsMutuallyExclusiveInConfig()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Layers =
            [
                new ControlLayerConfig { Id = a, Name = "A", IsEnabled = true },
                new ControlLayerConfig { Id = b, Name = "B", IsEnabled = false },
            ],
        });

        var rowB = vm.StructureRows.Single(r => r.Kind == "Layer" && r.LayerId == b);
        Assert.True(rowB.CanActivateLayer);

        rowB.ActivateLayerCommand!.Execute(null);

        var layers = vm.BuildSnapshot().Layers;
        Assert.False(layers.Single(l => l.Id == a).IsEnabled);
        Assert.True(layers.Single(l => l.Id == b).IsEnabled);
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
    public async Task AddLayer_CreatesActiveLayerFromDialogValues()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig());

        vm.LayerPrompt = dialog =>
        {
            // First layer: seeded with "Layer 1", priority 0, and active because nothing else is.
            Assert.Equal("Layer 1", dialog.Name);
            Assert.Equal("0", dialog.PriorityText);
            Assert.True(dialog.IsActive);
            dialog.Name = "Verse";
            dialog.PriorityText = "5";
            return Task.FromResult(true);
        };

        await vm.AddLayerCommand.ExecuteAsync(null);

        var layer = Assert.Single(vm.BuildSnapshot().Layers);
        Assert.Equal("Verse", layer.Name);
        Assert.Equal(5, layer.Priority);
        Assert.True(layer.IsEnabled);
        Assert.Equal(1, vm.LayerCount);
    }

    [Fact]
    public async Task AddLayer_ActiveSelectionIsMutuallyExclusive()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var existing = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Layers = [new ControlLayerConfig { Id = existing, Name = "A", IsEnabled = true }],
        });

        vm.LayerPrompt = dialog =>
        {
            // A second layer defaults to inactive; force it active to verify exclusivity.
            Assert.False(dialog.IsActive);
            dialog.Name = "B";
            dialog.IsActive = true;
            return Task.FromResult(true);
        };

        await vm.AddLayerCommand.ExecuteAsync(null);

        var layers = vm.BuildSnapshot().Layers;
        Assert.Equal(2, layers.Count);
        Assert.False(layers.Single(l => l.Id == existing).IsEnabled);
        Assert.True(layers.Single(l => l.Name == "B").IsEnabled);
    }

    [Fact]
    public async Task AddLayer_WhenCancelled_AddsNothing()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig());
        vm.LayerPrompt = _ => Task.FromResult(false);

        await vm.AddLayerCommand.ExecuteAsync(null);

        Assert.Empty(vm.BuildSnapshot().Layers);
    }

    [Fact]
    public async Task EditLayer_UpdatesNamePriorityAndActive()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var layerId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Layers = [new ControlLayerConfig { Id = layerId, Name = "Old", Priority = 1, IsEnabled = false }],
        });

        vm.LayerPrompt = dialog =>
        {
            Assert.Equal("Old", dialog.Name);
            Assert.Equal("1", dialog.PriorityText);
            Assert.False(dialog.IsActive);
            dialog.Name = "New";
            dialog.PriorityText = "9";
            dialog.IsActive = true;
            return Task.FromResult(true);
        };

        var row = vm.StructureRows.Single(r => r.Kind == "Layer" && r.LayerId == layerId);
        Assert.True(row.CanEditLayer);
        row.EditLayerCommand!.Execute(null);
        await Task.Yield();

        var layer = Assert.Single(vm.BuildSnapshot().Layers);
        Assert.Equal("New", layer.Name);
        Assert.Equal(9, layer.Priority);
        Assert.True(layer.IsEnabled);
    }

    [Fact]
    public async Task RemoveLayer_RemovesLayerAndUnbindsScopedScripts()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var layerId = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Layers = [new ControlLayerConfig { Id = layerId, Name = "Chorus" }],
            Scripts =
            [
                new ControlScriptConfig
                {
                    Name = "Layer script",
                    Scope = ControlScriptScope.Layer,
                    LayerId = layerId,
                },
            ],
        });

        var row = vm.StructureRows.Single(r => r.Kind == "Layer" && r.LayerId == layerId);
        Assert.True(row.CanEditLayer);

        row.RemoveLayerCommand!.Execute(null);

        Assert.Empty(vm.BuildSnapshot().Layers);
        var script = Assert.Single(vm.BuildSnapshot().Scripts);
        Assert.Null(script.LayerId); // unbound so a dangling id can't silently disable it
    }

    [Fact]
    public async Task ScriptRow_SelectingLayerScope_AutoBindsFirstLayerAndPickerIsSelectable()
    {
        await using var vm = new ControlWorkspaceViewModel();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        vm.LoadConfig(new ControlSystemConfig
        {
            Layers =
            [
                new ControlLayerConfig { Id = first, Name = "First", Priority = 0 },
                new ControlLayerConfig { Id = second, Name = "Second", Priority = 1 },
            ],
            Scripts = [new ControlScriptConfig { Name = "Script", Scope = ControlScriptScope.Project }],
        });

        var row = Assert.Single(vm.ScriptRows);
        Assert.Equal(2, row.LayerOptions.Count);
        Assert.False(row.ShowLayerPicker);

        row.Scope = ControlScriptScope.Layer;

        Assert.True(row.ShowLayerPicker);
        Assert.True(row.ShowLayerSelector);
        Assert.False(row.ShowLayerHint);
        Assert.Equal(first, row.SelectedLayer?.Id); // lowest-priority layer auto-bound
        Assert.Equal(first, vm.BuildSnapshot().Scripts.Single().LayerId);

        row.SelectedLayer = row.LayerOptions.Single(o => o.Id == second);
        Assert.Equal(second, vm.BuildSnapshot().Scripts.Single().LayerId);
    }

    [Fact]
    public async Task ScriptRow_LayerScopeWithNoLayers_ShowsHint()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts = [new ControlScriptConfig { Name = "Script", Scope = ControlScriptScope.Project }],
        });

        var row = Assert.Single(vm.ScriptRows);
        row.Scope = ControlScriptScope.Layer;

        Assert.True(row.ShowLayerPicker);
        Assert.False(row.ShowLayerSelector);
        Assert.True(row.ShowLayerHint);
        Assert.Null(row.SelectedLayer);
        Assert.Null(vm.BuildSnapshot().Scripts.Single().LayerId);
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

        vm.PeriodicSendPrompt = _ => Task.FromResult(true); // accept the dialog defaults (/xremote @ 8000)
        row.AddPeriodicSendCommand!.Execute(null);
        await Task.Yield();

        var device = Assert.Single(vm.BuildSnapshot().Devices);
        var send = Assert.Single(device.PeriodicOscSends);
        Assert.Equal("/xremote", send.Address);
        Assert.Equal(8000, send.IntervalMs);
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
        Assert.Equal(ControlMidiMessageType.ControlChange, cc.MidiMessageType);
        Assert.Equal(16, cc.MidiController);
        Assert.Null(cc.MidiNote);

        var note = ControlWorkspaceViewModel.BuildLearnedTrigger(
            new ControlMonitorRecord { MidiChannel = 1, MidiMessageType = ControlMidiMessageType.NoteOff, MidiNote = 40 },
            "onNote40");
        Assert.Equal(ControlScriptTriggerKind.MidiNote, note.Kind);
        Assert.Equal(ControlMidiMessageType.NoteOff, note.MidiMessageType);
        Assert.Equal(40, note.MidiNote);
        Assert.Null(note.MidiController);
    }

    [Fact]
    public void BuildLearnedTrigger_MapsGenericMidiMessageRecords()
    {
        var program = ControlWorkspaceViewModel.BuildLearnedTrigger(
            new ControlMonitorRecord
            {
                MidiChannel = 1,
                MidiMessageType = ControlMidiMessageType.ProgramChange,
                MidiValue = 5,
            },
            "onProgramChange");

        Assert.Equal(ControlScriptTriggerKind.MidiMessage, program.Kind);
        Assert.Equal(ControlMidiMessageType.ProgramChange, program.MidiMessageType);
        Assert.Equal(1, program.MidiChannel);
        Assert.Equal(5, program.MidiValue);

        var bend = ControlWorkspaceViewModel.BuildLearnedTrigger(
            new ControlMonitorRecord
            {
                MidiChannel = 1,
                MidiMessageType = ControlMidiMessageType.PitchBend,
                MidiValue = 128,
            },
            "onPitchBend");

        Assert.Equal(ControlScriptTriggerKind.MidiMessage, bend.Kind);
        Assert.Equal(ControlMidiMessageType.PitchBend, bend.MidiMessageType);
        Assert.Equal(1, bend.MidiChannel);
        Assert.Null(bend.MidiValue);
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
        Assert.Null(trigger.MidiValueMin);
        Assert.Null(trigger.MidiValueMax);
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
    public async Task ConfirmLearn_PreservesObservedHighResolutionCcRange()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.LoadConfig(new ControlSystemConfig
        {
            Scripts = [new ControlScriptConfig { Name = "Script" }],
        });

        var deviceId = Guid.NewGuid();
        vm.ApplyLearnCapture(new ControlMonitorRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Midi,
            DeviceInstanceId = deviceId,
            MidiMessageType = ControlMidiMessageType.ControlChange,
            MidiChannel = 1,
            MidiController = 16,
            MidiValue = 0,
            MidiHighResolution14Bit = true,
        });
        vm.ApplyLearnCapture(new ControlMonitorRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow.AddMilliseconds(10),
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Midi,
            DeviceInstanceId = deviceId,
            MidiMessageType = ControlMidiMessageType.ControlChange,
            MidiChannel = 1,
            MidiController = 16,
            MidiValue = 10000,
            MidiHighResolution14Bit = true,
        });

        Assert.True(vm.HasLearnCandidate);
        Assert.Equal(0, vm.LearnCandidate!.MinimumValue);
        Assert.Equal(10000, vm.LearnCandidate.MaximumValue);

        vm.ConfirmLearnCommand.Execute(null);

        var trigger = Assert.Single(Assert.Single(vm.BuildSnapshot().Scripts).Triggers);
        Assert.Equal(ControlScriptTriggerKind.MidiControlChange, trigger.Kind);
        Assert.Equal(16, trigger.MidiController);
        Assert.Null(trigger.MidiValue);
        Assert.Equal(0, trigger.MidiValueMin);
        Assert.Equal(10000, trigger.MidiValueMax);
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
            // An app listener is opt-in now, so add one explicitly to exercise the port-collision warning.
            OscListeners = [new ControlOscListenerConfig { LocalPort = 10020 }],
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
                new ControlDeviceInstanceConfig
                {
                    Name = "Colliding OSC",
                    Protocol = ControlDeviceProtocol.Osc,
                    Binding = new ControlDeviceBindingConfig { OscLocalPort = 10020 },
                },
            ],
        };

        var warnings = ControlWorkspaceViewModel.BuildProfileWarnings(
            config,
            CompositeControlDeviceProfileRepository.ForProject(config));

        Assert.DoesNotContain(warnings, warning => warning.Contains("Known X32"));
        Assert.Contains(warnings, warning => warning.Contains("missing.profile") && warning.Contains("raw Midi scripting"));
        Assert.Contains(warnings, warning => warning.Contains("Wrong Protocol") && warning.Contains("device is Midi"));
        Assert.Contains(warnings, warning => warning.Contains("Colliding OSC") && warning.Contains("client source port 10020"));
    }

    [Fact]
    public void BuildProfileRows_ListsInstalledProfilesWithCapabilitySummary()
    {
        var rows = ControlWorkspaceViewModel.BuildProfileRows(BuiltInControlDeviceProfileRepository.Instance);

        Assert.Contains(rows, row =>
            row.Id == "behringer.xtouch-mini.mc"
            && row.Protocol == nameof(ControlDeviceProtocol.Midi)
            && row.Summary.Contains("controls"));
        Assert.Contains(rows, row =>
            row.Id == "behringer.x32.osc"
            && row.Protocol == nameof(ControlDeviceProtocol.Osc)
            && row.Summary.Contains("commands")
            && row.Summary.Contains("tasks"));
    }

    [Fact]
    public void BuildProfileRows_MarksProjectProfileOverrides()
    {
        var rows = ControlWorkspaceViewModel.BuildProfileRows(new ControlSystemConfig
        {
            DeviceProfileOverrides =
            [
                new ControlDeviceProfile
                {
                    Id = "custom.osc",
                    DisplayName = "Custom OSC",
                    Protocol = ControlDeviceProtocol.Osc,
                },
            ],
        });

        var row = Assert.Single(rows, row => row.Id == "custom.osc");
        Assert.Equal("Project", row.Source);
        Assert.True(row.IsProjectOverride);
    }

    [Fact]
    public async Task SaveMidiProfileBuilder_CreatesProjectMidiProfileWithCcRange()
    {
        await using var vm = new ControlWorkspaceViewModel
        {
            ProfileBuilderDisplayName = "Motor Fader",
            ProfileBuilderControlName = "Main Fader",
            ProfileBuilderMidiChannelText = "1",
            ProfileBuilderMidiControllerText = "16",
            ProfileBuilderHighResolution14Bit = true,
            ProfileBuilderMinValueText = "0",
            ProfileBuilderMaxValueText = "10000",
        };

        vm.SaveMidiProfileBuilderCommand.Execute(null);

        var profile = Assert.Single(vm.BuildSnapshot().DeviceProfileOverrides);
        Assert.Equal("custom.midi.motor-fader", profile.Id);
        Assert.Equal(ControlDeviceProtocol.Midi, profile.Protocol);
        Assert.Equal(2, profile.Ports.Count);
        var control = Assert.Single(profile.Controls);
        Assert.Equal(ControlProfileValueMode.Absolute14Bit, control.ValueMode);
        Assert.True(control.MidiHighResolution14Bit);
        Assert.Equal(16, control.MidiController);
        Assert.Equal(0, control.MidiValueMin);
        Assert.Equal(10000, control.MidiValueMax);
        Assert.Contains(vm.ProfileRows, row => row.Id == profile.Id && row.Source == "Project");
    }

    [Fact]
    public async Task ImportProfileFromFile_AddsProjectOverrideAndCanExportSelectedProfile()
    {
        var importRoot = Path.Combine(Path.GetTempPath(), "haplay-profile-import-" + Guid.NewGuid().ToString("N"));
        var exportRoot = Path.Combine(Path.GetTempPath(), "haplay-profile-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new ControlDeviceProfile
            {
                Id = "custom.osc",
                DisplayName = "Custom OSC",
                Protocol = ControlDeviceProtocol.Osc,
                Commands =
                [
                    new ControlCommandProfile
                    {
                        Id = "custom.fader",
                        DisplayName = "Custom Fader",
                        Address = "/custom/fader",
                        ValueKind = ControlCommandValueKind.NormalizedFloat,
                    },
                ],
            };
            var sourcePath = DirectoryControlDeviceProfileRepository.SaveProfile(importRoot, profile);
            await using var vm = new ControlWorkspaceViewModel();

            var imported = vm.ImportProfileFromFile(sourcePath);

            Assert.Equal("custom.osc", imported.Id);
            Assert.Contains(vm.BuildSnapshot().DeviceProfileOverrides, p => p.Id == "custom.osc");
            var row = Assert.Single(vm.ProfileRows, row => row.Id == "custom.osc");
            Assert.Equal("Project", row.Source);
            Assert.True(row.IsProjectOverride);

            var exportedPath = vm.ExportProfileToDirectory(row, exportRoot);
            var exported = DirectoryControlDeviceProfileRepository.LoadProfileFile(exportedPath);

            Assert.Equal("custom.osc", exported.Id);
            Assert.Equal("/custom/fader", Assert.Single(exported.Commands).Address);
        }
        finally
        {
            if (Directory.Exists(importRoot))
                Directory.Delete(importRoot, recursive: true);
            if (Directory.Exists(exportRoot))
                Directory.Delete(exportRoot, recursive: true);
        }
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
    public void BuildX32CommandRows_FiltersAndGroupsProfileCommands()
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
                    Binding = new ControlDeviceBindingConfig
                    {
                        Alias = "x32",
                        OscHost = "192.168.2.76",
                        OscPort = 10023,
                    },
                },
            ],
        };

        var rows = ControlWorkspaceViewModel.BuildX32CommandRows(
            config,
            CompositeControlDeviceProfileRepository.ForProject(config),
            cache: null,
            filterText: "channel 01 fader");

        var row = Assert.Single(rows);
        Assert.Equal(x32Id, row.DeviceInstanceId);
        Assert.Equal("x32", row.DeviceKey);
        Assert.Equal("192.168.2.76", row.Host);
        Assert.Equal(10023, row.Port);
        Assert.Equal("Channel 01", row.Group);
        Assert.Equal("Ch 01 Fader", row.CommandName);
        Assert.True(row.CanRequest);
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

    [Fact]
    public async Task SaveAndLoadControlConfigFromPath_RoundTripsSnapshot()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.AddOrUpdateMidiInputDevice(1, "X-Touch MINI");

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "." + ControlSystemIO.FileExtension);
        try
        {
            await vm.SaveControlConfigToPathAsync(path);
            Assert.Equal(path, vm.ConfigFilePath);

            await using var reloaded = new ControlWorkspaceViewModel();
            await reloaded.LoadControlConfigFromPathAsync(path);

            var device = Assert.Single(reloaded.BuildSnapshot().Devices);
            Assert.Equal("X-Touch MINI", device.Name);
            Assert.Equal(path, reloaded.ConfigFilePath);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
