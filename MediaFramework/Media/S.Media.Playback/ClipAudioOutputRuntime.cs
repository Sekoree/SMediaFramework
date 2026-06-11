using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;

namespace S.Media.Playback;

/// <summary>
/// Shared cue audio-output runtime: owns one <see cref="AudioRouter"/> feeding one physical
/// <see cref="IAudioOutput"/>, and lets multiple cue clips add/remove routed sources.
/// </summary>
public sealed class ClipAudioOutputRuntime : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Playback.ClipAudioOutputRuntime");

    private readonly IAudioOutput _audioOutput;
    private readonly AudioRouter _router;
    private readonly string _routerOutputId;
    private readonly Action? _releaseOutput;
    private readonly object _gate = new();
    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, RouteGainTarget>> _sourceRouteGains = new(StringComparer.Ordinal);
    private bool _disposed;

    public ClipAudioOutputRuntime(
        string outputId,
        IAudioOutput audioOutput,
        IPlaybackClock? playbackClock = null,
        Action? releaseOutput = null,
        string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        OutputId = outputId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? outputId : displayName;
        _audioOutput = audioOutput ?? throw new ArgumentNullException(nameof(audioOutput));
        PlaybackClock = playbackClock;
        _releaseOutput = releaseOutput;

        var format = _audioOutput.Format;
        if (!format.IsValid)
        {
            _releaseOutput?.Invoke();
            throw new InvalidOperationException(
                $"Audio output '{DisplayName}' has invalid format {format}.");
        }

        try
        {
            _router = new AudioRouter(format.SampleRate);
            _routerOutputId = _router.AddOutput(_audioOutput, id: $"shared_{outputId}");
        }
        catch
        {
            _releaseOutput?.Invoke();
            throw;
        }
    }

    public string OutputId { get; }

    public string DisplayName { get; }

    public AudioFormat OutputFormat => _audioOutput.Format;

    public IPlaybackClock? PlaybackClock { get; }

    public int SourceCount
    {
        get { lock (_gate) return _sources.Count; }
    }

    public string AddSource(
        IAudioSource source,
        IReadOnlyList<AudioRouteSpec> routes,
        string sourceIdHint,
        Func<string, int, string>? routeIdFactory = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0)
            throw new ArgumentException("at least one route required", nameof(routes));

        var srcId = _router.AddSource(source, id: sourceIdHint, autoResample: true);
        try
        {
            var outChannels = _audioOutput.Format.Channels;
            var routePlans = new List<(string RouteId, ChannelMap Map, float Gain)>(routes.Count);
            for (var i = 0; i < routes.Count; i++)
            {
                var (map, gain) = BuildRoutePlan(routes[i], outChannels);
                var routeId = routeIdFactory?.Invoke(srcId, i);
                routePlans.Add((string.IsNullOrWhiteSpace(routeId) ? $"{srcId}_r{i}" : routeId, map, gain));
            }

            foreach (var (routeId, map, gain) in routePlans)
                _router.AddRoute(srcId, _routerOutputId, routeId, map, gain);

            lock (_gate)
            {
                _sources.Add(srcId);
                _sourceRouteGains[srcId] = routePlans.ToDictionary(
                    route => route.RouteId,
                    route => new RouteGainTarget(route.RouteId, route.Gain),
                    StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            try { _router.RemoveSource(srcId); }
            catch (Exception removeEx)
            {
                Trace.LogWarning(removeEx, "ClipAudioOutputRuntime: rollback RemoveSource failed for {Src}", srcId);
            }
            throw new InvalidOperationException(
                $"Failed to route cue source '{srcId}' to audio output '{DisplayName}'.",
                ex);
        }

        return srcId;
    }

    public void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _router.Play();
    }

    /// <summary>Pump throughput stats for the runtime's single physical output (operator HUD / output health).</summary>
    public AudioRouter.OutputPumpStats GetOutputPumpStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _router.GetPumpStats(_routerOutputId);
    }

    public Task FadeOutSourceAsync(string sourceId, TimeSpan duration, CancellationToken cancellationToken = default) =>
        FadeSourceAsync(sourceId, duration, fromScale: 1f, toScale: 0f, cancellationToken);

    public Task FadeInSourceAsync(string sourceId, TimeSpan duration, CancellationToken cancellationToken = default) =>
        FadeSourceAsync(sourceId, duration, fromScale: 0f, toScale: 1f, cancellationToken);

    /// <summary>Ramps the source's route gains from <paramref name="fromScale"/>×base to
    /// <paramref name="toScale"/>×base over <paramref name="duration"/> (~33 ms steps).</summary>
    public async Task FadeSourceAsync(
        string sourceId,
        TimeSpan duration,
        float fromScale,
        float toScale,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sourceId))
            return;

        var routes = SnapshotRouteGains(sourceId);
        if (routes.Count == 0)
            return;

        var steps = Math.Clamp((int)Math.Ceiling(Math.Max(1, duration.TotalMilliseconds) / 33.0), 1, 120);
        var delay = duration > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Max(1, duration.Ticks / steps))
            : TimeSpan.Zero;

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = (float)step / steps;
            SetRouteScale(routes, Math.Clamp(fromScale + (toScale - fromScale) * t, 0f, 1f));
            if (step < steps && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Immediately scales the source's route gains (scale×base). Use scale 0 before starting
    /// playback so a fade-in doesn't blip at full gain for the first chunks.</summary>
    public void SetSourceGainScale(string sourceId, float scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sourceId))
            return;
        SetRouteScale(SnapshotRouteGains(sourceId), Math.Clamp(scale, 0f, 1f));
    }

    private List<RouteGainTarget> SnapshotRouteGains(string sourceId)
    {
        lock (_gate)
        {
            return _sourceRouteGains.TryGetValue(sourceId, out var found)
                ? found.Values.ToList()
                : [];
        }
    }

    private void SetRouteScale(IEnumerable<RouteGainTarget> routes, float scale)
    {
        foreach (var route in routes)
        {
            try { _router.SetRouteGainById(route.RouteId, route.BaseGain * scale); }
            catch (Exception ex) { Trace.LogWarning(ex, "ClipAudioOutputRuntime.SetRouteScale: {Route}", route.RouteId); }
        }
    }

    public void UpdateRoute(string sourceId, string routeId, AudioRouteSpec route)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(routeId);
        if (!string.Equals(route.OutputId, OutputId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Route targets output '{route.OutputId}', but this runtime owns '{OutputId}'.");

        var (map, gain) = BuildRoutePlan(route, _audioOutput.Format.Channels);
        _router.AddRoute(sourceId, _routerOutputId, routeId, map, gain);
        lock (_gate)
        {
            if (!_sourceRouteGains.TryGetValue(sourceId, out var routes))
                _sourceRouteGains[sourceId] = routes = new Dictionary<string, RouteGainTarget>(StringComparer.Ordinal);
            routes[routeId] = new RouteGainTarget(routeId, gain);
        }
    }

    public void SetRouteGain(string sourceId, string routeId, double gainDb, bool muted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(routeId);

        var gain = muted ? 0f : DbToLinear(gainDb);
        _router.SetRouteGainById(routeId, gain);
        lock (_gate)
        {
            if (!_sourceRouteGains.TryGetValue(sourceId, out var routes))
                _sourceRouteGains[sourceId] = routes = new Dictionary<string, RouteGainTarget>(StringComparer.Ordinal);
            routes[routeId] = new RouteGainTarget(routeId, gain);
        }
    }

    public bool RemoveRoute(string sourceId, string routeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(routeId);

        var removed = false;
        try { removed = _router.RemoveRouteById(routeId); }
        catch (Exception ex) { Trace.LogWarning(ex, "ClipAudioOutputRuntime.RemoveRoute: {Route}", routeId); }
        lock (_gate)
        {
            if (_sourceRouteGains.TryGetValue(sourceId, out var routes))
                routes.Remove(routeId);
        }
        return removed;
    }

    public void RemoveSource(string sourceId)
    {
        try { _router.RemoveSource(sourceId); }
        catch (Exception ex) { Trace.LogWarning(ex, "ClipAudioOutputRuntime.RemoveSource: {Src}", sourceId); }
        lock (_gate)
        {
            _sources.Remove(sourceId);
            _sourceRouteGains.Remove(sourceId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        MediaDiagnostics.SwallowDisposeErrors(_router.Dispose, "ClipAudioOutputRuntime.Dispose: router");
        if (_releaseOutput is not null)
            MediaDiagnostics.SwallowDisposeErrors(_releaseOutput, "ClipAudioOutputRuntime.Dispose: release");
    }

    private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);

    private static (ChannelMap Map, float Gain) BuildRoutePlan(AudioRouteSpec route, int outChannels)
    {
        if (route.SourceChannel < 0)
            throw new InvalidOperationException(
                $"Route source channel {route.SourceChannel} is invalid; source channels are zero-based.");
        if (route.OutputChannel < 1 || route.OutputChannel > outChannels)
            throw new InvalidOperationException(
                $"Route output channel {route.OutputChannel} is outside output channel range 1-{outChannels}.");

        var span = new int[outChannels];
        Array.Fill(span, -1);
        span[route.OutputChannel - 1] = route.SourceChannel;
        var map = new ChannelMap(span);
        var gain = route.Muted ? 0f : DbToLinear(route.GainDb);
        return (map, gain);
    }

    private sealed record RouteGainTarget(string RouteId, float BaseGain);
}
