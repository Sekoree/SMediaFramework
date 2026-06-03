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
        lock (_gate) _sources.Add(srcId);

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
            lock (_gate) _sources.Remove(srcId);
            throw new InvalidOperationException(
                $"Failed to route cue source '{srcId}' to audio output '{DisplayName}'.",
                ex);
        }

        _router.Play();
        return srcId;
    }

    public void RemoveSource(string sourceId)
    {
        try { _router.RemoveSource(sourceId); }
        catch (Exception ex) { Trace.LogWarning(ex, "ClipAudioOutputRuntime.RemoveSource: {Src}", sourceId); }
        lock (_gate) _sources.Remove(sourceId);
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
}
