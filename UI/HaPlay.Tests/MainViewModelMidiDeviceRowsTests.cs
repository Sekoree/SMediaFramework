using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class MainViewModelMidiDeviceRowsTests
{
    [Fact]
    public void BuildProjectMidiInputRows_ListsOnlyControlInputs()
    {
        var inputId = Guid.NewGuid();
        var outputId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Devices =
            [
                MidiDevice(inputId, inputDeviceId: 1, inputName: "X-Touch MINI"),
                MidiDevice(outputId, outputDeviceId: 7, outputName: "X-Touch MINI"),
            ],
        };

        var row = Assert.Single(MainViewModel.BuildProjectMidiInputRows(config));

        Assert.Equal(inputId, row.ControlDeviceId);
        Assert.Equal(1, row.DeviceId);
        Assert.Equal("X-Touch MINI", row.Name);
        Assert.Equal("Control input", row.UsageText);
    }

    [Fact]
    public void BuildProjectMidiOutputRows_MergesCueAndControlOutputs()
    {
        var controlId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            Devices =
            [
                MidiDevice(controlId, outputDeviceId: 7, outputName: "X-Touch MINI"),
            ],
        };

        var rows = MainViewModel.BuildProjectMidiOutputRows(
            config,
            [
                new MidiActionEndpoint
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
    public void BuildProjectMidiOutputRows_ShowsCueOnlyOutputs()
    {
        var endpointId = Guid.NewGuid();

        var row = Assert.Single(MainViewModel.BuildProjectMidiOutputRows(
            new ControlSystemConfig(),
            [
                new MidiActionEndpoint
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

    private static ControlDeviceInstanceConfig MidiDevice(
        Guid id,
        int? inputDeviceId = null,
        string? inputName = null,
        int? outputDeviceId = null,
        string? outputName = null) =>
        new()
        {
            Id = id,
            Name = inputName ?? outputName ?? "MIDI Device",
            Protocol = ControlDeviceProtocol.Midi,
            Binding = new ControlDeviceBindingConfig
            {
                MidiInputDeviceId = inputDeviceId,
                MidiInputDeviceName = inputName,
                MidiOutputDeviceId = outputDeviceId,
                MidiOutputDeviceName = outputName,
            },
        };
}
