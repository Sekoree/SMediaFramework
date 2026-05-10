using S.Media.Core.Clock;

namespace S.Media.Core.Audio;

/// <summary>
/// High-level facade that wires an <see cref="AudioRouter"/>, a
/// <see cref="MediaClock"/>, and one-or-more decoder sources into a single
/// "media player" surface. Coordinates Play / Pause / Resume / Stop / Seek so
/// callers don't have to manually keep router and clock in sync.
/// </summary>
/// <remarks>
/// <para>
/// The first output that implements <em>both</em> <see cref="IClockedSink"/>
/// and <see cref="IPlaybackClock"/> (typically the PortAudio output) becomes
/// the <strong>primary</strong>: the router is slaved to its consumption rate
/// and the <see cref="Clock"/> derives its position from its played samples.
/// You can opt out by setting <see cref="AutoWirePrimary"/> to <c>false</c>
/// before adding outputs and configuring the router/clock yourself.
/// </para>
/// <para>
/// Sources passed to <see cref="LoadFile"/> (or <see cref="AddOwnedSource"/>)
/// are owned by the player and disposed with it. Sinks are <strong>not</strong>
/// owned — the caller decides when to dispose hardware resources, since they
/// often outlive a single player instance.
/// </para>
/// <para>
/// <see cref="Router"/> and <see cref="Clock"/> are exposed for power users
/// who need finer control (custom routes, channel maps, per-route gains,
/// non-default <see cref="IRouterClock"/>, etc.).
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

    public AudioRouter Router => _router;
    public MediaClock Clock => _clock;

    /// <summary>The playhead — derived from the master playback clock when one's attached.</summary>
    public TimeSpan Position => _clock.CurrentPosition;

    /// <summary>True while the router is producing chunks (false when stopped or paused).</summary>
    public bool IsPlaying => _router.IsRunning;

    /// <summary>The current primary sink (whose clock paces production), or <c>null</c> if none.</summary>
    public string? PrimarySinkId { get { lock (_gate) return _primarySinkId; } }

    /// <summary>
    /// When <c>true</c> (default), the first eligible output (implements both
    /// <see cref="IClockedSink"/> and <see cref="IPlaybackClock"/>) becomes
    /// the primary clock source automatically. Set to <c>false</c> before
    /// <see cref="AddOutput"/> if you want to manage clocking manually.
    /// </summary>
    public bool AutoWirePrimary { get; set; } = true;

    public AudioPlayer(int sampleRate = 48000, int chunkSamples = 480)
    {
        _router = new AudioRouter(sampleRate, chunkSamples);
        _clock = new MediaClock();
    }

    // --- attaching pieces -------------------------------------------------

    /// <summary>
    /// Register an output sink with the router. If <see cref="AutoWirePrimary"/>
    /// is on and this is the first sink that implements both
    /// <see cref="IClockedSink"/> and <see cref="IPlaybackClock"/>, the router
    /// is slaved to it and the clock's master is set to it.
    /// </summary>
    public string AddOutput(IAudioSink sink, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sinkId = _router.AddSink(sink, id);

        lock (_gate)
        {
            _sinkFormats[sinkId] = sink.Format;
            if (AutoWirePrimary && _primarySinkId is null
                && sink is IClockedSink && sink is IPlaybackClock pc)
            {
                _router.SlaveTo(sinkId);
                _clock.SetMaster(pc);
                _primarySinkId = sinkId;
            }
        }
        return sinkId;
    }

    /// <summary>
    /// Remove an output sink. Cleans up the player's metadata and forwards
    /// to <see cref="AudioRouter.RemoveSink"/>. If this was the primary sink,
    /// the clock master is detached and the router clock falls back to
    /// wall-clock automatically (via <see cref="SinkSlavedRouterClock"/>'s
    /// fallback chain).
    /// </summary>
    public bool RemoveOutput(string sinkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool removed;
        lock (_gate)
        {
            removed = _router.RemoveSink(sinkId);
            if (!removed) return false;
            _sinkFormats.Remove(sinkId);
            if (_primarySinkId == sinkId)
            {
                _primarySinkId = null;
                _clock.SetMaster(null);
                // Router's slaved clock auto-falls-back to wall-clock when the
                // sink is gone, no SetClock call needed.
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
    public string AddOwnedSource(IAudioSource source, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sourceId = _router.AddSource(source, id);
        if (source is IDisposable d) _ownedDisposables.Add(d);
        return sourceId;
    }

    /// <summary>
    /// Convenience: register a route from <paramref name="sourceId"/> to
    /// <paramref name="sinkId"/>. <paramref name="map"/> defaults to identity
    /// sized to the sink's channel count.
    /// </summary>
    public void Connect(string sourceId, string sinkId, ChannelMap? map = null, float gain = 1.0f)
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
                if (!_sinkFormats.TryGetValue(sinkId, out var fmt))
                    throw new ArgumentException($"unknown sink '{sinkId}'", nameof(sinkId));
                channels = fmt.Channels;
            }
            effective = ChannelMap.Identity(channels);
        }
        _router.AddRoute(sourceId, sinkId, effective, gain);
    }

    // --- lifecycle --------------------------------------------------------

    /// <summary>Start playback: starts the router and the clock. Idempotent.</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _router.Start();
        _clock.Start();
    }

    /// <summary>
    /// Immediate-silence pause: aborts the audio device buffer (via
    /// <see cref="IFlushableSink"/>) and freezes the clock. <see cref="Resume"/>
    /// continues from the current position.
    /// </summary>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
    /// Set the gain on the route from <paramref name="sourceId"/> to
    /// <paramref name="sinkId"/>. The change linearly fades over the next
    /// chunk (click-free).
    /// </summary>
    public void SetVolume(string sourceId, string sinkId, float gain)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _router.SetRouteGain(sourceId, sinkId, gain);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { /* best-effort */ }
        _router.Dispose();
        _clock.Dispose();
        foreach (var d in _ownedDisposables)
        {
            try { d.Dispose(); } catch { /* don't let one disposal swallow others */ }
        }
        _ownedDisposables.Clear();
    }
}