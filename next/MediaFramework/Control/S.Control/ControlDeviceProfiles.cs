using System.Text.Json;

namespace S.Control;

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

    /// <summary>Optional runtime behavior metadata (protocol maintenance, meter decoding, etc.).</summary>
    public ControlDeviceProfileBehaviors? Behaviors { get; init; }

    /// <summary>
    /// Optional Mond helper script embedded in the profile: device-specific convenience functions (e.g. OSC
    /// address builders) exposed to control scripts under <see cref="ScriptModule"/>. Keeps device-specific logic
    /// out of the runtime — a device is described entirely by its profile (data + helpers). Helpers read the
    /// profile's own command data (e.g. <c>device.command(id).address</c>) instead of re-deriving address patterns.
    /// </summary>
    public string? HelperScript { get; init; }

    /// <summary>
    /// The global name the compiled <see cref="HelperScript"/> is exposed under to control scripts (e.g. "x32").
    /// Defaults to a name derived from <see cref="Id"/> when omitted.
    /// </summary>
    public string? ScriptModule { get; init; }
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

    public bool MidiHighResolution14Bit { get; init; }

    public int? MidiValueMin { get; init; }

    public int? MidiValueMax { get; init; }

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
    Absolute14Bit,
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
    ProtocolMaintenance,
    X32ProtocolMaintenance = ProtocolMaintenance,
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
    // External profile JSON uses the same source-generated, NativeAOT-safe contract as the rest of the
    // control system (camelCase, indented, numeric enums). ControlSystemJsonContext already serializes
    // ControlDeviceProfile and its whole graph, so reuse it instead of a reflection-based serializer.
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<ControlDeviceProfile> ProfileTypeInfo =
        ControlSystemJsonContext.Default.ControlDeviceProfile;

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

        File.WriteAllText(path, JsonSerializer.Serialize(profile, ProfileTypeInfo));
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
            var profile = JsonSerializer.Deserialize(json, ProfileTypeInfo);
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
        _profiles = BuiltInProfileLoader.Load()
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ControlDeviceProfile> Profiles => _profiles;

    public ControlDeviceProfile? FindById(string profileId) =>
        _profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
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

        foreach (var control in profile.Controls)
        {
            if (control.MidiValueMin.HasValue
                && control.MidiValueMax.HasValue
                && control.MidiValueMin.Value > control.MidiValueMax.Value)
            {
                issues.Add(new ControlDeviceProfileValidationIssue("invalid-control-range", $"Control '{control.Id}' has min above max."));
            }
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
