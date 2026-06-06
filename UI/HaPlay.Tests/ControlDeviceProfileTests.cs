using System.Text.Json;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlDeviceProfileTests
{
    [Fact]
    public void BuiltInRepository_ContainsXTouchMiniX32AndXAirProfiles()
    {
        var repository = BuiltInControlDeviceProfileRepository.Instance;

        Assert.NotNull(repository.FindById("behringer.xtouch-mini.mc"));
        Assert.NotNull(repository.FindById("behringer.bcf2000"));
        Assert.NotNull(repository.FindById("behringer.x32.osc"));
        Assert.NotNull(repository.FindById("behringer.xair.osc"));
        Assert.Null(repository.FindById("missing.profile"));
    }

    [Fact]
    public void XAirProfile_UsesXAirAddressingAndDefaultPort()
    {
        var profile = BuiltInControlDeviceProfileRepository.CreateXAirProfile();

        Assert.Equal("behringer.xair.osc", profile.Id);
        Assert.Equal(ControlDeviceProtocol.Osc, profile.Protocol);
        Assert.Equal(10024, profile.DefaultOscPort);
        Assert.Contains(profile.Ports, p => p.Kind == ControlDevicePortKind.OscRemote);

        // Channels share the X32 /ch/NN/mix layout (16 channels for XR16/XR18).
        var ch1Fader = Assert.Single(profile.Commands, c => c.Id == "xair.ch.01.fader");
        Assert.Equal("/ch/01/mix/fader", ch1Fader.Address);
        Assert.Equal(ControlCommandValueKind.NormalizedFloat, ch1Fader.ValueKind);
        Assert.Contains(profile.Commands, c => c.Id == "xair.ch.16.mute" && c.Address == "/ch/16/mix/on");
        Assert.DoesNotContain(profile.Commands, c => c.Id == "xair.ch.17.fader");

        // Buses and DCAs are single-digit; the main is /lr.
        Assert.Contains(profile.Commands, c => c.Id == "xair.bus.1.fader" && c.Address == "/bus/1/mix/fader");
        Assert.Contains(profile.Commands, c => c.Id == "xair.bus.6.mute" && c.Address == "/bus/6/mix/on");
        Assert.Contains(profile.Commands, c => c.Id == "xair.dca.1.fader" && c.Address == "/dca/1/fader");
        Assert.Contains(profile.Commands, c => c.Id == "xair.dca.4.mute" && c.Address == "/dca/4/on");
        Assert.Contains(profile.Commands, c => c.Id == "xair.lr.fader" && c.Address == "/lr/mix/fader");
        Assert.Contains(profile.Commands, c => c.Id == "xair.lr.mute" && c.Address == "/lr/mix/on");

        var xremote = Assert.Single(profile.Tasks, t => t.Id == "xair.xremote");
        Assert.True(xremote.IsDefaultEnabled);
        Assert.Equal("/xremote", xremote.Address);
        Assert.Equal(8000, xremote.IntervalMs);

        Assert.Empty(ControlDeviceProfileValidator.Validate(profile));
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
    public void Bcf2000Profile_Uses14BitFadersEncodersAndButtonBanks()
    {
        var profile = BuiltInControlDeviceProfileRepository.CreateBcf2000Profile();

        Assert.Equal("behringer.bcf2000", profile.Id);
        Assert.Equal(ControlDeviceProtocol.Midi, profile.Protocol);
        Assert.Contains(profile.Ports, p => p.Kind == ControlDevicePortKind.MidiInput);
        Assert.Contains(profile.Ports, p => p.Kind == ControlDevicePortKind.MidiOutput);

        // Motor faders: 14-bit absolute CC 0..7 across the full 14-bit range.
        var fader1 = Assert.Single(profile.Controls, c => c.Id == "bcf.fader.1");
        Assert.Equal(ControlProfileControlKind.Fader, fader1.Kind);
        Assert.Equal(0, fader1.MidiController);
        Assert.Equal(ControlProfileValueMode.Absolute14Bit, fader1.ValueMode);
        Assert.True(fader1.MidiHighResolution14Bit);
        Assert.Equal(0, fader1.MidiValueMin);
        Assert.Equal(16383, fader1.MidiValueMax);
        Assert.Equal(7, Assert.Single(profile.Controls, c => c.Id == "bcf.fader.8").MidiController);

        // Rotary encoders: 14-bit absolute CC 10..17.
        var enc1 = Assert.Single(profile.Controls, c => c.Id == "bcf.encoder.1");
        Assert.Equal(ControlProfileControlKind.Encoder, enc1.Kind);
        Assert.Equal(10, enc1.MidiController);
        Assert.True(enc1.MidiHighResolution14Bit);
        Assert.Equal(16383, enc1.MidiValueMax);
        Assert.Equal(17, Assert.Single(profile.Controls, c => c.Id == "bcf.encoder.8").MidiController);

        // Encoder press: notes 0..7.
        Assert.Equal(0, Assert.Single(profile.Controls, c => c.Id == "bcf.encoder.1.push").MidiNote);
        Assert.Equal(7, Assert.Single(profile.Controls, c => c.Id == "bcf.encoder.8.push").MidiNote);

        // Button banks: Row 1 notes 10..17, Row 2 20..27, Group 6 up to 63.
        Assert.Equal(10, Assert.Single(profile.Controls, c => c.Id == "bcf.button.row1.1").MidiNote);
        Assert.Equal(17, Assert.Single(profile.Controls, c => c.Id == "bcf.button.row1.8").MidiNote);
        Assert.Equal(20, Assert.Single(profile.Controls, c => c.Id == "bcf.button.row2.1").MidiNote);
        Assert.Equal(63, Assert.Single(profile.Controls, c => c.Id == "bcf.button.group6.4").MidiNote);

        // 8 faders + 8 encoders + 8 pushes + (8+8+4+4+2+4) buttons = 54.
        Assert.Equal(54, profile.Controls.Count);
        Assert.Equal(5, profile.Layers.Count);

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

        Assert.Equal(3, profile.Tasks.Count);
        var xremote = Assert.Single(profile.Tasks, t => t.Id == "x32.xremote");
        Assert.True(xremote.IsDefaultEnabled);
        Assert.Equal(ControlDeviceTaskKind.PeriodicOscSend, xremote.Kind);
        Assert.Equal("/xremote", xremote.Address);
        Assert.Equal(8000, xremote.IntervalMs);

        var subscribe = Assert.Single(profile.Tasks, t => t.Id == "x32.subscribe.ch01.fader");
        Assert.False(subscribe.IsDefaultEnabled);
        Assert.Equal("/subscribe", subscribe.Address);
        Assert.Equal("/ch/01/mix/fader", subscribe.Arguments[0].StringValue);
        Assert.Equal(50, subscribe.Arguments[1].IntegerValue);

        var meters = Assert.Single(profile.Tasks, t => t.Id == "x32.meters.bank6");
        Assert.False(meters.IsDefaultEnabled);
        Assert.Equal("/meters", meters.Address);
        Assert.Equal("/meters/6", meters.Arguments[0].StringValue);
        Assert.Equal(16, meters.Arguments[1].IntegerValue);
        Assert.Equal(1, meters.Arguments[2].IntegerValue);

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

    [Fact]
    public async Task DirectoryRepository_LoadsValidUserProfilesAndReportsInvalidFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "haplay-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var valid = new ControlDeviceProfile
        {
            Id = "user.generic-midi",
            DisplayName = "User Generic MIDI",
            Protocol = ControlDeviceProtocol.Midi,
            Ports =
            [
                new ControlDevicePortProfile
                {
                    Id = "midi-in",
                    DisplayName = "MIDI In",
                    Kind = ControlDevicePortKind.MidiInput,
                },
            ],
        };

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "valid.json"),
                JsonSerializer.Serialize(valid, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await File.WriteAllTextAsync(
                Path.Combine(root, "invalid.json"),
                JsonSerializer.Serialize(valid with { Id = string.Empty }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await File.WriteAllTextAsync(Path.Combine(root, "broken.json"), "{ not json");

            var repository = DirectoryControlDeviceProfileRepository.Load(root);

            var loaded = Assert.Single(repository.Profiles);
            Assert.Equal("user.generic-midi", loaded.Id);
            Assert.Equal("User Generic MIDI", repository.FindById("USER.GENERIC-MIDI")?.DisplayName);
            Assert.Contains(repository.LoadIssues, issue => issue.Source.EndsWith("invalid.json") && issue.Code == "missing-id");
            Assert.Contains(repository.LoadIssues, issue => issue.Source.EndsWith("broken.json") && issue.Code == "load-failed");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectoryRepository_SavesShareableProfileJsonAndReloadsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "haplay-profile-save-" + Guid.NewGuid().ToString("N"));
        var profile = new ControlDeviceProfile
        {
            Id = "learned.xtouch.custom",
            DisplayName = "Learned X-Touch Custom",
            Protocol = ControlDeviceProtocol.Midi,
            Controls =
            [
                new ControlControlProfile
                {
                    Id = "learned.button.1",
                    DisplayName = "Learned Button 1",
                    Kind = ControlProfileControlKind.Fader,
                    MidiChannel = 1,
                    MidiController = 16,
                    MidiHighResolution14Bit = true,
                    MidiValueMin = 0,
                    MidiValueMax = 10000,
                    ValueMode = ControlProfileValueMode.Absolute14Bit,
                },
            ],
        };

        try
        {
            var path = DirectoryControlDeviceProfileRepository.SaveProfile(root, profile);
            var json = File.ReadAllText(path);
            var repository = DirectoryControlDeviceProfileRepository.Load(root);

            Assert.Equal("learned.xtouch.custom.json", Path.GetFileName(path));
            Assert.Contains("\"id\": \"learned.xtouch.custom\"", json);
            Assert.Empty(repository.LoadIssues);
            var loaded = Assert.Single(repository.Profiles);
            Assert.Equal("Learned X-Touch Custom", loaded.DisplayName);
            var control = Assert.Single(loaded.Controls);
            Assert.Equal(ControlProfileValueMode.Absolute14Bit, control.ValueMode);
            Assert.True(control.MidiHighResolution14Bit);
            Assert.Equal(16, control.MidiController);
            Assert.Equal(0, control.MidiValueMin);
            Assert.Equal(10000, control.MidiValueMax);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectoryRepository_ExportsBuiltInProfilesAsExternalJsonFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "haplay-profile-export-" + Guid.NewGuid().ToString("N"));

        try
        {
            var paths = DirectoryControlDeviceProfileRepository.ExportBuiltInProfiles(root);
            var repository = DirectoryControlDeviceProfileRepository.Load(root);

            Assert.Contains(paths, path => Path.GetFileName(path) == "behringer.xtouch-mini.mc.json");
            Assert.Contains(paths, path => Path.GetFileName(path) == "behringer.x32.osc.json");
            Assert.NotNull(repository.FindById("behringer.xtouch-mini.mc"));
            Assert.NotNull(repository.FindById("behringer.x32.osc"));
            Assert.Empty(repository.LoadIssues);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompositeRepository_ProjectProfilesOverrideAppAndBuiltIns()
    {
        var appProfile = new ControlDeviceProfile
        {
            Id = "behringer.x32.osc",
            DisplayName = "App X32 Override",
            Protocol = ControlDeviceProtocol.Osc,
        };
        var projectProfile = appProfile with { DisplayName = "Project X32 Override" };
        var appRepository = new ProjectControlDeviceProfileRepository([appProfile]);
        var repository = CompositeControlDeviceProfileRepository.ForProject(
            new ControlSystemConfig { DeviceProfileOverrides = [projectProfile] },
            appRepository);

        var x32 = repository.FindById("behringer.x32.osc");

        Assert.NotNull(x32);
        Assert.Equal("Project X32 Override", x32.DisplayName);
        Assert.NotNull(repository.FindById("behringer.xtouch-mini.mc"));
    }
}
