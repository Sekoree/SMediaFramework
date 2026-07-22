using S.Media.Core.Buses;
using S.Media.Core.Diagnostics;

namespace S.Media.Session;

/// <summary>
/// Publishes filename metadata immediately and refines it through at most one background probe. While a slow
/// probe runs, newer requests replace the single pending slot; stale results never overwrite the current item.
/// </summary>
internal sealed class ShowSessionMetadataPublisher : IDisposable
{
    private readonly Lock _gate = new();
    private readonly BusMetadataHub _hub;
    private readonly Func<string, MediaItemMetadata?>? _probe;
    private ProbeRequest? _pending;
    private long _generation;
    private bool _workerRunning;
    private bool _disposed;

    public ShowSessionMetadataPublisher(BusMetadataHub hub, Func<string, MediaItemMetadata?>? probe)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _probe = probe;
    }

    public void Publish(string mediaPath, string fallbackId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackId);

        var fallbackTitle = TryGetFileTitle(mediaPath) ?? fallbackId;
        var startWorker = false;
        lock (_gate)
        {
            if (_disposed)
                return;
            var generation = ++_generation;
            if (_probe is not null)
            {
                _pending = new ProbeRequest(generation, mediaPath, fallbackTitle);
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    startWorker = true;
                }
            }

            // Serialize fallback and rich publication with the generation check. Otherwise a worker could
            // validate A, then B could publish its fallback, then A could still publish stale rich metadata.
            _hub.Publish(new MediaItemMetadata(fallbackTitle, SourceUri: mediaPath));
        }
        if (startWorker)
        {
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(RunWorker);
        }
    }

    private void RunWorker()
    {
        while (true)
        {
            ProbeRequest request;
            lock (_gate)
            {
                if (_disposed || _pending is null)
                {
                    _workerRunning = false;
                    return;
                }

                request = _pending;
                _pending = null;
            }

            MediaItemMetadata? rich = null;
            try
            {
                rich = _probe!(request.MediaPath);
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning(
                    "ShowSession: metadata probe failed for '{0}' ({1})", request.MediaPath, ex.Message);
            }

            if (rich is null)
                continue;

            lock (_gate)
            {
                if (_disposed || request.Generation != _generation)
                    continue;

                _hub.Publish(rich with
                {
                    Title = rich.Title ?? request.FallbackTitle,
                    SourceUri = rich.SourceUri ?? request.MediaPath,
                });
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _generation++;
            _pending = null;
        }
    }

    private static string? TryGetFileTitle(string mediaPath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(mediaPath);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ProbeRequest(long Generation, string MediaPath, string FallbackTitle);
}
