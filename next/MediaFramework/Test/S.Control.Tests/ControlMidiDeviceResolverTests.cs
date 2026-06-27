using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlMidiDeviceResolverTests
{
    [Fact]
    public void BuildRequests_EmitsAmbiguousAndMissingBindingsOnly()
    {
        var ambiguousId = Guid.NewGuid();
        var matchedId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = ambiguousId,
                    Name = "X-Touch MINI",
                    Protocol = ControlDeviceProtocol.Midi,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig
                    {
                        Alias = "x-touch",
                        MidiInputDeviceName = "X-Touch MINI",
                        MidiOutputDeviceName = "X-Touch MINI",
                    },
                },
                new ControlDeviceInstanceConfig
                {
                    Id = matchedId,
                    Name = "Solo",
                    Protocol = ControlDeviceProtocol.Midi,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig { MidiInputDeviceName = "Solo Controller" },
                },
                // disabled MIDI device — skipped
                new ControlDeviceInstanceConfig
                {
                    Name = "Off",
                    Protocol = ControlDeviceProtocol.Midi,
                    IsEnabled = false,
                    Binding = new ControlDeviceBindingConfig { MidiInputDeviceName = "Off" },
                },
                // OSC device — skipped
                new ControlDeviceInstanceConfig { Name = "X32", Protocol = ControlDeviceProtocol.Osc, IsEnabled = true },
            ],
        };

        var inputs = new[]
        {
            new ControlMidiPortInfo(1, "X-Touch MINI"),
            new ControlMidiPortInfo(2, "X-Touch MINI"),
            new ControlMidiPortInfo(3, "Solo Controller"),
        };
        var outputs = new[] { new ControlMidiPortInfo(5, "Some Other Synth") };

        var requests = ControlMidiDeviceResolver.BuildRequests(config, inputs, outputs);

        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(requests, r => r.DeviceInstanceId == matchedId);

        var inputRequest = Assert.Single(requests, r => r.Direction == ControlMidiPortDirection.Input);
        Assert.Equal(ambiguousId, inputRequest.DeviceInstanceId);
        Assert.Equal(ControlDeviceMatchStatus.Ambiguous, inputRequest.Status);
        Assert.Equal(2, inputRequest.Candidates.Count);
        Assert.Equal(inputs.Length, inputRequest.AvailablePorts.Count);

        var outputRequest = Assert.Single(requests, r => r.Direction == ControlMidiPortDirection.Output);
        Assert.Equal(ControlDeviceMatchStatus.Missing, outputRequest.Status);
        Assert.Equal(outputs.Length, outputRequest.AvailablePorts.Count);
    }

    [Fact]
    public void ApplySelections_WritesChosenPortsIntoBindings()
    {
        var deviceId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = deviceId,
                    Name = "X-Touch MINI",
                    Protocol = ControlDeviceProtocol.Midi,
                    Binding = new ControlDeviceBindingConfig { MidiInputDeviceName = "stale" },
                },
            ],
        };

        var selections = new Dictionary<ControlMidiResolutionKey, ControlMidiPortInfo>
        {
            [new ControlMidiResolutionKey(deviceId, ControlMidiPortDirection.Input)] = new(7, "Live In"),
            [new ControlMidiResolutionKey(deviceId, ControlMidiPortDirection.Output)] = new(8, "Live Out"),
        };

        var updated = ControlMidiDeviceResolver.ApplySelections(config, selections);

        var device = Assert.Single(updated.Devices);
        Assert.Equal(7, device.Binding.MidiInputDeviceId);
        Assert.Equal("Live In", device.Binding.MidiInputDeviceName);
        Assert.Equal(8, device.Binding.MidiOutputDeviceId);
        Assert.Equal("Live Out", device.Binding.MidiOutputDeviceName);
    }

    [Fact]
    public void ApplySelections_WithNoSelections_ReturnsSameConfig()
    {
        var config = new ControlSystemConfig();
        var updated = ControlMidiDeviceResolver.ApplySelections(
            config,
            new Dictionary<ControlMidiResolutionKey, ControlMidiPortInfo>());

        Assert.Same(config, updated);
    }
}
