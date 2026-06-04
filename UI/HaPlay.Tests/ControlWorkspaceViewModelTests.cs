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
