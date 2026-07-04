using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlSystemIOTests
{
    [Fact]
    public async Task SaveConfigAsync_RoundTripsStandaloneControlDocument()
    {
        var listenerId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var layerId = Guid.NewGuid();
        var scriptId = Guid.NewGuid();
        var triggerId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            OSCListeners =
            {
                new ControlOSCListenerConfig
                {
                    Id = listenerId,
                    Name = "Lighting In",
                    LocalPort = 10030,
                },
            },
            Devices =
            {
                new ControlDeviceInstanceConfig
                {
                    Id = deviceId,
                    Name = "X-Touch Mini",
                    Protocol = ControlDeviceProtocol.MIDI,
                    Binding = new ControlDeviceBindingConfig
                    {
                        MIDIInputDeviceName = "X-TOUCH MINI",
                        MIDIOutputDeviceName = "X-TOUCH MINI",
                    },
                    ScriptIds = { scriptId },
                },
            },
            Layers =
            {
                new ControlLayerConfig
                {
                    Id = layerId,
                    Name = "Preset A",
                    IsEnabled = true,
                    Priority = 10,
                    ScriptIds = { scriptId },
                },
            },
            Scripts =
            {
                new ControlScriptConfig
                {
                    Id = scriptId,
                    Name = "Layer Buttons",
                    ScriptPath = "control/layers.mnd",
                    Scope = ControlScriptScope.Layer,
                    LayerId = layerId,
                    FailurePolicy = new ControlScriptFailurePolicy
                    {
                        Mode = ControlScriptFailureMode.DisableScope,
                        MaxConsecutiveFailures = 2,
                    },
                    Triggers =
                    {
                        new ControlScriptTriggerConfig
                        {
                            Id = triggerId,
                            Kind = ControlScriptTriggerKind.MIDIControlChange,
                            FunctionName = "onCc",
                            DeviceInstanceId = deviceId,
                            MIDIChannel = 0,
                            MIDIController = 42,
                        },
                    },
                },
            },
        };

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "." + ControlSystemIO.FileExtension);
        try
        {
            await ControlSystemIO.SaveConfigAsync(config, tmp, "HaPlay.Tests");
            var document = await ControlSystemIO.LoadDocumentAsync(tmp);

            Assert.Equal(ControlSystemDocument.CurrentSchemaVersion, document.SchemaVersion);
            Assert.Equal("HaPlay.Tests", document.Generator);
            Assert.Equal("scontrol", ControlSystemIO.FileExtension);
            Assert.True(document.ControlSystem.IsArmed);
            Assert.Equal(listenerId, Assert.Single(document.ControlSystem.OSCListeners).Id);
            Assert.Equal("Lighting In", document.ControlSystem.OSCListeners[0].Name);
            Assert.Equal(10030, document.ControlSystem.OSCListeners[0].LocalPort);

            var device = Assert.Single(document.ControlSystem.Devices);
            Assert.Equal(deviceId, device.Id);
            Assert.Equal(ControlDeviceProtocol.MIDI, device.Protocol);
            Assert.Equal("X-TOUCH MINI", device.Binding.MIDIInputDeviceName);
            Assert.Equal(scriptId, Assert.Single(device.ScriptIds));

            var layer = Assert.Single(document.ControlSystem.Layers);
            Assert.Equal(layerId, layer.Id);
            Assert.Equal("Preset A", layer.Name);
            Assert.True(layer.IsEnabled);
            Assert.Equal(10, layer.Priority);

            var script = Assert.Single(document.ControlSystem.Scripts);
            Assert.Equal(scriptId, script.Id);
            Assert.Equal(ControlScriptScope.Layer, script.Scope);
            Assert.Equal(layerId, script.LayerId);
            Assert.Equal(ControlScriptFailureMode.DisableScope, script.FailurePolicy.Mode);
            var trigger = Assert.Single(script.Triggers);
            Assert.Equal(triggerId, trigger.Id);
            Assert.Equal(ControlScriptTriggerKind.MIDIControlChange, trigger.Kind);
            Assert.Equal(42, trigger.MIDIController);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    [Fact]
    public void Deserialize_WhenSchemaIsTooNew_ThrowsVersionException()
    {
        var json = """{"schemaVersion":999,"controlSystem":{}}""";

        var ex = Assert.Throws<UnsupportedControlSystemSchemaVersionException>(() =>
            ControlSystemIO.Deserialize(json));

        Assert.Equal(999, ex.FileVersion);
        Assert.Equal(ControlSystemDocument.CurrentSchemaVersion, ex.SupportedVersion);
    }

    [Fact]
    public void Serialize_RoundTripsGenericMIDITriggerFilters()
    {
        var document = new ControlSystemDocument
        {
            ControlSystem = new ControlSystemConfig
            {
                Scripts =
                {
                    new ControlScriptConfig
                    {
                        Name = "Program trigger",
                        ScriptPath = "control/program.mnd",
                        Triggers =
                        {
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.MIDIMessage,
                                FunctionName = "onProgram",
                                MIDIMessageType = ControlMIDIMessageType.ProgramChange,
                                MIDIChannel = 1,
                                MIDIValue = 5,
                                MIDIParameter = 12,
                            },
                        },
                    },
                },
            },
        };

        var loaded = ControlSystemIO.Deserialize(ControlSystemIO.Serialize(document));
        var trigger = Assert.Single(Assert.Single(loaded.ControlSystem.Scripts).Triggers);

        Assert.Equal(ControlScriptTriggerKind.MIDIMessage, trigger.Kind);
        Assert.Equal(ControlMIDIMessageType.ProgramChange, trigger.MIDIMessageType);
        Assert.Equal(1, trigger.MIDIChannel);
        Assert.Equal(5, trigger.MIDIValue);
        Assert.Equal(12, trigger.MIDIParameter);
    }
}
