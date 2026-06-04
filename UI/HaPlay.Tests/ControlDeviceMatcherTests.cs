using HaPlay.ControlGraph;
using HaPlay.Models;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlDeviceMatcherTests
{
    [Fact]
    public void MatchMidiInput_UsesRememberedIdBeforeAmbiguousName()
    {
        var device = MidiDevice(
            inputId: 2,
            inputName: "Shared MIDI",
            alias: "surface",
            name: "Surface");
        var ports = new[]
        {
            new ControlMidiPortInfo(1, "Shared MIDI"),
            new ControlMidiPortInfo(2, "Shared MIDI"),
        };

        var match = ControlDeviceMatcher.MatchMidiInput(device, ports);

        Assert.Equal(ControlDeviceMatchStatus.Matched, match.Status);
        Assert.Equal(ControlDeviceMatchKind.RememberedDeviceId, match.Kind);
        Assert.Equal(2, match.Port!.Id);
    }

    [Fact]
    public void MatchMidiInput_UsesUserAliasWhenPortNameMatchesAlias()
    {
        var device = MidiDevice(
            inputId: null,
            inputName: null,
            alias: "xtouch",
            name: "Control Surface");

        var match = ControlDeviceMatcher.MatchMidiInput(device, [new ControlMidiPortInfo(3, "xtouch")]);

        Assert.Equal(ControlDeviceMatchStatus.Matched, match.Status);
        Assert.Equal(ControlDeviceMatchKind.UserAlias, match.Kind);
        Assert.Equal(3, match.Port!.Id);
    }

    [Fact]
    public void MatchMidiOutput_FuzzyMatchesNormalizedName()
    {
        var device = MidiDevice(
            outputId: null,
            outputName: "XTouch Mini",
            alias: "surface",
            name: "Control Surface");

        var match = ControlDeviceMatcher.MatchMidiOutput(device, [new ControlMidiPortInfo(7, "X-Touch MINI")]);

        Assert.Equal(ControlDeviceMatchStatus.Matched, match.Status);
        Assert.Equal(ControlDeviceMatchKind.FuzzyName, match.Kind);
        Assert.Equal(7, match.Port!.Id);
    }

    [Fact]
    public void MatchMidiOutput_ReturnsAmbiguousForMultipleFuzzyMatches()
    {
        var device = MidiDevice(
            outputId: null,
            outputName: "XTouch Mini",
            alias: "surface",
            name: "Control Surface");
        var ports = new[]
        {
            new ControlMidiPortInfo(7, "X-Touch MINI"),
            new ControlMidiPortInfo(8, "X Touch Mini"),
        };

        var match = ControlDeviceMatcher.MatchMidiOutput(device, ports);

        Assert.Equal(ControlDeviceMatchStatus.Ambiguous, match.Status);
        Assert.Equal([7, 8], match.Candidates.Select(p => p.Id));
        Assert.Contains("ambiguous", match.Message);
    }

    [Fact]
    public void MatchMidiInput_ReturnsMissingWhenNoConfidentMatchExists()
    {
        var device = MidiDevice(
            inputId: 4,
            inputName: "XTouch Mini",
            alias: "xtouch",
            name: "X-Touch Mini");

        var match = ControlDeviceMatcher.MatchMidiInput(device, [new ControlMidiPortInfo(9, "Other Controller")]);

        Assert.Equal(ControlDeviceMatchStatus.Missing, match.Status);
        Assert.Null(match.Port);
        Assert.Contains("was not found", match.Message);
    }

    private static ControlDeviceInstanceConfig MidiDevice(
        int? inputId = null,
        string? inputName = null,
        int? outputId = null,
        string? outputName = null,
        string? alias = null,
        string name = "MIDI Device") =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Protocol = ControlDeviceProtocol.Midi,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                MidiInputDeviceId = inputId,
                MidiInputDeviceName = inputName,
                MidiOutputDeviceId = outputId,
                MidiOutputDeviceName = outputName,
            },
        };
}
