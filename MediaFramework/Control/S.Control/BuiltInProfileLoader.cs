using System.Reflection;
using System.Text.Json;

namespace S.Control;

internal static class BuiltInProfileLoader
{
    private const string EmbeddedPrefix = "S.Control.Profiles.";

    public static IReadOnlyList<ControlDeviceProfile> Load()
    {
        var profiles = new List<ControlDeviceProfile>();
        var issues = new List<ControlDeviceProfileLoadIssue>();

        var directory = ResolveProfilesDirectory();
        if (directory is not null)
        {
            var fromDisk = DirectoryControlDeviceProfileRepository.Load(directory);
            if (fromDisk.Profiles.Count > 0)
                return fromDisk.Profiles;
            issues.AddRange(fromDisk.LoadIssues);
        }

        foreach (var resourceName in typeof(BuiltInProfileLoader).Assembly
                     .GetManifestResourceNames()
                     .Where(name => name.StartsWith(EmbeddedPrefix, StringComparison.Ordinal)
                         && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            LoadEmbeddedResource(resourceName, profiles, issues);
        }

        if (profiles.Count == 0 && issues.Count > 0)
        {
            throw new InvalidOperationException(
                "Built-in control device profiles failed to load: "
                + string.Join("; ", issues.Select(issue => $"{issue.Source}: {issue.Code}")));
        }

        return profiles;
    }

    private static void LoadEmbeddedResource(
        string resourceName,
        List<ControlDeviceProfile> profiles,
        List<ControlDeviceProfileLoadIssue> issues)
    {
        try
        {
            using var stream = typeof(BuiltInProfileLoader).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                issues.Add(new ControlDeviceProfileLoadIssue(resourceName, "missing-resource", "Embedded profile stream was not found."));
                return;
            }

            var profile = JsonSerializer.Deserialize(stream, ControlSystemJsonContext.Default.ControlDeviceProfile);
            if (profile is null)
            {
                issues.Add(new ControlDeviceProfileLoadIssue(resourceName, "empty-profile", "Embedded profile did not contain a profile."));
                return;
            }

            var validationIssues = ControlDeviceProfileValidator.Validate(profile);
            if (validationIssues.Count > 0)
            {
                issues.AddRange(validationIssues.Select(issue =>
                    new ControlDeviceProfileLoadIssue(resourceName, issue.Code, issue.Message)));
                return;
            }

            profiles.Add(profile);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            issues.Add(new ControlDeviceProfileLoadIssue(resourceName, "load-failed", ex.Message));
        }
    }

    private static string? ResolveProfilesDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, "Profiles");
        if (Directory.Exists(candidate))
            return candidate;

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            return null;

        candidate = Path.GetFullPath(Path.Combine(assemblyDirectory, "Profiles"));
        return Directory.Exists(candidate) ? candidate : null;
    }
}
