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
    private readonly Dictionary<string, List<RouteGainTarget>> _sourceRouteGains = new(StringComparer.Ordinal);
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

    public string AddSource(IAudioSource source, IReadOnlyList<AudioRouteSpec> routes, string sourceIdHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0)
            throw new ArgumentException("at least one route required", nameof(routes));

        var srcId = _router.AddSource(source, id: sourceIdHint, autoResample: true);

        var outChannels = _audioOutput.Format.Channels;
        var routePlans = new List<(string RouteId, ChannelMap Map, float Gain)>(routes.Count);
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var span = new int[outChannels];
            Array.Fill(span, -1);
            var outCh = Math.Clamp(route.OutputChannel - 1, 0, outChannels - 1);
            span[outCh] = Math.Max(0, route.SourceChannel);
            var map = new ChannelMap(span);
            var gain = route.Muted ? 0f : DbToLinear(route.GainDb);
            routePlans.Add(($"{srcId}_r{i}", map, gain));
        }

        try
        {
            foreach (var (routeId, map, gain) in routePlans)
                _router.AddRoute(srcId, _routerOutputId, routeId, map, gain);
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

        lock (_gate)
        {
            _sources.Add(srcId);
            _sourceRouteGains[srcId] = routePlans
                .Select(route => new RouteGainTarget(route.RouteId, route.Gain))
                .ToList();
        }

        _router.Play();
        return srcId;
    }

    public async Task FadeOutSourceAsync(string sourceId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sourceId))
            return;

        List<RouteGainTarget> routes;
        lock (_gate)
        {
            if (!_sourceRouteGains.TryGetValue(sourceId, out var found))
                return;
            routes = found.ToList();
        }

        if (routes.Count == 0)
            return;

        var steps = Math.Clamp((int)Math.Ceiling(Math.Max(1, duration.TotalMilliseconds) / 33.0), 1, 120);
        var delay = duration > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Max(1, duration.Ticks / steps))
            : TimeSpan.Zero;

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scale = Math.Clamp(1f - (float)step / steps, 0f, 1f);
            SetRouteScale(routes, scale);
            if (step < steps && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetRouteScale(IEnumerable<RouteGainTarget> routes, float scale)
    {
        foreach (var route in routes)
        {
            try { _router.SetRouteGainById(route.RouteId, route.BaseGain * scale); }
            catch (Exception ex) { Trace.LogWarning(ex, "ClipAudioOutputRuntime.FadeOutSourceAsync: {Route}", route.RouteId); }
        }
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

    private sealed record RouteGainTarget(string RouteId, float BaseGain);
}
