using Microsoft.Extensions.Logging;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>
/// High-level facade that wires an <see cref="AudioRouter"/>, a
/// <see cref="MediaClock"/>, and one-or-more decoder sources into a single
/// "media player" surface. Coordinates Play / Pause / Resume / Stop / Seek so
/// callers don't have to manually keep router and clock in sync.
/// </summary>
/// <remarks>
/// <para>
/// The first output that implements <see cref="IClockedOutput"/> (typically
/// PortAudio for pacing) becomes the <strong>primary</strong> for the router.
/// If it also implements <see cref="IPlaybackClock"/>, the
/// <see cref="Clock"/> masters to it so position tracks played samples;
/// otherwise the clock stays in stopwatch mode until you attach a master via
/// <see cref="MediaClock.SetMaster"/>.
/// You can opt out by setting <see cref="AutoWirePrimary"/> to <c>false</c>
/// before adding outputs and configuring the router/clock yourself.
/// </para>
/// <para>
/// Sources passed to <see cref="AddOwnedSource"/> (for example an
/// <c>AudioFileDecoder</c> opened by the caller) are owned by the player and
/// disposed with it. Outputs are <strong>not</strong>
/// owned — the caller decides when to dispose hardware resources, since they
/// often outlive a single player instance.
/// </para>
/// <para>
/// <see cref="Router"/> and <see cref="Clock"/> are exposed for power users
/// who need finer control (custom routes, channel maps, per-route gains,
/// non-default <see cref="IRouterClock"/>, etc.).
/// </para>
/// <para>
/// To pre-buffer a PortAudio device ring before <see cref="Play"/> (decoder-direct,
/// before the router thread runs), use <c>S.Media.PortAudio.PortAudioOutput.PrefillFrom</c>
/// or <c>S.Media.PortAudio.AudioPlayerPortAudioExtensions.TryPrefillPrimaryPortAudio</c>
/// from a project that references <c>S.Media.PortAudio</c>.
/// </para>
/// </remarks>
public sealed class AudioPlayer : IDisposable
{
    private readonly AudioRouter _router;
    private readonly MediaClock _clock;
    private readonly List<IDisposable> _ownedDisposables = [];
    private readonly Dictionary<string, AudioFormat> _sinkFormats = [];
    private readonly Lock _gate = new();

    private string? _primarySinkId;
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Audio.AudioPlayer");

    public AudioRouter Router => _router;
    public MediaClock Clock => _clock;

    /// <summary>Same object as <see cref="Clock"/> — <see cref="IPlaybackTimeline"/> surface for strategy‑B consumers.</summary>
    public IPlaybackTimeline Timeline => _clock;

    /// <summary>The playhead — derived from the master playback clock when one's attached.</summary>
    public TimeSpan Position => _clock.CurrentPosition;

    /// <summary>True while the router is producing chunks (false when stopped or paused).</summary>
    public bool IsPlaying => _router.IsRunning;

    /// <summary>The current primary output (whose clock paces production), or <c>null</c> if none.</summary>
    public string? PrimaryOutputId { get { lock (_gate) return _primarySinkId; } }

    /// <summary>
    /// When <c>true</c> (default), the first output that implements
    /// <see cref="IClockedOutput"/> becomes the pacing primary automatically.
    /// <see cref="AddOutput"/> if you want to manage clocking manually.
    /// </summary>
    public bool AutoWirePrimary { get; set; } = true;

    /// <param name="sampleRate">
    /// Router nominal sample rate. No baked-in default — callers must declare the rate explicitly
    /// (typically the decoder's <c>Format.SampleRate</c>) so format mismatches surface at wire-up
    /// time rather than as silent resamples or output garbling.
    /// </param>
    /// <param name="chunkSamples">Per-channel chunk size used by the router pump (default 480 = 10 ms @ 48 kHz).</param>
    public AudioPlayer(int sampleRate, int chunkSamples = 480)
    {
        _router = new AudioRouter(sampleRate, chunkSamples);
        _clock = new MediaClock();
    }

    // --- attaching pieces -------------------------------------------------

    /// <summary>
    /// Register an output output with the router. See <see cref="AutoWirePrimary"/>
    /// for clock/pacing wiring.
    /// </summary>
    /// <param name="outputPumpCapacityChunks">
    /// Optional per-output pump queue depth (mixed chunks). <c>null</c> inherits the router
    /// default (currently 8 → ≈ 80&#160;ms at chunkSamples=480 / 48&#160;kHz). For hardware outputs
    /// implementing <see cref="IClockedOutput"/> a smaller value (2–4) keeps end-to-end latency
    /// down — they have their own ring. See <see cref="AudioRouter.AddOutput"/> for the full
    /// latency-budget discussion.
    /// </param>
    public string AddOutput(IAudioOutput output, string? id = null, int? outputPumpCapacityChunks = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var outputId = _router.AddOutput(output, id, outputPumpCapacityChunks);

        lock (_gate)
        {
            _sinkFormats[outputId] = output.Format;
            if (AutoWirePrimary && _primarySinkId is null && output is IClockedOutput)
            {
                _router.SlaveTo(outputId);
                if (output is IPlaybackClock pc)
                {
                    _clock.SetMaster(pc);
                    Trace.LogDebug("AddOutput: promoted output {SinkId} to primary, master clock set ({ClockType})",
                        outputId, pc.GetType().Name);
                }
                else
                {
                    Trace.LogDebug("AddOutput: output {SinkId} promoted to primary (IClockedOutput, but no IPlaybackClock — media clock stays in stopwatch mode)",
                        outputId);
                }
                _primarySinkId = outputId;
            }
            else if (AutoWirePrimary && _primarySinkId is null)
            {
                Trace.LogTrace("AddOutput: output {SinkId} ({SinkType}) does not implement IClockedOutput — not eligible as router master",
                    outputId, output.GetType().Name);
            }
        }
        return outputId;
    }

    /// <summary>
    /// Remove an output output. Cleans up the player's metadata and forwards
    /// to <see cref="AudioRouter.RemoveOutput"/>. If this was the primary output,
    /// the clock master is detached. When <see cref="AutoWirePrimary"/> is
    /// <c>true</c>, another output that implements <see cref="IClockedOutput"/> is
    /// promoted (router pacing + optional <see cref="IPlaybackClock"/> master)
    /// if one remains.
    /// </summary>
    public bool RemoveOutput(string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool removed;
        string? promoteTo = null;
        lock (_gate)
        {
            removed = _router.RemoveOutput(outputId);
            if (!removed) return false;
            _sinkFormats.Remove(outputId);
            if (_primarySinkId == outputId)
            {
                _primarySinkId = null;
                _clock.SetMaster(null);
                if (AutoWirePrimary)
                {
                    foreach (var id in _router.SinkIds.Order(StringComparer.Ordinal))
                    {
                        if (id == outputId) continue;
                        if (!_router.TryGetOutput(id, out var s) || s is not IClockedOutput) continue;
                        promoteTo = id;
                        break;
                    }
                }
            }
        }

        if (promoteTo is not null)
        {
            _router.RetargetSlaveClock(promoteTo);
            lock (_gate)
            {
                if (_router.TryGetOutput(promoteTo, out var s) && s is IPlaybackClock pc)
                    _clock.SetMaster(pc);
                _primarySinkId = promoteTo;
            }
        }

        return true;
    }

    /// <summary>
    /// Add a source the player will own (dispose with it). Returns the source
    /// id assigned by the router. To pull file decoding into the player surface,
    /// open the decoder yourself and pass it here:
    /// <code>player.AddOwnedSource(AudioFileDecoder.Open(path));</code>
    /// </summary>
    /// <param name="autoResample">
    /// Forwarded to <see cref="AudioRouter.AddSource(IAudioSource, string?, bool)"/>. When
    /// <c>true</c> and the source rate doesn't match the router's nominal rate, the router
    /// transparently wraps the source via the resampler factory installed by
    /// <c>S.Media.FFmpeg</c>'s <c>FFmpegRuntime.EnsureInitialized</c>. The wrapper is owned by the
    /// router; the player only tracks the original (added to <c>_ownedDisposables</c> when it
    /// implements <see cref="IDisposable"/>).
    /// </param>
    public string AddOwnedSource(IAudioSource source, string? id = null, bool autoResample = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sourceId = _router.AddSource(source, id, autoResample);
        if (source is IDisposable d)
        {
            lock (_gate)
                _ownedDisposables.Add(d);
        }
        return sourceId;
    }

    /// <summary>
    /// Convenience: register a route from <paramref name="sourceId"/> to
    /// <paramref name="outputId"/>. <paramref name="map"/> defaults to identity
    /// sized to the output's channel count.
    /// </summary>
    public void Connect(string sourceId, string outputId, ChannelMap? map = null, float gain = 1.0f)
    {
        ChannelMap effective;
        if (map is { } m)
        {
            effective = m;
        }
        else
        {
            int channels;
            lock (_gate)
            {
                if (!_sinkFormats.TryGetValue(outputId, out var fmt))
                    throw new ArgumentException($"unknown output '{outputId}'", nameof(outputId));
                channels = fmt.Channels;
            }
            effective = ChannelMap.Identity(channels);
        }
        _router.AddRoute(sourceId, outputId, effective, gain);
    }

    // --- lifecycle --------------------------------------------------------

    /// <summary>Start playback: starts the router and the clock. Idempotent.</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Trace.LogDebug("Play: primary={Primary} outputs={SinkCount} sources={SourceCount}",
            _primarySinkId ?? "(none)", _router.SinkIds.Count, _router.SourceIds.Count);
        _router.Start();
        _clock.Start();
    }

    /// <summary>
    /// Immediate-silence pause: aborts the audio device buffer (via
    /// <see cref="IFlushableOutput"/>) and freezes the clock. <see cref="Resume"/>
    /// continues from the current position.
    /// </summary>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Trace.LogDebug("Pause: position={Position}", _clock.CurrentPosition);
        _router.Pause();
        _clock.Pause();
    }

    /// <summary>Resume a paused player. Equivalent to <see cref="Play"/>.</summary>
    public void Resume() => Play();

    /// <summary>Stop playback (alias for <see cref="Pause"/>; the next <see cref="Play"/> picks up where you left off).</summary>
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _router.Stop();
        _clock.Stop();
    }

    /// <summary>
    /// Coordinated seek: pauses the router, calls <see cref="ISeekableSource.Seek"/>
    /// on the named source, repositions the clock, then resumes if the player
    /// was playing. The audio device buffer is flushed for immediate audible
    /// effect.
    /// </summary>
    public void Seek(string sourceId, TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _router.SeekSource(sourceId, position);
        _clock.Seek(position);
    }

    /// <summary>
    /// Seeks the only registered source. Throws if there are zero or multiple sources.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string id;
        lock (_gate)
        {
            var ids = Router.SourceIds;
            if (ids.Count != 1)
                throw new InvalidOperationException(
                    $"Seek() requires exactly one registered source (found {ids.Count}); use Seek(sourceId, position) instead.");
            id = ids.First();
        }
        Seek(id, position);
    }

    /// <summary>
    /// Set the gain on the route from <paramref name="sourceId"/> to
    /// <paramref name="outputId"/>. The change linearly fades over the next
    /// chunk (click-free).
    /// </summary>
    public void SetVolume(string sourceId, string outputId, float gain)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _router.SetRouteGain(sourceId, outputId, gain);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(Stop, "AudioPlayer.Dispose: Stop");
        _router.Dispose();
        _clock.Dispose();
        foreach (var d in _ownedDisposables)
        {
            MediaDiagnostics.SwallowDisposeErrors(d.Dispose, "AudioPlayer.Dispose: owned disposable");
        }
        _ownedDisposables.Clear();
    }
}