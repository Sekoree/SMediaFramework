namespace S.Media.Core.Audio;

/// <summary>
/// Fluent helper for the most common <see cref="AudioRouter"/> wiring pattern: add a source, add a
/// sink, route them. Returns the builder from every method so a chain can collapse the boilerplate
/// of <see cref="AudioRouter.AddSource"/> → <see cref="AudioRouter.AddSink"/> →
/// <see cref="AudioRouter.AddRoute"/> into one statement.
/// </summary>
/// <remarks>
/// <para>
/// This is a thin convenience — every operation forwards immediately to the underlying router (no
/// deferred apply). The builder remembers the last added source / sink id so the most common case
/// ("connect the thing I just added to the other thing I just added") collapses to
/// <see cref="ConnectLast"/>.
/// </para>
/// <example>
/// <code>
/// new AudioGraphBuilder(router)
///     .AddSource(decoder, "music", autoResample: true)
///     .AddSink(speakers, "main")
///     .Connect("music", "main", gain: 0.8f);
/// </code>
/// </example>
/// <example>
/// <code>
/// // Connect-last shorthand for the soundboard one-clip-one-output case:
/// new AudioGraphBuilder(router)
///     .AddSource(clipDecoder, autoResample: true)
///     .AddSink(deviceOutput)
///     .ConnectLast();
/// </code>
/// </example>
/// </remarks>
public sealed class AudioGraphBuilder
{
    private readonly AudioRouter _router;
    private string? _lastSourceId;
    private string? _lastSinkId;

    public AudioGraphBuilder(AudioRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
    }

    /// <summary>The router this builder writes to.</summary>
    public AudioRouter Router => _router;

    /// <summary>Id of the most recently added source (via this builder). <c>null</c> until <see cref="AddSource"/> runs.</summary>
    public string? LastSourceId => _lastSourceId;

    /// <summary>Id of the most recently added sink (via this builder). <c>null</c> until <see cref="AddSink"/> runs.</summary>
    public string? LastSinkId => _lastSinkId;

    /// <summary>
    /// Adds a source to the router. <paramref name="autoResample"/> forwards to
    /// <see cref="AudioRouter.AddSource(IAudioSource, string?, bool)"/> so mixed-rate clips work
    /// without manual <c>ResamplingAudioSource</c> wiring.
    /// </summary>
    public AudioGraphBuilder AddSource(IAudioSource source, string? id = null, bool autoResample = false)
    {
        _lastSourceId = _router.AddSource(source, id, autoResample);
        return this;
    }

    /// <summary>Adds a sink to the router with the standard pump capacity (or an override).</summary>
    public AudioGraphBuilder AddSink(IAudioSink sink, string? id = null, int? pumpCapacityChunks = null)
    {
        _lastSinkId = _router.AddSink(sink, id, pumpCapacityChunks);
        return this;
    }

    /// <summary>
    /// Routes <paramref name="sourceId"/> to <paramref name="sinkId"/>. When <paramref name="map"/>
    /// is <c>null</c>, an <see cref="ChannelMap.Identity"/> sized to the sink's channel count is used.
    /// </summary>
    public AudioGraphBuilder Connect(string sourceId, string sinkId, ChannelMap? map = null, float gain = 1.0f)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        var effective = map ?? ResolveIdentityMap(sinkId);
        _router.AddRoute(sourceId, sinkId, effective, gain);
        return this;
    }

    /// <summary>
    /// Routes <see cref="LastSourceId"/> to <see cref="LastSinkId"/>. Use this when you just added
    /// one source and one sink and want them wired with an identity channel map.
    /// </summary>
    public AudioGraphBuilder ConnectLast(ChannelMap? map = null, float gain = 1.0f)
    {
        if (_lastSourceId is null)
            throw new InvalidOperationException("AudioGraphBuilder.ConnectLast: no source has been added yet — call AddSource first.");
        if (_lastSinkId is null)
            throw new InvalidOperationException("AudioGraphBuilder.ConnectLast: no sink has been added yet — call AddSink first.");
        return Connect(_lastSourceId, _lastSinkId, map, gain);
    }

    private ChannelMap ResolveIdentityMap(string sinkId)
    {
        if (!_router.TryGetSink(sinkId, out var sink) || sink is null)
            throw new ArgumentException($"unknown sink id '{sinkId}'", nameof(sinkId));
        return ChannelMap.Identity(sink.Format.Channels);
    }
}
