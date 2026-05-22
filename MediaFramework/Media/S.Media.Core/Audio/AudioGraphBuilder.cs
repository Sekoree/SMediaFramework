namespace S.Media.Core.Audio;

/// <summary>
/// Fluent helper for the most common <see cref="AudioRouter"/> wiring pattern: add a source, add a
/// output, route them. Returns the builder from every method so a chain can collapse the boilerplate
/// of <see cref="AudioRouter.AddSource"/> → <see cref="AudioRouter.AddOutput"/> →
/// <see cref="AudioRouter.AddRoute"/> into one statement.
/// </summary>
/// <remarks>
/// <para>
/// This is a thin convenience — every operation forwards immediately to the underlying router (no
/// deferred apply). The builder remembers the last added source / output id so the most common case
/// ("connect the thing I just added to the other thing I just added") collapses to
/// <see cref="ConnectLast"/>.
/// </para>
/// <example>
/// <code>
/// new AudioGraphBuilder(router)
///     .AddSource(decoder, "music", autoResample: true)
///     .AddOutput(speakers, "main")
///     .Connect("music", "main", gain: 0.8f);
/// </code>
/// </example>
/// <example>
/// <code>
/// // Connect-last shorthand for the soundboard one-clip-one-output case:
/// new AudioGraphBuilder(router)
///     .AddSource(clipDecoder, autoResample: true)
///     .AddOutput(deviceOutput)
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

    /// <summary>Id of the most recently added output (via this builder). <c>null</c> until <see cref="AddOutput"/> runs.</summary>
    public string? LastOutputId => _lastSinkId;

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

    /// <summary>Adds a output to the router with the standard pump capacity (or an override).</summary>
    public AudioGraphBuilder AddOutput(IAudioOutput output, string? id = null, int? pumpCapacityChunks = null)
    {
        _lastSinkId = _router.AddOutput(output, id, pumpCapacityChunks);
        return this;
    }

    /// <summary>
    /// Routes <paramref name="sourceId"/> to <paramref name="outputId"/>. When <paramref name="map"/>
    /// is <c>null</c>, an <see cref="ChannelMap.Identity"/> sized to the output's channel count is used.
    /// </summary>
    public AudioGraphBuilder Connect(string sourceId, string outputId, ChannelMap? map = null, float gain = 1.0f)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        var effective = map ?? ResolveIdentityMap(outputId);
        _router.AddRoute(sourceId, outputId, effective, gain);
        return this;
    }

    /// <summary>
    /// Routes <see cref="LastSourceId"/> to <see cref="LastOutputId"/>. Use this when you just added
    /// one source and one output and want them wired with an identity channel map.
    /// </summary>
    public AudioGraphBuilder ConnectLast(ChannelMap? map = null, float gain = 1.0f)
    {
        if (_lastSourceId is null)
            throw new InvalidOperationException("AudioGraphBuilder.ConnectLast: no source has been added yet — call AddSource first.");
        if (_lastSinkId is null)
            throw new InvalidOperationException("AudioGraphBuilder.ConnectLast: no output has been added yet — call AddOutput first.");
        return Connect(_lastSourceId, _lastSinkId, map, gain);
    }

    private ChannelMap ResolveIdentityMap(string outputId)
    {
        if (!_router.TryGetOutput(outputId, out var output) || output is null)
            throw new ArgumentException($"unknown output id '{outputId}'", nameof(outputId));
        return ChannelMap.Identity(output.Format.Channels);
    }
}
