using HaPlay.Models;

namespace HaPlay.ControlGraph;

public sealed record ControlDeviceProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public ControlDeviceProtocol Protocol { get; init; }

    public string Version { get; init; } = "1.0";

    public List<ControlDevicePortProfile> Ports { get; init; } = new();

    public List<ControlControlProfile> Controls { get; init; } = new();

    public List<ControlCommandProfile> Commands { get; init; } = new();

    public List<ControlLayerProfile> Layers { get; init; } = new();

    public List<ControlDeviceTaskProfile> Tasks { get; init; } = new();
}

public sealed record ControlDevicePortProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public ControlDevicePortKind Kind { get; init; }
}

public enum ControlDevicePortKind
{
    MidiInput,
    MidiOutput,
    OscRemote,
    OscListener,
}

public sealed record ControlControlProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public ControlProfileControlKind Kind { get; init; }

    public int? MidiChannel { get; init; }

    public int? MidiController { get; init; }

    public int? MidiNote { get; init; }

    public ControlProfileValueMode ValueMode { get; init; }

    public List<int> IncrementValues { get; init; } = new();

    public List<int> DecrementValues { get; init; } = new();
}

public enum ControlProfileControlKind
{
    Encoder,
    EncoderPush,
    Button,
    LayerButton,
    Fader,
}

public enum ControlProfileValueMode
{
    Absolute7Bit,
    RelativeEncoder,
    NoteMomentary,
    PitchWheel,
}

public sealed record ControlCommandProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public ControlCommandValueKind ValueKind { get; init; }

    public ControlCommandAccess Access { get; init; } = ControlCommandAccess.ReadWrite;

    public double? MinValue { get; init; }

    public double? MaxValue { get; init; }

    public string CacheKey { get; init; } = string.Empty;
}

public enum ControlCommandValueKind
{
    None,
    NormalizedFloat,
    BooleanInt,
    Text,
    MeterBlob,
}

public enum ControlCommandAccess
{
    ReadOnly,
    WriteOnly,
    ReadWrite,
}

public sealed record ControlLayerProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
}

public sealed record ControlDeviceTaskProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public ControlDeviceTaskKind Kind { get; init; }

    public string Address { get; init; } = string.Empty;

    public int IntervalMs { get; init; } = 8000;

    public List<ControlOscArgumentConfig> Arguments { get; init; } = new();
}

public enum ControlDeviceTaskKind
{
    PeriodicOscSend,
}

public sealed record ControlDeviceProfileValidationIssue(string Code, string Message);

public interface IControlDeviceProfileRepository
{
    IReadOnlyList<ControlDeviceProfile> Profiles { get; }

    ControlDeviceProfile? FindById(string profileId);
}

public sealed class BuiltInControlDeviceProfileRepository : IControlDeviceProfileRepository
{
    public static BuiltInControlDeviceProfileRepository Instance { get; } = new();

    private readonly IReadOnlyList<ControlDeviceProfile> _profiles;

    private BuiltInControlDeviceProfileRepository()
    {
        _profiles = [CreateXTouchMiniProfile(), CreateX32Profile()];
    }

    public IReadOnlyList<ControlDeviceProfile> Profiles => _profiles;

    public ControlDeviceProfile? FindById(string profileId) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public static ControlDeviceProfile CreateXTouchMiniProfile()
    {
        var controls = new List<ControlControlProfile>
        {
            new()
            {
                Id = "xtouch.layer.a",
                DisplayName = "Layer A",
                Kind = ControlProfileControlKind.LayerButton,
                MidiNote = 84,
                ValueMode = ControlProfileValueMode.NoteMomentary,
            },
            new()
            {
                Id = "xtouch.layer.b",
                DisplayName = "Layer B",
                Kind = ControlProfileControlKind.LayerButton,
                MidiNote = 85,
                ValueMode = ControlProfileValueMode.NoteMomentary,
            },
        };

        for (var i = 0; i < 8; i++)
        {
            controls.Add(new ControlControlProfile
            {
                Id = $"xtouch.encoder.{i + 1}",
                DisplayName = $"Encoder {i + 1}",
                Kind = ControlProfileControlKind.Encoder,
                MidiController = 16 + i,
                ValueMode = ControlProfileValueMode.RelativeEncoder,
                IncrementValues = Enumerable.Range(1, 10).ToList(),
                DecrementValues = Enumerable.Range(65, 8).ToList(),
            });
            controls.Add(new ControlControlProfile
            {
                Id = $"xtouch.encoder.{i + 1}.push",
                DisplayName = $"Encoder {i + 1} Push",
                Kind = ControlProfileControlKind.EncoderPush,
                MidiNote = 32 + i,
                ValueMode = ControlProfileValueMode.NoteMomentary,
            });
        }

        var buttonNotes = new[] { 89, 90, 40, 41, 42, 43, 44, 45, 87, 88, 91, 92, 86, 93, 94, 95 };
        for (var i = 0; i < buttonNotes.Length; i++)
        {
            controls.Add(new ControlControlProfile
            {
                Id = $"xtouch.button.{i + 1}",
                DisplayName = $"Button {i + 1}",
                Kind = ControlProfileControlKind.Button,
                MidiNote = buttonNotes[i],
                ValueMode = ControlProfileValueMode.NoteMomentary,
            });
        }

        controls.Add(new ControlControlProfile
        {
            Id = "xtouch.master-fader",
            DisplayName = "Master Fader",
            Kind = ControlProfileControlKind.Fader,
            ValueMode = ControlProfileValueMode.PitchWheel,
        });

        return new ControlDeviceProfile
        {
            Id = "behringer.xtouch-mini.mc",
            DisplayName = "Behringer X-Touch Mini (MC Mode)",
            Protocol = ControlDeviceProtocol.Midi,
            Version = "1.0",
            Ports =
            [
                new ControlDevicePortProfile
                {
                    Id = "midi-in",
                    DisplayName = "MIDI Input",
                    Kind = ControlDevicePortKind.MidiInput,
                },
                new ControlDevicePortProfile
                {
                    Id = "midi-out",
                    DisplayName = "MIDI Output",
                    Kind = ControlDevicePortKind.MidiOutput,
                },
            ],
            Controls = controls,
            Layers =
            [
                new ControlLayerProfile { Id = "layer.a", DisplayName = "Layer A" },
                new ControlLayerProfile { Id = "layer.b", DisplayName = "Layer B" },
            ],
        };
    }

    public static ControlDeviceProfile CreateX32Profile()
    {
        var commands = new List<ControlCommandProfile>();

        for (var channel = 1; channel <= 32; channel++)
        {
            commands.Add(NormalizedCommand($"x32.ch.{channel:00}.fader", $"Ch {channel:00} Fader", X32Presets.ChannelFaderAddress(channel)));
            commands.Add(BooleanCommand($"x32.ch.{channel:00}.mute", $"Ch {channel:00} Mute", X32Presets.ChannelMuteAddress(channel)));
            commands.Add(NormalizedCommand($"x32.ch.{channel:00}.pan", $"Ch {channel:00} Pan", X32Presets.ChannelPanAddress(channel)));
            commands.Add(BooleanCommand($"x32.ch.{channel:00}.solo", $"Ch {channel:00} Solo", X32Presets.ChannelSoloStatusAddress(channel), ControlCommandAccess.ReadOnly));
        }

        for (var dca = 1; dca <= 8; dca++)
        {
            commands.Add(NormalizedCommand($"x32.dca.{dca}.fader", $"DCA {dca} Fader", X32Presets.DcaFaderAddress(dca)));
            commands.Add(BooleanCommand($"x32.dca.{dca}.mute", $"DCA {dca} Mute", X32Presets.DcaMuteAddress(dca)));
        }

        for (var bus = 1; bus <= 16; bus++)
        {
            commands.Add(NormalizedCommand($"x32.bus.{bus:00}.fader", $"Bus {bus:00} Fader", X32Presets.BusFaderAddress(bus)));
            commands.Add(BooleanCommand($"x32.bus.{bus:00}.mute", $"Bus {bus:00} Mute", X32Presets.BusMuteAddress(bus)));
        }

        for (var matrix = 1; matrix <= 6; matrix++)
        {
            commands.Add(NormalizedCommand($"x32.matrix.{matrix:00}.fader", $"Matrix {matrix:00} Fader", X32Presets.MatrixFaderAddress(matrix)));
            commands.Add(BooleanCommand($"x32.matrix.{matrix:00}.mute", $"Matrix {matrix:00} Mute", X32Presets.MatrixMuteAddress(matrix)));
        }

        commands.Add(NormalizedCommand("x32.main.st.fader", "Main Stereo Fader", X32Presets.MainStereoFaderAddress()));
        commands.Add(BooleanCommand("x32.main.st.mute", "Main Stereo Mute", X32Presets.MainStereoMuteAddress()));

        return new ControlDeviceProfile
        {
            Id = "behringer.x32.osc",
            DisplayName = "Behringer X32 / Midas M32 OSC",
            Protocol = ControlDeviceProtocol.Osc,
            Version = "1.0",
            Ports =
            [
                new ControlDevicePortProfile
                {
                    Id = "osc-remote",
                    DisplayName = "OSC Remote",
                    Kind = ControlDevicePortKind.OscRemote,
                },
                new ControlDevicePortProfile
                {
                    Id = "osc-listener",
                    DisplayName = "OSC Listener",
                    Kind = ControlDevicePortKind.OscListener,
                },
            ],
            Commands = commands,
            Tasks =
            [
                new ControlDeviceTaskProfile
                {
                    Id = "x32.xremote",
                    DisplayName = "Maintain /xremote",
                    Kind = ControlDeviceTaskKind.PeriodicOscSend,
                    Address = "/xremote",
                    IntervalMs = 8000,
                },
            ],
        };
    }

    private static ControlCommandProfile NormalizedCommand(string id, string displayName, string address) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Address = address,
            ValueKind = ControlCommandValueKind.NormalizedFloat,
            MinValue = 0,
            MaxValue = 1,
            CacheKey = address,
        };

    private static ControlCommandProfile BooleanCommand(
        string id,
        string displayName,
        string address,
        ControlCommandAccess access = ControlCommandAccess.ReadWrite) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Address = address,
            ValueKind = ControlCommandValueKind.BooleanInt,
            Access = access,
            MinValue = 0,
            MaxValue = 1,
            CacheKey = address,
        };
}

public static class ControlDeviceProfileValidator
{
    public static IReadOnlyList<ControlDeviceProfileValidationIssue> Validate(ControlDeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = new List<ControlDeviceProfileValidationIssue>();

        if (string.IsNullOrWhiteSpace(profile.Id))
            issues.Add(new ControlDeviceProfileValidationIssue("missing-id", "Profile id is required."));
        if (string.IsNullOrWhiteSpace(profile.DisplayName))
            issues.Add(new ControlDeviceProfileValidationIssue("missing-display-name", "Profile display name is required."));

        AddDuplicateIssues(profile.Ports.Select(p => p.Id), "duplicate-port", "port", issues);
        AddDuplicateIssues(profile.Controls.Select(c => c.Id), "duplicate-control", "control", issues);
        AddDuplicateIssues(profile.Commands.Select(c => c.Id), "duplicate-command", "command", issues);
        AddDuplicateIssues(profile.Tasks.Select(t => t.Id), "duplicate-task", "task", issues);

        foreach (var command in profile.Commands)
        {
            if (string.IsNullOrWhiteSpace(command.Address))
                issues.Add(new ControlDeviceProfileValidationIssue("missing-command-address", $"Command '{command.Id}' has no OSC address."));
        }

        foreach (var task in profile.Tasks)
        {
            if (task.IntervalMs <= 0)
                issues.Add(new ControlDeviceProfileValidationIssue("invalid-task-interval", $"Task '{task.Id}' must have a positive interval."));
            if (string.IsNullOrWhiteSpace(task.Address))
                issues.Add(new ControlDeviceProfileValidationIssue("missing-task-address", $"Task '{task.Id}' has no OSC address."));
        }

        return issues;
    }

    private static void AddDuplicateIssues(
        IEnumerable<string> ids,
        string code,
        string label,
        List<ControlDeviceProfileValidationIssue> issues)
    {
        foreach (var duplicate in ids
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            issues.Add(new ControlDeviceProfileValidationIssue(code, $"Duplicate {label} id '{duplicate}'."));
        }
    }
}
