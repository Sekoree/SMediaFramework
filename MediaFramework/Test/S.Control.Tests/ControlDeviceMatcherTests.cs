using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlDeviceMatcherTests
{
    [Fact]
    public void MatchMIDIInput_UsesRememberedIdBeforeAmbiguousName()
    {
        var device = MIDIDevice(
            inputId: 2,
            inputName: "Shared MIDI",
            alias: "surface",
            name: "Surface");
        var ports = new[]
        {
            new ControlMIDIPortInfo(1, "Shared MIDI"),
            new ControlMIDIPortInfo(2, "Shared MIDI"),
        };

        var match = ControlDeviceMatcher.MatchMIDIInput(device, ports);

        Assert.Equal(ControlDeviceMatchStatus.Matched, match.Status);
        Assert.Equal(ControlDeviceMatchKind.RememberedDeviceId, match.Kind);
        Assert.Equal(2, match.Port!.Id);
    }

    [Fact]
    public void MatchMIDIInput_UsesUserAliasWhenPortNameMatchesAlias()
    {
        var device = MIDIDevice(
            inputId: null,
            inputName: null,
            alias: "xtouch",
            name: "Control Surface");

        var match = ControlDeviceMatcher.MatchMIDIInput(device, [new ControlMIDIPortInfo(3, "xtouch")]);

        Assert.Equal(ControlDeviceMatchStatus.Matched, match.Status);
        Assert.Equal(ControlDeviceMatchKind.UserAlias, match.Kind);
        Assert.Equal(3, match.Port!.Id);
    }

    [Fact]
    public void MatchMIDIOutput_FuzzyMatchesNormalizedName()
    {
        var device = MIDIDevice(
            outputId: null,
            outputName: "XTouch Mini",
            alias: "surface",
            name: "Control Surface");

        var match = ControlDeviceMatcher.MatchMIDIOutput(device, [new ControlMIDIPortInfo(7, "X-Touch MINI")]);

        Assert.Equal(ControlDeviceMatchStatus.Matched, match.Status);
        Assert.Equal(ControlDeviceMatchKind.FuzzyName, match.Kind);
        Assert.Equal(7, match.Port!.Id);
    }

    [Fact]
    public void MatchMIDIOutput_ReturnsAmbiguousForMultipleFuzzyMatches()
    {
        var device = MIDIDevice(
            outputId: null,
            outputName: "XTouch Mini",
            alias: "surface",
            name: "Control Surface");
        var ports = new[]
        {
            new ControlMIDIPortInfo(7, "X-Touch MINI"),
            new ControlMIDIPortInfo(8, "X Touch Mini"),
        };

        var match = ControlDeviceMatcher.MatchMIDIOutput(device, ports);

        Assert.Equal(ControlDeviceMatchStatus.Ambiguous, match.Status);
        Assert.Equal([7, 8], match.Candidates.Select(p => p.Id));
        Assert.Contains("ambiguous", match.Message);
    }

    [Fact]
    public void MatchMIDIInput_ReturnsMissingWhenNoConfidentMatchExists()
    {
        var device = MIDIDevice(
            inputId: 4,
            inputName: "XTouch Mini",
            alias: "xtouch",
            name: "X-Touch Mini");

        var match = ControlDeviceMatcher.MatchMIDIInput(device, [new ControlMIDIPortInfo(9, "Other Controller")]);

        Assert.Equal(ControlDeviceMatchStatus.Missing, match.Status);
        Assert.Null(match.Port);
        Assert.Contains("was not found", match.Message);
    }

    private static ControlDeviceInstanceConfig MIDIDevice(
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
            Protocol = ControlDeviceProtocol.MIDI,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                MIDIInputDeviceId = inputId,
                MIDIInputDeviceName = inputName,
                MIDIOutputDeviceId = outputId,
                MIDIOutputDeviceName = outputName,
            },
        };
}
