using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlMIDIDeviceResolverTests
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
                    Protocol = ControlDeviceProtocol.MIDI,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig
                    {
                        Alias = "x-touch",
                        MIDIInputDeviceName = "X-Touch MINI",
                        MIDIOutputDeviceName = "X-Touch MINI",
                    },
                },
                new ControlDeviceInstanceConfig
                {
                    Id = matchedId,
                    Name = "Solo",
                    Protocol = ControlDeviceProtocol.MIDI,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig { MIDIInputDeviceName = "Solo Controller" },
                },
                // disabled MIDI device - skipped
                new ControlDeviceInstanceConfig
                {
                    Name = "Off",
                    Protocol = ControlDeviceProtocol.MIDI,
                    IsEnabled = false,
                    Binding = new ControlDeviceBindingConfig { MIDIInputDeviceName = "Off" },
                },
                // OSC device - skipped
                new ControlDeviceInstanceConfig { Name = "X32", Protocol = ControlDeviceProtocol.OSC, IsEnabled = true },
            ],
        };

        var inputs = new[]
        {
            new ControlMIDIPortInfo(1, "X-Touch MINI"),
            new ControlMIDIPortInfo(2, "X-Touch MINI"),
            new ControlMIDIPortInfo(3, "Solo Controller"),
        };
        var outputs = new[] { new ControlMIDIPortInfo(5, "Some Other Synth") };

        var requests = ControlMIDIDeviceResolver.BuildRequests(config, inputs, outputs);

        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(requests, r => r.DeviceInstanceId == matchedId);

        var inputRequest = Assert.Single(requests, r => r.Direction == ControlMIDIPortDirection.Input);
        Assert.Equal(ambiguousId, inputRequest.DeviceInstanceId);
        Assert.Equal(ControlDeviceMatchStatus.Ambiguous, inputRequest.Status);
        Assert.Equal(2, inputRequest.Candidates.Count);
        Assert.Equal(inputs.Length, inputRequest.AvailablePorts.Count);

        var outputRequest = Assert.Single(requests, r => r.Direction == ControlMIDIPortDirection.Output);
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
                    Protocol = ControlDeviceProtocol.MIDI,
                    Binding = new ControlDeviceBindingConfig { MIDIInputDeviceName = "stale" },
                },
            ],
        };

        var selections = new Dictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo>
        {
            [new ControlMIDIResolutionKey(deviceId, ControlMIDIPortDirection.Input)] = new(7, "Live In"),
            [new ControlMIDIResolutionKey(deviceId, ControlMIDIPortDirection.Output)] = new(8, "Live Out"),
        };

        var updated = ControlMIDIDeviceResolver.ApplySelections(config, selections);

        var device = Assert.Single(updated.Devices);
        Assert.Equal(7, device.Binding.MIDIInputDeviceId);
        Assert.Equal("Live In", device.Binding.MIDIInputDeviceName);
        Assert.Equal(8, device.Binding.MIDIOutputDeviceId);
        Assert.Equal("Live Out", device.Binding.MIDIOutputDeviceName);
    }

    [Fact]
    public void ApplySelections_WithNoSelections_ReturnsSameConfig()
    {
        var config = new ControlSystemConfig();
        var updated = ControlMIDIDeviceResolver.ApplySelections(
            config,
            new Dictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo>());

        Assert.Same(config, updated);
    }
}
