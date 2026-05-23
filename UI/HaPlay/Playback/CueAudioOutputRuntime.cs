using HaPlay.Models;
using HaPlay.ViewModels;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.NDI;
using S.Media.PortAudio;

namespace HaPlay.Playback;

/// <summary>
/// One per active audio-capable output line. Owns a shared <see cref="AudioRouter"/> so N cues' audio
/// sources can mix into a single physical output — the missing piece that <see cref="HaPlayPlaybackSession"/>'s
/// per-cue exclusive acquire pattern couldn't support.
/// </summary>
/// <remarks>
/// Lifecycle is ref-counted by the engine: when the last source is removed, the engine disposes
/// the runtime, which stops the router and releases the acquired output.
/// </remarks>
internal sealed class CueAudioOutputRuntime : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.CueAudioOutputRuntime");

    private readonly OutputManagementViewModel _outputs;
    private readonly OutputLineViewModel _line;
    private readonly IAudioOutput _audioOutput;
    private readonly IPlaybackClock? _playbackClock;
    private readonly AudioRouter _router;
    private readonly string _outputId;
    private readonly Action _releaseOutput;
    private readonly object _gate = new();
    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Throws if the output line couldn't be acquired (already held, not running, or not audio-capable).</summary>
    public CueAudioOutputRuntime(OutputLineViewModel line, OutputManagementViewModel outputs)
    {
        _line = line;
        _outputs = outputs;

        var (audioOutput, playbackClock, releaseOutput) = AcquireOutput(line, outputs);
        _audioOutput = audioOutput;
        _playbackClock = playbackClock;
        _releaseOutput = releaseOutput;

        var format = _audioOutput.Format;
        if (!format.IsValid)
        {
            _releaseOutput();
            throw new InvalidOperationException(
                $"Audio output '{line.Definition.DisplayName}' has invalid format {format}.");
        }

        try
        {
            _router = new AudioRouter(format.SampleRate);
            _outputId = _router.AddOutput(_audioOutput, id: $"shared_{line.Definition.Id:N}");
        }
        catch
        {
            _releaseOutput();
            throw;
        }
    }

    public Guid OutputLineId => _line.Definition.Id;

    public AudioFormat OutputFormat => _audioOutput.Format;

    public IPlaybackClock? PlaybackClock => _playbackClock;

    public int SourceCount
    {
        get { lock (_gate) return _sources.Count; }
    }

    /// <summary>Adds a cue's audio source as an input and routes it per the route's channel map +
    /// gain. Returns the router source id so the caller can call <see cref="RemoveSource"/> on stop.
    /// </summary>
    public string AddSource(IAudioSource source, IReadOnlyList<CueAudioRoute> routes, string sourceIdHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (routes.Count == 0)
            throw new ArgumentException("at least one route required", nameof(routes));

        var srcId = _router.AddSource(source, id: sourceIdHint, autoResample: true);
        lock (_gate) _sources.Add(srcId);

        // One route-per-channel: each CueAudioRoute becomes its own router route with a single-
        // entry ChannelMap. AudioRouter's accumulator mixes them. Gain converts from dB to linear.
        var outChannels = _audioOutput.Format.Channels;
        var idx = 0;
        foreach (var route in routes)
        {
            var span = new int[outChannels];
            Array.Fill(span, -1);
            var outCh = Math.Clamp(route.OutputChannel - 1, 0, outChannels - 1);
            span[outCh] = Math.Max(0, route.SourceChannel);
            var map = new ChannelMap(span);
            var gain = route.Muted ? 0f : DbToLinear(route.GainDb);
            try
            {
                _router.AddRoute(srcId, _outputId, routeId: $"{srcId}_r{idx++}", map, gain);
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "CueAudioOutputRuntime: AddRoute failed for cue source {Src} → out {Out}",
                    srcId, _outputId);
            }
        }

        _router.Play();
        return srcId;
    }

    public void RemoveSource(string sourceId)
    {
        try { _router.RemoveSource(sourceId); }
        catch (Exception ex) { Trace.LogWarning(ex, "CueAudioOutputRuntime.RemoveSource: {Src}", sourceId); }
        lock (_gate) _sources.Remove(sourceId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _router.Dispose(); }
        catch (Exception ex) { Trace.LogWarning(ex, "CueAudioOutputRuntime.Dispose: router"); }

        try { _releaseOutput(); }
        catch (Exception ex) { Trace.LogWarning(ex, "CueAudioOutputRuntime.Dispose: release"); }
    }

    private static (IAudioOutput Output, IPlaybackClock? PlaybackClock, Action Release) AcquireOutput(
        OutputLineViewModel line,
        OutputManagementViewModel outputs)
    {
        switch (line.Definition)
        {
            case PortAudioOutputDefinition:
            {
                var pa = outputs.TryAcquirePortAudioForPlayback(line)
                    ?? throw new InvalidOperationException(
                        $"PortAudio output '{line.Definition.DisplayName}' couldn't be acquired (preview not running or held).");
                return (pa, pa, () => outputs.ReleasePortAudioForPlayback(line));
            }
            case NDIOutputDefinition { StreamMode: not NDIOutputStreamMode.VideoOnly } nd:
            {
                var ndi = outputs.TryAcquireNDICarrierForPlayback(line, needsVideo: false, needsAudio: true)
                    ?? throw new InvalidOperationException(
                        $"NDI output '{line.Definition.DisplayName}' couldn't be acquired (carrier not running or held).");
                try
                {
                    var channels = Math.Max(1, nd.AudioChannelCount);
                    var sampleRate = Math.Max(1, nd.AudioSampleRate);
                    var output = ndi.EnableAudio(new AudioFormat(sampleRate, channels));
                    return (output, null, () => outputs.ReleaseNDICarrierForPlayback(line, releaseVideo: false, releaseAudio: true));
                }
                catch
                {
                    outputs.ReleaseNDICarrierForPlayback(line, releaseVideo: false, releaseAudio: true);
                    throw;
                }
            }
            case NDIOutputDefinition:
                throw new InvalidOperationException(
                    $"NDI output '{line.Definition.DisplayName}' is video-only and cannot receive cue audio.");
            default:
                throw new InvalidOperationException(
                    $"Output '{line.Definition.DisplayName}' is not an audio-capable output.");
        }
    }

    private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);
}
