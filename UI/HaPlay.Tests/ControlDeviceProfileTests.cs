using HaPlay.ControlGraph;
using HaPlay.Models;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlDeviceProfileTests
{
    [Fact]
    public void BuiltInRepository_ContainsXTouchMiniAndX32Profiles()
    {
        var repository = BuiltInControlDeviceProfileRepository.Instance;

        Assert.NotNull(repository.FindById("behringer.xtouch-mini.mc"));
        Assert.NotNull(repository.FindById("behringer.x32.osc"));
        Assert.Null(repository.FindById("missing.profile"));
    }

    [Fact]
    public void XTouchMiniProfile_UsesReferenceMcModeMapping()
    {
        var profile = BuiltInControlDeviceProfileRepository.CreateXTouchMiniProfile();

        Assert.Equal("behringer.xtouch-mini.mc", profile.Id);
        Assert.Equal(ControlDeviceProtocol.Midi, profile.Protocol);
        Assert.Contains(profile.Ports, p => p.Kind == ControlDevicePortKind.MidiInput);
        Assert.Contains(profile.Ports, p => p.Kind == ControlDevicePortKind.MidiOutput);
        Assert.Equal(35, profile.Controls.Count);

        var layerA = Assert.Single(profile.Controls, c => c.Id == "xtouch.layer.a");
        Assert.Equal(ControlProfileControlKind.LayerButton, layerA.Kind);
        Assert.Equal(84, layerA.MidiNote);

        var layerB = Assert.Single(profile.Controls, c => c.Id == "xtouch.layer.b");
        Assert.Equal(85, layerB.MidiNote);

        var encoder1 = Assert.Single(profile.Controls, c => c.Id == "xtouch.encoder.1");
        Assert.Equal(ControlProfileControlKind.Encoder, encoder1.Kind);
        Assert.Equal(16, encoder1.MidiController);
        Assert.Equal(ControlProfileValueMode.RelativeEncoder, encoder1.ValueMode);
        Assert.Equal(Enumerable.Range(1, 10), encoder1.IncrementValues);
        Assert.Equal(Enumerable.Range(65, 8), encoder1.DecrementValues);

        var encoder8 = Assert.Single(profile.Controls, c => c.Id == "xtouch.encoder.8");
        Assert.Equal(23, encoder8.MidiController);

        var encoderPush1 = Assert.Single(profile.Controls, c => c.Id == "xtouch.encoder.1.push");
        Assert.Equal(32, encoderPush1.MidiNote);

        var buttonNotes = profile.Controls
            .Where(c => c.Id.StartsWith("xtouch.button.", StringComparison.Ordinal))
            .OrderBy(c => int.Parse(c.Id["xtouch.button.".Length..]))
            .Select(c => c.MidiNote)
            .ToArray();
        Assert.Equal([89, 90, 40, 41, 42, 43, 44, 45, 87, 88, 91, 92, 86, 93, 94, 95], buttonNotes);

        var master = Assert.Single(profile.Controls, c => c.Id == "xtouch.master-fader");
        Assert.Equal(ControlProfileControlKind.Fader, master.Kind);
        Assert.Equal(ControlProfileValueMode.PitchWheel, master.ValueMode);

        Assert.Empty(ControlDeviceProfileValidator.Validate(profile));
    }

    [Fact]
    public void X32Profile_ContainsCoreCommandCatalogAndXRemoteTask()
    {
        var profile = BuiltInControlDeviceProfileRepository.CreateX32Profile();

        Assert.Equal("behringer.x32.osc", profile.Id);
        Assert.Equal(ControlDeviceProtocol.Osc, profile.Protocol);
        Assert.Contains(profile.Ports, p => p.Kind == ControlDevicePortKind.OscRemote);
        Assert.Contains(profile.Ports, p => p.Kind == ControlDevicePortKind.OscListener);

        var ch1Fader = Assert.Single(profile.Commands, c => c.Id == "x32.ch.01.fader");
        Assert.Equal("/ch/01/mix/fader", ch1Fader.Address);
        Assert.Equal(ControlCommandValueKind.NormalizedFloat, ch1Fader.ValueKind);
        Assert.Equal(0, ch1Fader.MinValue);
        Assert.Equal(1, ch1Fader.MaxValue);

        var ch1Mute = Assert.Single(profile.Commands, c => c.Id == "x32.ch.01.mute");
        Assert.Equal("/ch/01/mix/on", ch1Mute.Address);
        Assert.Equal(ControlCommandValueKind.BooleanInt, ch1Mute.ValueKind);

        var ch1Solo = Assert.Single(profile.Commands, c => c.Id == "x32.ch.01.solo");
        Assert.Equal("/-stat/solosw/01", ch1Solo.Address);
        Assert.Equal(ControlCommandAccess.ReadOnly, ch1Solo.Access);

        Assert.Contains(profile.Commands, c => c.Id == "x32.dca.8.fader" && c.Address == "/dca/8/fader");
        Assert.Contains(profile.Commands, c => c.Id == "x32.bus.16.mute" && c.Address == "/bus/16/mix/on");
        Assert.Contains(profile.Commands, c => c.Id == "x32.matrix.06.fader" && c.Address == "/mtx/06/mix/fader");
        Assert.Contains(profile.Commands, c => c.Id == "x32.main.st.fader" && c.Address == "/main/st/mix/fader");

        var task = Assert.Single(profile.Tasks);
        Assert.Equal("x32.xremote", task.Id);
        Assert.Equal(ControlDeviceTaskKind.PeriodicOscSend, task.Kind);
        Assert.Equal("/xremote", task.Address);
        Assert.Equal(8000, task.IntervalMs);

        Assert.Empty(ControlDeviceProfileValidator.Validate(profile));
    }

    [Fact]
    public void ProfileValidator_ReturnsIssuesForDuplicateIdsAndInvalidTasks()
    {
        var profile = new ControlDeviceProfile
        {
            Id = "test",
            DisplayName = "Test",
            Protocol = ControlDeviceProtocol.Osc,
            Commands =
            [
                new ControlCommandProfile { Id = "dup", Address = "/a" },
                new ControlCommandProfile { Id = "dup", Address = "/b" },
                new ControlCommandProfile { Id = "missing-address" },
            ],
            Tasks =
            [
                new ControlDeviceTaskProfile { Id = "bad-task", Address = "", IntervalMs = 0 },
            ],
        };

        var issues = ControlDeviceProfileValidator.Validate(profile);

        Assert.Contains(issues, issue => issue.Code == "duplicate-command");
        Assert.Contains(issues, issue => issue.Code == "missing-command-address");
        Assert.Contains(issues, issue => issue.Code == "invalid-task-interval");
        Assert.Contains(issues, issue => issue.Code == "missing-task-address");
    }
}
