using System.Text.Json;
using HaPlay.Models;

namespace HaPlay.ControlGraph;

public sealed record ControlDeviceProfile
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public ControlDeviceProtocol Protocol { get; init; }

    public string Version { get; init; } = "1.0";

    /// <summary>Suggested remote port for an OSC device using this profile (e.g. X32 = 10023, X-Air = 10024).</summary>
    public int? DefaultOscPort { get; init; }

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

    public bool IsDefaultEnabled { get; init; }

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

public sealed record ControlDeviceProfileLoadIssue(string Source, string Code, string Message);

public interface IControlDeviceProfileRepository
{
    IReadOnlyList<ControlDeviceProfile> Profiles { get; }

    ControlDeviceProfile? FindById(string profileId);
}

public sealed class DirectoryControlDeviceProfileRepository : IControlDeviceProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly IReadOnlyList<ControlDeviceProfile> _profiles;

    private DirectoryControlDeviceProfileRepository(
        IReadOnlyList<ControlDeviceProfile> profiles,
        IReadOnlyList<ControlDeviceProfileLoadIssue> loadIssues)
    {
        _profiles = profiles;
        LoadIssues = loadIssues;
    }

    public IReadOnlyList<ControlDeviceProfile> Profiles => _profiles;

    public IReadOnlyList<ControlDeviceProfileLoadIssue> LoadIssues { get; }

    public ControlDeviceProfile? FindById(string profileId) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public static DirectoryControlDeviceProfileRepository Load(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return new DirectoryControlDeviceProfileRepository([], []);

        var profiles = new List<ControlDeviceProfile>();
        var issues = new List<ControlDeviceProfileLoadIssue>();
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            LoadFile(file, profiles, issues);

        return new DirectoryControlDeviceProfileRepository(profiles, issues);
    }

    public static ControlDeviceProfile LoadProfileFile(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
            throw new ArgumentException("Profile file is required.", nameof(file));

        var profiles = new List<ControlDeviceProfile>();
        var issues = new List<ControlDeviceProfileLoadIssue>();
        LoadFile(file, profiles, issues);
        if (profiles.Count == 1)
            return profiles[0];

        var message = issues.Count == 0
            ? "Profile file did not contain a profile."
            : string.Join("; ", issues.Select(issue => $"{issue.Code}: {issue.Message}"));
        throw new InvalidOperationException($"Profile '{file}' is not valid: {message}");
    }

    public static string SaveProfile(string directoryPath, ControlDeviceProfile profile, bool overwrite = true)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Profile directory is required.", nameof(directoryPath));
        ArgumentNullException.ThrowIfNull(profile);

        var validationIssues = ControlDeviceProfileValidator.Validate(profile);
        if (validationIssues.Count > 0)
        {
            var message = string.Join("; ", validationIssues.Select(issue => $"{issue.Code}: {issue.Message}"));
            throw new InvalidOperationException($"Profile '{profile.Id}' is not valid: {message}");
        }

        Directory.CreateDirectory(directoryPath);
        var path = Path.Combine(directoryPath, CreateProfileFileName(profile.Id));
        if (!overwrite && File.Exists(path))
            throw new IOException($"Profile file already exists: {path}");

        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
        return path;
    }

    public static IReadOnlyList<string> ExportBuiltInProfiles(string directoryPath, bool overwrite = true) =>
        BuiltInControlDeviceProfileRepository.Instance.Profiles
            .Select(profile => SaveProfile(directoryPath, profile, overwrite))
            .ToArray();

    private static void LoadFile(
        string file,
        List<ControlDeviceProfile> profiles,
        List<ControlDeviceProfileLoadIssue> issues)
    {
        try
        {
            var json = File.ReadAllText(file);
            var profile = JsonSerializer.Deserialize<ControlDeviceProfile>(json, JsonOptions);
            if (profile is null)
            {
                issues.Add(new ControlDeviceProfileLoadIssue(file, "empty-profile", "Profile file did not contain a profile."));
                return;
            }

            var validationIssues = ControlDeviceProfileValidator.Validate(profile);
            if (validationIssues.Count > 0)
            {
                issues.AddRange(validationIssues.Select(issue =>
                    new ControlDeviceProfileLoadIssue(file, issue.Code, issue.Message)));
                return;
            }

            profiles.Add(profile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            issues.Add(new ControlDeviceProfileLoadIssue(file, "load-failed", ex.Message));
        }
    }

    private static string CreateProfileFileName(string profileId)
    {
        var safe = new string(profileId
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? char.ToLowerInvariant(c) : '-')
            .ToArray()).Trim('-', '.');
        if (string.IsNullOrWhiteSpace(safe))
            safe = "profile";

        return safe + ".json";
    }
}

public sealed class CompositeControlDeviceProfileRepository : IControlDeviceProfileRepository
{
    private readonly IReadOnlyList<ControlDeviceProfile> _profiles;

    public CompositeControlDeviceProfileRepository(params IControlDeviceProfileRepository[] repositories)
        : this((IEnumerable<IControlDeviceProfileRepository>)repositories)
    {
    }

    public CompositeControlDeviceProfileRepository(IEnumerable<IControlDeviceProfileRepository> repositories)
    {
        ArgumentNullException.ThrowIfNull(repositories);

        var merged = new Dictionary<string, ControlDeviceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
        {
            if (repository is null)
                continue;

            foreach (var profile in repository.Profiles)
            {
                if (!string.IsNullOrWhiteSpace(profile.Id))
                    merged[profile.Id] = profile;
            }
        }

        _profiles = merged.Values
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ControlDeviceProfile> Profiles => _profiles;

    public ControlDeviceProfile? FindById(string profileId) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public static CompositeControlDeviceProfileRepository ForProject(
        ControlSystemConfig config,
        IControlDeviceProfileRepository? appRepository = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new CompositeControlDeviceProfileRepository(
            BuiltInControlDeviceProfileRepository.Instance,
            appRepository ?? EmptyControlDeviceProfileRepository.Instance,
            new ProjectControlDeviceProfileRepository(config.DeviceProfileOverrides));
    }
}

public sealed class ProjectControlDeviceProfileRepository : IControlDeviceProfileRepository
{
    private readonly IReadOnlyList<ControlDeviceProfile> _profiles;

    public ProjectControlDeviceProfileRepository(IEnumerable<ControlDeviceProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .ToArray();
    }

    public IReadOnlyList<ControlDeviceProfile> Profiles => _profiles;

    public ControlDeviceProfile? FindById(string profileId) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
}

public sealed class EmptyControlDeviceProfileRepository : IControlDeviceProfileRepository
{
    public static EmptyControlDeviceProfileRepository Instance { get; } = new();

    private EmptyControlDeviceProfileRepository()
    {
    }

    public IReadOnlyList<ControlDeviceProfile> Profiles => [];

    public ControlDeviceProfile? FindById(string profileId) => null;
}

public sealed class BuiltInControlDeviceProfileRepository : IControlDeviceProfileRepository
{
    public static BuiltInControlDeviceProfileRepository Instance { get; } = new();

    private readonly IReadOnlyList<ControlDeviceProfile> _profiles;

    private BuiltInControlDeviceProfileRepository()
    {
        _profiles = [CreateXTouchMiniProfile(), CreateX32Profile(), CreateXAirProfile()];
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
            DefaultOscPort = X32Presets.DefaultPort,
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
                    IsDefaultEnabled = true,
                    Kind = ControlDeviceTaskKind.PeriodicOscSend,
                    Address = "/xremote",
                    IntervalMs = 8000,
                },
                new ControlDeviceTaskProfile
                {
                    Id = "x32.subscribe.ch01.fader",
                    DisplayName = "Subscribe Ch 01 fader (optional)",
                    Kind = ControlDeviceTaskKind.PeriodicOscSend,
                    Address = "/subscribe",
                    IntervalMs = 8000,
                    Arguments =
                    [
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.String, StringValue = X32Presets.ChannelFaderAddress(1) },
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.Int32, IntegerValue = 50 },
                    ],
                },
                new ControlDeviceTaskProfile
                {
                    Id = "x32.meters.bank6",
                    DisplayName = "Subscribe meter bank 6 (optional)",
                    Kind = ControlDeviceTaskKind.PeriodicOscSend,
                    Address = "/meters",
                    IntervalMs = 8000,
                    Arguments =
                    [
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.String, StringValue = "/meters/6" },
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.Int32, IntegerValue = 16 },
                        new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.Int32, IntegerValue = 1 },
                    ],
                },
            ],
        };
    }

    public static ControlDeviceProfile CreateXAirProfile()
    {
        var commands = new List<ControlCommandProfile>();

        for (var channel = 1; channel <= XAirPresets.ChannelCount; channel++)
        {
            commands.Add(NormalizedCommand($"xair.ch.{channel:00}.fader", $"Ch {channel:00} Fader", XAirPresets.ChannelFaderAddress(channel)));
            commands.Add(BooleanCommand($"xair.ch.{channel:00}.mute", $"Ch {channel:00} Mute", XAirPresets.ChannelMuteAddress(channel)));
            commands.Add(NormalizedCommand($"xair.ch.{channel:00}.pan", $"Ch {channel:00} Pan", XAirPresets.ChannelPanAddress(channel)));
            commands.Add(BooleanCommand($"xair.ch.{channel:00}.solo", $"Ch {channel:00} Solo", XAirPresets.ChannelSoloStatusAddress(channel), ControlCommandAccess.ReadOnly));
        }

        for (var bus = 1; bus <= XAirPresets.BusCount; bus++)
        {
            commands.Add(NormalizedCommand($"xair.bus.{bus}.fader", $"Bus {bus} Fader", XAirPresets.BusFaderAddress(bus)));
            commands.Add(BooleanCommand($"xair.bus.{bus}.mute", $"Bus {bus} Mute", XAirPresets.BusMuteAddress(bus)));
        }

        for (var dca = 1; dca <= XAirPresets.DcaCount; dca++)
        {
            commands.Add(NormalizedCommand($"xair.dca.{dca}.fader", $"DCA {dca} Fader", XAirPresets.DcaFaderAddress(dca)));
            commands.Add(BooleanCommand($"xair.dca.{dca}.mute", $"DCA {dca} Mute", XAirPresets.DcaMuteAddress(dca)));
        }

        commands.Add(NormalizedCommand("xair.lr.fader", "Main LR Fader", XAirPresets.MainLrFaderAddress()));
        commands.Add(BooleanCommand("xair.lr.mute", "Main LR Mute", XAirPresets.MainLrMuteAddress()));

        return new ControlDeviceProfile
        {
            Id = "behringer.xair.osc",
            DisplayName = "Behringer X-Air / Midas M-Air OSC",
            Protocol = ControlDeviceProtocol.Osc,
            Version = "1.0",
            DefaultOscPort = XAirPresets.DefaultPort,
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
                    Id = "xair.xremote",
                    DisplayName = "Maintain /xremote",
                    IsDefaultEnabled = true,
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
