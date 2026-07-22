namespace S.Media.Core.Buses;

/// <summary>
/// Cover art as carried in metadata: the container's encoded bytes (PNG/JPEG) plus an optional
/// lazy decoder the host installs (the framework Core never references a codec). Consumers that
/// need pixels call <see cref="TryDecode"/>; consumers that just persist/forward keep the bytes.
/// </summary>
public sealed record CoverArt(ReadOnlyMemory<byte> EncodedBytes, string? MimeType)
{
    /// <summary>Host-installed decoder (e.g. the FFmpeg module's image decode) - null when no module
    /// provides one. Returns a caller-owned frame or null when the bytes don't decode.</summary>
    public Func<Video.VideoFrame?>? Decode { get; init; }

    public Video.VideoFrame? TryDecode() => Decode?.Invoke();
}

/// <summary>What is playing: track/title metadata published on item load for buses/visualizers.</summary>
public sealed record MediaItemMetadata(
    string? Title,
    string? Artist = null,
    string? Album = null,
    TimeSpan? Duration = null,
    string? SourceUri = null,
    CoverArt? Cover = null);

/// <summary>
/// Cheap per-frame video statistics (published by the frame-stats probe effect) so audio-visual
/// effects can match the picture - e.g. a visualizer tinting itself toward the program's dominant
/// color. Colors are 0xAARRGGBB.
/// </summary>
public readonly record struct FrameStatsMetadata(
    uint AverageArgb,
    uint DominantArgb,
    double AverageLuma,
    TimeSpan PresentationTime);

/// <summary>Receives metadata pushes. Callbacks arrive on arbitrary threads and must return promptly.</summary>
public interface IBusMetadataSink
{
    void OnItemMetadata(MediaItemMetadata metadata);

    void OnFrameStats(in FrameStatsMetadata stats);
}

/// <summary>
/// The session's metadata blackboard: publishers push (item loads, frame probes), sinks subscribe
/// (visualizers, future overlay effects), and late subscribers get the current item immediately.
/// Thread-safe; sink exceptions are swallowed per-sink so one bad consumer can't break the rest.
/// </summary>
public sealed class BusMetadataHub
{
    private readonly Lock _gate = new();
    private readonly List<IBusMetadataSink> _sinks = [];
    private MediaItemMetadata? _currentItem;

    /// <summary>The most recently published item (what's playing), for late joiners and HUDs.</summary>
    public MediaItemMetadata? CurrentItem
    {
        get { lock (_gate) return _currentItem; }
    }

    public void Attach(IBusMetadataSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        MediaItemMetadata? current;
        lock (_gate)
        {
            if (!_sinks.Contains(sink))
                _sinks.Add(sink);
            current = _currentItem;
        }

        if (current is not null)
            TryDeliver(sink, current);
    }

    public void Detach(IBusMetadataSink sink)
    {
        lock (_gate)
            _sinks.Remove(sink);
    }

    public void Publish(MediaItemMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        IBusMetadataSink[] sinks;
        lock (_gate)
        {
            _currentItem = metadata;
            sinks = _sinks.ToArray();
        }

        foreach (var sink in sinks)
            TryDeliver(sink, metadata);
    }

    public void Publish(in FrameStatsMetadata stats)
    {
        IBusMetadataSink[] sinks;
        lock (_gate)
            sinks = _sinks.ToArray();

        foreach (var sink in sinks)
        {
            try { sink.OnFrameStats(in stats); }
            catch { /* per-sink isolation */ }
        }
    }

    private static void TryDeliver(IBusMetadataSink sink, MediaItemMetadata metadata)
    {
        try { sink.OnItemMetadata(metadata); }
        catch { /* per-sink isolation */ }
    }
}
