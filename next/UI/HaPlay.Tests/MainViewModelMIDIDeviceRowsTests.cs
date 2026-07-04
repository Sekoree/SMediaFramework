using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class MainViewModelMIDIDeviceRowsTests
{
    [Fact]
    public void BuildProjectMIDIInputRows_ListsOnlyControlInputs()
    {
        var inputId = Guid.NewGuid();
        var outputId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Devices =
            [
                MIDIDevice(inputId, inputDeviceId: 1, inputName: "X-Touch MINI"),
                MIDIDevice(outputId, outputDeviceId: 7, outputName: "X-Touch MINI"),
            ],
        };

        var row = Assert.Single(MainViewModel.BuildProjectMIDIInputRows(config));

        Assert.Equal(inputId, row.ControlDeviceId);
        Assert.Equal(1, row.DeviceId);
        Assert.Equal("X-Touch MINI", row.Name);
        Assert.Equal("Control input", row.UsageText);
    }

    [Fact]
    public void BuildProjectMIDIOutputRows_MergesCueAndControlOutputs()
    {
        var controlId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Devices =
            [
                MIDIDevice(controlId, outputDeviceId: 7, outputName: "X-Touch MINI"),
            ],
        };

        var rows = MainViewModel.BuildProjectMIDIOutputRows(
            config,
            [
                new MIDIActionEndpoint
                {
                    Id = endpointId,
                    Name = "X-Touch MINI",
                    DeviceId = 7,
                    DeviceName = "X-Touch MINI",
                },
            ]);

        var row = Assert.Single(rows);
        Assert.Equal(controlId, row.ControlDeviceId);
        Assert.Equal(endpointId, row.CueEndpointId);
        Assert.Equal(7, row.DeviceId);
        Assert.Equal("Cue + Control output", row.UsageText);
    }

    [Fact]
    public void BuildProjectMIDIOutputRows_ShowsCueOnlyOutputs()
    {
        var endpointId = Guid.NewGuid();

        var row = Assert.Single(MainViewModel.BuildProjectMIDIOutputRows(
            new ControlSystemConfig(),
            [
                new MIDIActionEndpoint
                {
                    Id = endpointId,
                    Name = "Lighting MIDI",
                    DeviceName = "Lighting MIDI",
                },
            ]));

        Assert.Null(row.ControlDeviceId);
        Assert.Equal(endpointId, row.CueEndpointId);
        Assert.Null(row.DeviceId);
        Assert.Equal("Cue output", row.UsageText);
    }

    private static ControlDeviceInstanceConfig MIDIDevice(
        Guid id,
        int? inputDeviceId = null,
        string? inputName = null,
        int? outputDeviceId = null,
        string? outputName = null) =>
        new()
        {
            Id = id,
            Name = inputName ?? outputName ?? "MIDI Device",
            Protocol = ControlDeviceProtocol.MIDI,
            Binding = new ControlDeviceBindingConfig
            {
                MIDIInputDeviceId = inputDeviceId,
                MIDIInputDeviceName = inputName,
                MIDIOutputDeviceId = outputDeviceId,
                MIDIOutputDeviceName = outputName,
            },
        };
}
