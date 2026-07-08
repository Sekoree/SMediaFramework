using HaPlay.Models;

namespace HaPlay.Services;

/// <summary>
/// Serializes every write to a HaPlay project file. Each request receives a monotonically increasing
/// generation; an older request that has not started writing when a newer request arrives is discarded.
/// This gives manual Save, background auto-save, and the final shutdown flush one ordering domain.
/// </summary>
public sealed class ProjectPersistenceCoordinator
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<HaPlayProject, string, CancellationToken, Task> _writeProject;
    private long _nextGeneration;
    private long _latestRequestedGeneration;
    private string? _lastSuccessfulPath;
    private string? _lastSuccessfulHash;

    public ProjectPersistenceCoordinator(
        Func<HaPlayProject, string, CancellationToken, Task>? writeProject = null)
    {
        _writeProject = writeProject ?? ProjectIO.SaveAsync;
    }

    public async Task<ProjectPersistenceResult> PersistAsync(
        HaPlayProject snapshot,
        string path,
        Func<HaPlayProject, string, CancellationToken, Task<ProjectPublishResult>>? publish = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var generation = Interlocked.Increment(ref _nextGeneration);
        Interlocked.Exchange(ref _latestRequestedGeneration, generation);
        var hash = ProjectHash.Of(snapshot);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (generation < Volatile.Read(ref _latestRequestedGeneration))
                return ProjectPersistenceResult.Superseded(generation, hash);

            await _writeProject(snapshot, path, cancellationToken).ConfigureAwait(false);

            var published = publish is null
                ? ProjectPublishResult.Empty
                : await publish(snapshot, path, cancellationToken).ConfigureAwait(false);
            // Auto-save's contract includes publishable sidecars. Keep retrying the generation when publication
            // was incomplete even though the .haplayproj itself reached disk successfully.
            if (published.Errors.Count == 0)
            {
                Volatile.Write(ref _lastSuccessfulPath, Path.GetFullPath(path));
                Volatile.Write(ref _lastSuccessfulHash, hash);
            }
            return ProjectPersistenceResult.Success(generation, hash, published);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProjectPersistenceResult.Failure(generation, hash, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether this exact project content is known to have reached this exact file.</summary>
    public bool IsPersisted(string? path, string hash)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var successfulPath = Volatile.Read(ref _lastSuccessfulPath);
        var successfulHash = Volatile.Read(ref _lastSuccessfulHash);
        return string.Equals(successfulPath, Path.GetFullPath(path), PathComparison)
               && string.Equals(successfulHash, hash, StringComparison.Ordinal);
    }

    /// <summary>Waits until the currently-running persistence request leaves the coordinator.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _gate.Release();
    }
}

public sealed record ProjectPublishResult(IReadOnlyList<string> SidecarPaths, IReadOnlyList<string> Errors)
{
    public static ProjectPublishResult Empty { get; } = new([], []);
}

public sealed record ProjectPersistenceResult(
    bool Succeeded,
    bool WasSuperseded,
    long Generation,
    string ContentHash,
    Exception? Error,
    ProjectPublishResult Published)
{
    public static ProjectPersistenceResult Success(long generation, string hash, ProjectPublishResult published) =>
        new(true, false, generation, hash, null, published);

    public static ProjectPersistenceResult Failure(long generation, string hash, Exception error) =>
        new(false, false, generation, hash, error, ProjectPublishResult.Empty);

    public static ProjectPersistenceResult Superseded(long generation, string hash) =>
        new(false, true, generation, hash, null, ProjectPublishResult.Empty);
}
