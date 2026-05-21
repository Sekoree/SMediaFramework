using System.Text.Json;

namespace HaPlay.Models;

/// <summary>
/// Serialization helpers for <see cref="HaPlayProject"/>. Pure I/O — no UI dependency so unit tests
/// can round-trip projects without spinning up Avalonia. Save / Load command plumbing on
/// <c>MainViewModel</c> is built on top of these.
/// </summary>
public static class ProjectIO
{
    /// <summary>Default project file extension (§7.3 — Save Project dialog uses this).</summary>
    public const string FileExtension = "haplayproj";

    /// <summary>Reads a project from <paramref name="path"/>. Throws on missing file or malformed JSON.</summary>
    /// <remarks>Schema-version mismatches are surfaced as <see cref="UnsupportedSchemaVersionException"/>
    /// (§7.4 acceptance) so callers can show an informative banner instead of silently coercing state.</remarks>
    public static async Task<HaPlayProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var project = await JsonSerializer
            .DeserializeAsync(stream, HaPlayProjectJsonContext.Default.HaPlayProject, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
            throw new InvalidDataException($"Project file '{path}' contains no JSON object.");
        if (project.SchemaVersion is < 1 or > HaPlayProject.CurrentSchemaVersion)
            throw new UnsupportedSchemaVersionException(project.SchemaVersion, HaPlayProject.CurrentSchemaVersion);
        return project;
    }

    /// <summary>Writes <paramref name="project"/> to <paramref name="path"/>, replacing any existing file.</summary>
    public static async Task SaveAsync(HaPlayProject project, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer
            .SerializeAsync(stream, project, HaPlayProjectJsonContext.Default.HaPlayProject, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Round-trip a project via its JSON representation. Useful for tests and the Phase B "rebind" flow
    /// (load → mutate → re-save) without going through disk.</summary>
    public static string Serialize(HaPlayProject project) =>
        JsonSerializer.Serialize(project, HaPlayProjectJsonContext.Default.HaPlayProject);

    /// <summary>Companion to <see cref="Serialize"/>.</summary>
    public static HaPlayProject Deserialize(string json)
    {
        var p = JsonSerializer.Deserialize(json, HaPlayProjectJsonContext.Default.HaPlayProject);
        if (p is null)
            throw new InvalidDataException("Project JSON is empty.");
        return p;
    }
}

public static class PlaylistIO
{
    public const string FileExtension = "haplayplaylist";

    public static async Task<PlaylistConfig> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".m3u", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var paths = await ParseM3uAsync(path, cancellationToken).ConfigureAwait(false);
            return new PlaylistConfig
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Items = paths.ConvertAll(p => (PlaylistItem)new FilePlaylistItem(p)),
            };
        }

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer
            .DeserializeAsync(stream, MediaPlayerConfigJsonContext.Default.PlaylistConfig, cancellationToken)
            .ConfigureAwait(false);
        if (config is null)
            throw new InvalidDataException($"Playlist file '{path}' contains no JSON object.");
        return config;
    }

    public static async Task SaveAsync(PlaylistConfig config, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer
            .SerializeAsync(stream, config, MediaPlayerConfigJsonContext.Default.PlaylistConfig, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<List<string>> ParseM3uAsync(string path, CancellationToken cancellationToken)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        var result = new List<string>();
        foreach (var raw in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            result.Add(Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(baseDir, line)));
        }
        return result;
    }
}

public static class CueListIO
{
    public const string FileExtension = "haplaycues";

    public static async Task<CueList> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer
            .DeserializeAsync(stream, CueListJsonContext.Default.CueList, cancellationToken)
            .ConfigureAwait(false);
        if (config is null)
            throw new InvalidDataException($"Cue list file '{path}' contains no JSON object.");
        return config;
    }

    public static async Task SaveAsync(CueList config, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer
            .SerializeAsync(stream, config, CueListJsonContext.Default.CueList, cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class UnsupportedSchemaVersionException : Exception
{
    public UnsupportedSchemaVersionException(int fileVersion, int supportedVersion)
        : base($"Project schema version {fileVersion} is not supported by this build (max supported: {supportedVersion}).")
    {
        FileVersion = fileVersion;
        SupportedVersion = supportedVersion;
    }

    public int FileVersion { get; }
    public int SupportedVersion { get; }
}
