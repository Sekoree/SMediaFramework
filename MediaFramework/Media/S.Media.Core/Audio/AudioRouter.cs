using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Core.Audio;

/// <summary>
/// Routes packed-float audio between any number of <see cref="IAudioSource"/>s
/// and <see cref="IAudioSink"/>s. Each connection is an explicit
/// <see cref="Route"/>: a source ID, a sink ID, a mandatory
/// <see cref="ChannelMap"/>, and a per-route <see cref="Route.Gain"/>. Sinks
/// sum contributions from every route that targets them.
/// </summary>
/// <remarks>
/// <para>
/// There is no central mixer bus. Routes are direct. To play one source on
/// two outputs differently, register two routes from the same source ID to
/// each sink ID with their own channel maps and gains. To mix two sources
/// into one output, register two routes both targeting the same sink — they
/// sum.
/// </para>
/// <para>
/// All sources and sinks must agree on the router's nominal sample rate for
/// routing and mixing. Optional per-leaf wrappers (e.g. FFmpeg
/// <c>AdaptiveRateAudioSink</c>) may apply a tiny rate tweak only on the path
/// into that sink without changing the router's graph rate.
/// </para>
/// <para>
/// The graph is <strong>fully dynamic</strong> — sources, sinks, and routes
/// can be added or removed at any time, including while the router is
/// running. Updates take effect on the next chunk. Mutations swap an immutable
/// <see cref="RouterState"/> under a lock; the run loop reads it without
/// blocking.
/// </para>
/// <para>
/// <strong>Per-sink threading</strong>: every sink gets its own bounded chunk
/// queue plus a drainer thread that calls <see cref="IAudioSink.Submit"/>.
/// Slow or blocking sinks (e.g. a clocked NDI sender) cannot throttle the router
/// or any other sink; they only fill their own queue and eventually drop oldest chunks.
/// Queue depth defaults to the router constructor's <c>pumpCapacityChunks</c>;
/// <see cref="AddSink(IAudioSink, string?, int?)"/> can override depth per sink.
/// </para>
/// <para>
/// Optional route-mix profiling: set <c>MF_MEDIA_PROFILE_CHANNEL_MAP=1</c> (global recording in apps), or use
/// <see cref="ChannelRouteMixProfiling.SetTestOverride"/> with <see cref="ChannelRouteMixProfiling.EnterTestRecordingScope"/> in tests so parallel workers do not share counters.
/// Read <see cref="ChannelRouteMixProfiling"/> to measure scalar versus SIMD fast-path channel mixing in the run loop.
/// </para>
/// <para>
/// <strong>Pacing</strong>: the router is paced by an <see cref="IRouterClock"/>.
/// Default is <see cref="WallClockRouterClock"/>; call <see cref="SlaveTo"/>
/// to bind production to a specific <see cref="IClockedSink"/> (typically a
/// PortAudio output) for sample-accurate sync.
/// </para>
/// <para>
/// <strong>Volume changes are click-free</strong>: <see cref="SetRouteGain"/>
/// updates the route's target in a concurrent map (no immutable graph rebuild);
/// the next chunk linearly interpolates from the
/// previously-applied gain to the new target across its samples, so even a
/// hard 1.0 → 0.0 transition produces a smooth fade rather than a discontinuity.
/// </para>
/// <para>
/// <strong>Multi-output drift</strong>: when multiple outputs are attached,
/// only the slaved sink's clock paces the router. Other sinks' physical clocks
/// (PortAudio's hardware crystal, NDI sender's internal pace) drift relative
/// to the master at typical ±50 ppm hardware tolerance — over hours, the
/// non-slaved sink's pump accumulates fill or empty pressure and eventually
/// drops oldest chunks. Subscribe to <see cref="PumpPressure"/> when
/// <c>Dropped</c> increments so a host can react. For automatic easing on one
/// slow sink without retuning the master clock, wrap that sink with the FFmpeg
/// <c>AdaptiveRateAudioSink</c> (per-sink <c>swresample</c> driven by
/// <see cref="PumpPressurePlaybackHintMonitor"/> filtered to that sink id).
/// </para>
/// </remarks>
public sealed class AudioRouter : IDisposable
{
    private int _sampleRate;
    private readonly int _chunkSamples;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<SinkPump> _pumpsAwaitingDispose = new();
    /// <summary>Per-route "currently applied" gain. <see cref="Route.Gain"/> is the target; this tracks the value we ramped to last chunk.</summary>
    private readonly ConcurrentDictionary<string, float> _currentGains = new();
    /// <summary>Concurrent target gain for routing (hot updates from <see cref="SetRouteGain"/> without rewriting <see cref="RouterState"/>).</summary>
    private readonly ConcurrentDictionary<string, float> _routeTargetGains = new();
    private readonly ILogger? _log;
    private readonly int _pumpCapacityChunks;

    private RouterState _state = RouterState.Empty;
    private IRouterClock _clock;
    /// <summary>When pacing uses <see cref="SinkSlavedRouterClock"/>, the sink id passed to <see cref="SlaveTo"/> / <see cref="RetargetSlaveClock"/>.</summary>
    private string? _slaveClockSinkId;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _disposed;
    private long _chunksProduced;
    private int _idCounter;

    /// <summary>
    /// Nominal mix sample rate in Hz — fixed for the lifetime of this <see cref="AudioRouter"/>.
    /// </summary>
    /// <remarks>Changing rate at runtime without rebuilding the graph is not supported; construct a new router.</remarks>
    public int SampleRate => _sampleRate;
    public int ChunkSamples => _chunkSamples;

    public bool IsRunning { get { lock (_gate) return _isRunning; } }
    /// <summary>True after the router stopped on its own because every source was exhausted.</summary>
    public bool CompletedNaturally { get; private set; }
    public long ChunksProduced => Volatile.Read(ref _chunksProduced);

    /// <summary>The active <see cref="IRouterClock"/>. Swapped via <see cref="SlaveTo"/> / <see cref="SetClock"/> — only safe while stopped.</summary>
    public IRouterClock Clock { get { lock (_gate) return _clock; } }

    /// <summary>Raised when a sink throws from <see cref="IAudioSink.Submit"/> (non-fatal; pump keeps running).</summary>
    public event EventHandler<AudioRouterSinkErrorEventArgs>? SinkErrored;

    /// <summary>Raised when a sink pump drops chunks — sustained drops mean the sink is behind.</summary>
    public event EventHandler<AudioRouterPumpPressureEventArgs>? PumpPressure;

    public AudioRouter(int sampleRate, int chunkSamples = 480, int pumpCapacityChunks = 8)
        : this(sampleRate, chunkSamples, clock: null, pumpCapacityChunks, logger: null) { }

    public AudioRouter(int sampleRate, int chunkSamples, IRouterClock? clock, int pumpCapacityChunks = 8, ILogger? logger = null)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "sample rate must be positive");
        if (chunkSamples < 16)
            throw new ArgumentOutOfRangeException(nameof(chunkSamples), "must be >= 16");
        if (pumpCapacityChunks < 2)
            throw new ArgumentOutOfRangeException(nameof(pumpCapacityChunks), "must be >= 2");

        _sampleRate = sampleRate;
        _chunkSamples = chunkSamples;
        _pumpCapacityChunks = pumpCapacityChunks;
        _clock = clock ?? new WallClockRouterClock(sampleRate, chunkSamples);
        _log = logger;
    }

    // --- registration ------------------------------------------------------

    /// <summary>
    /// Register a source. <paramref name="id"/> defaults to an auto-generated
    /// value (<c>src_1</c>, <c>src_2</c>, …) — pass an explicit ID to make
    /// route definitions self-documenting.
    /// </summary>
    public string AddSource(IAudioSource source, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (source.Format.SampleRate != _sampleRate)
            throw new InvalidOperationException(
                $"source sample rate {source.Format.SampleRate} doesn't match router's {_sampleRate} (resampling not implemented)");

        lock (_gate)
        {
            id ??= $"__auto_src_{++_idCounter}";
            ArgumentException.ThrowIfNullOrEmpty(id);
            if (_state.Sources.ContainsKey(id))
                throw new ArgumentException($"source ID '{id}' is already registered", nameof(id));

            var entry = new SourceEntry(id, source, new float[_chunkSamples * source.Format.Channels]);
            Volatile.Write(ref _state, _state with { Sources = _state.Sources.Add(id, entry) });
            return id;
        }
    }

    /// <summary>Removes a source and any routes that reference it. Returns false if no source had that ID.</summary>
    public bool RemoveSource(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        lock (_gate)
        {
            if (!_state.Sources.ContainsKey(id)) return false;
            foreach (var r in _state.Routes)
                if (r.SourceId == id)
                {
                    var rk = RouteGainDictionaryKey(r.SourceId, r.SinkId);
                    _currentGains.TryRemove(rk, out _);
                    _routeTargetGains.TryRemove(rk, out _);
                }
            Volatile.Write(ref _state, _state with
            {
                Sources = _state.Sources.Remove(id),
                Routes = _state.Routes.RemoveAll(r => r.SourceId == id),
            });
            return true;
        }
    }

    /// <param name="pumpCapacityChunks">
    /// Bounded depth of this sink's background chunk queue (see remarks on
    /// <see cref="AudioRouter"/>). <c>null</c> uses the router constructor default.
    /// When set, must be at least 2.
    /// </param>
    public string AddSink(IAudioSink sink, string? id = null, int? pumpCapacityChunks = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (sink.Format.SampleRate != _sampleRate)
            throw new InvalidOperationException(
                $"sink sample rate {sink.Format.SampleRate} doesn't match router's {_sampleRate}");

        var capacity = pumpCapacityChunks ?? _pumpCapacityChunks;
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(pumpCapacityChunks), "must be >= 2 when specified");

        lock (_gate)
        {
            id ??= $"__auto_sink_{++_idCounter}";
            ArgumentException.ThrowIfNullOrEmpty(id);
            if (_state.Sinks.ContainsKey(id))
                throw new ArgumentException($"sink ID '{id}' is already registered", nameof(id));

            var floatsPerChunk = _chunkSamples * sink.Format.Channels;
            var pump = new SinkPump(this, sink, capacity, floatsPerChunk, id);
            var entry = new SinkEntry(id, sink, pump);
            Volatile.Write(ref _state, _state with { Sinks = _state.Sinks.Add(id, entry) });
            return id;
        }
    }

    /// <summary>
    /// Removes a sink and any routes that target it. Any chunks queued in the
    /// sink's pump are abandoned synchronously; an in-flight
    /// <see cref="IAudioSink.Submit"/> on the pump's drainer thread completes
    /// (briefly waited for) before the pump's thread teardown is scheduled.
    /// Returns false if no sink had that ID.
    /// </summary>
    public bool RemoveSink(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        SinkPump? pump;
        bool wasRunning;
        lock (_gate)
        {
            if (!_state.Sinks.TryGetValue(id, out var entry)) return false;
            pump = entry.Pump;
            // Use the pre-removal Routes snapshot to know exactly which
            // (sourceId, sinkId) keys belong to this sink — avoids enumerating
            // the entire _currentGains dictionary while concurrent
            // AddRoute/RemoveRoute calls mutate it.
            foreach (var route in _state.Routes)
                if (route.SinkId == id)
                {
                    var rk = RouteGainDictionaryKey(route.SourceId, route.SinkId);
                    _currentGains.TryRemove(rk, out _);
                    _routeTargetGains.TryRemove(rk, out _);
                }
            Volatile.Write(ref _state, _state with
            {
                Sinks = _state.Sinks.Remove(id),
                Routes = _state.Routes.RemoveAll(r => r.SinkId == id),
            });
            wasRunning = _isRunning;
        }
        // Drop any pending chunks (caller asked for "stop delivering"), then
        // wait briefly for an in-flight Submit on the drainer thread to
        // complete. The next run-loop iteration sees the new state and won't
        // enqueue further; the pump's thread join is deferred so RemoveSink
        // can return promptly.
        pump.AbandonQueue();
        pump.WaitForIdle(TimeSpan.FromMilliseconds(100), cancellationToken);
        if (wasRunning) _pumpsAwaitingDispose.Enqueue(pump);
        else pump.Dispose();
        return true;
    }

    // --- routing -----------------------------------------------------------

    /// <summary>
    /// Add (or replace) a route from <paramref name="sourceId"/> to
    /// <paramref name="sinkId"/>. The route's <paramref name="map"/> describes
    /// how source channels feed sink channels — its
    /// <see cref="ChannelMap.OutputChannels"/> must match the sink's channel
    /// count. <paramref name="gain"/> scales the contribution before summation.
    /// Re-adding for an existing pair replaces it.
    /// </summary>
    public void AddRoute(string sourceId, string sinkId, ChannelMap map, float gain = 1.0f)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!_state.Sources.TryGetValue(sourceId, out var src))
                throw new ArgumentException($"unknown source ID '{sourceId}'", nameof(sourceId));
            if (!_state.Sinks.TryGetValue(sinkId, out var sink))
                throw new ArgumentException($"unknown sink ID '{sinkId}'", nameof(sinkId));

            if (map.OutputChannels != sink.Sink.Format.Channels)
                throw new InvalidOperationException(
                    $"map outputs {map.OutputChannels} channels but sink '{sinkId}' expects {sink.Sink.Format.Channels}");
            if (map.RequiredInputChannels > src.Source.Format.Channels)
                throw new InvalidOperationException(
                    $"map requires {map.RequiredInputChannels} input channels but source '{sourceId}' has {src.Source.Format.Channels}");

            var route = new Route(sourceId, sinkId, map, gain);
            var existing = FindRouteIndex(_state.Routes, sourceId, sinkId);
            var newRoutes = existing >= 0
                ? _state.Routes.SetItem(existing, route)
                : _state.Routes.Add(route);
            Volatile.Write(ref _state, _state with { Routes = newRoutes });
            var rk = RouteGainDictionaryKey(sourceId, sinkId);
            _routeTargetGains[rk] = gain;
            // Brand-new route starts at its target gain (no ramp on first chunk).
            // Re-adding an existing route is treated as a fresh registration —
            // user explicitly replaced the route, no fade.
            _currentGains[rk] = gain;
        }
    }

    /// <summary>Removes the route between <paramref name="sourceId"/> and <paramref name="sinkId"/>. Returns false if no such route existed.</summary>
    public bool RemoveRoute(string sourceId, string sinkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        lock (_gate)
        {
            var idx = FindRouteIndex(_state.Routes, sourceId, sinkId);
            if (idx < 0) return false;
            Volatile.Write(ref _state, _state with { Routes = _state.Routes.RemoveAt(idx) });
            var rk = RouteGainDictionaryKey(sourceId, sinkId);
            _currentGains.TryRemove(rk, out _);
            _routeTargetGains.TryRemove(rk, out _);
            return true;
        }
    }

    /// <summary>
    /// Updates the target gain on an existing route. The next chunk linearly
    /// interpolates from the previously-applied gain to <paramref name="gain"/>
    /// across its samples (sample-accurate, click-free fade). Subsequent
    /// chunks then run at the new gain. Throws if the route doesn't exist.
    /// </summary>
    public void SetRouteGain(string sourceId, string sinkId, float gain)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        lock (_gate)
        {
            var idx = FindRouteIndex(_state.Routes, sourceId, sinkId);
            if (idx < 0)
                throw new InvalidOperationException($"no route exists from '{sourceId}' to '{sinkId}'");
            _routeTargetGains[RouteGainDictionaryKey(sourceId, sinkId)] = gain;
        }
    }

    private static string RouteGainDictionaryKey(string sourceId, string sinkId) =>
        string.Concat(sourceId, '\u001f', sinkId);

    private static int FindRouteIndex(ImmutableArray<Route> routes, string sourceId, string sinkId)
    {
        for (var i = 0; i < routes.Length; i++)
            if (routes[i].SourceId == sourceId && routes[i].SinkId == sinkId)
                return i;
        return -1;
    }

    // --- inspection --------------------------------------------------------

    public IReadOnlyCollection<string> SourceIds
    {
        get { lock (_gate) return _state.Sources.Keys.ToArray(); }
    }
    public IReadOnlyCollection<string> SinkIds
    {
        get { lock (_gate) return _state.Sinks.Keys.ToArray(); }
    }
    public IReadOnlyList<Route> Routes
    {
        get { lock (_gate) return _state.Routes; }
    }

    /// <summary>Per-sink stats (chunks enqueued, processed, dropped). Useful for diagnosing throughput.</summary>
    public SinkPumpStats GetPumpStats(string sinkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        lock (_gate)
        {
            if (!_state.Sinks.TryGetValue(sinkId, out var entry))
                throw new ArgumentException($"unknown sink ID '{sinkId}'", nameof(sinkId));
            return entry.Pump.Stats;
        }
    }

    /// <summary>Try to resolve a live sink instance by id (for capability checks outside the router).</summary>
    public bool TryGetSink(string sinkId, out IAudioSink? sink)
    {
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        lock (_gate)
        {
            if (_state.Sinks.TryGetValue(sinkId, out var entry))
            {
                sink = entry.Sink;
                return true;
            }
        }

        sink = null;
        return false;
    }

    /// <summary>
    /// Point <see cref="SinkSlavedRouterClock"/> pacing at <paramref name="sinkId"/>.
    /// Safe while the router is running — the next <see cref="IRouterClock.WaitForNextChunk"/> uses the new sink.
    /// </summary>
    public void RetargetSlaveClock(string sinkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        lock (_gate)
        {
            if (!_state.Sinks.TryGetValue(sinkId, out var entry))
                throw new ArgumentException($"unknown sink ID '{sinkId}'", nameof(sinkId));
            if (entry.Sink is not IClockedSink)
                throw new ArgumentException($"sink '{sinkId}' does not implement IClockedSink", nameof(sinkId));

            _slaveClockSinkId = sinkId;
            _clock = new SinkSlavedRouterClock(_sampleRate, _chunkSamples, () => ResolveClockedSink(sinkId));
        }
    }

    // --- clocking ----------------------------------------------------------

    /// <summary>
    /// Replace the active <see cref="IRouterClock"/>. Only safe while the
    /// router is stopped — call before <see cref="Start"/> or after
    /// <see cref="Stop"/>.
    /// </summary>
    public void SetClock(IRouterClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (_gate)
        {
            if (_isRunning)
                throw new InvalidOperationException("cannot replace clock while router is running");
            _slaveClockSinkId = null;
            _clock = clock;
        }
    }

    /// <summary>
    /// Moves the router's nominal sample rate to <paramref name="newSampleRate"/> while stopped.
    /// Every registered source and sink must already use that rate. Rebuilds the wall or slaved clock.
    /// </summary>
    public void ReconfigureSampleRate(int newSampleRate)
    {
        if (newSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(newSampleRate));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning)
                throw new InvalidOperationException("ReconfigureSampleRate requires a stopped router.");

            foreach (var kv in _state.Sources)
            {
                if (kv.Value.Source.Format.SampleRate != newSampleRate)
                    throw new InvalidOperationException(
                        $"source '{kv.Key}' is {kv.Value.Source.Format.SampleRate} Hz; all sources must already match {newSampleRate} Hz before reconfiguration.");
            }

            foreach (var kv in _state.Sinks)
            {
                if (kv.Value.Sink.Format.SampleRate != newSampleRate)
                    throw new InvalidOperationException(
                        $"sink '{kv.Key}' is {kv.Value.Sink.Format.SampleRate} Hz; all sinks must already match {newSampleRate} Hz before reconfiguration.");
            }

            _sampleRate = newSampleRate;
            if (_slaveClockSinkId is { } sid)
            {
                if (!_state.Sinks.ContainsKey(sid))
                    throw new InvalidOperationException($"slaved sink '{sid}' is no longer registered.");
                _clock = new SinkSlavedRouterClock(newSampleRate, _chunkSamples, () => ResolveClockedSink(sid));
            }
            else if (_clock is WallClockRouterClock)
            {
                _clock = new WallClockRouterClock(newSampleRate, _chunkSamples);
            }
            else
            {
                throw new InvalidOperationException(
                    "ReconfigureSampleRate: clock must be WallClockRouterClock (default) or SinkSlavedRouterClock from SlaveTo / RetargetSlaveClock. Install a known clock with SetClock first.");
            }
        }
    }

    /// <summary>
    /// Slave the router's pacing to the named sink, which must implement
    /// <see cref="IClockedSink"/>. If that sink is later removed, the clock
    /// transparently falls back to a wall-clock impl. Only safe while
    /// stopped.
    /// </summary>
    public void SlaveTo(string sinkId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sinkId);
        lock (_gate)
        {
            if (_isRunning)
                throw new InvalidOperationException("cannot slave clock while router is running");

            if (!_state.Sinks.TryGetValue(sinkId, out var entry))
                throw new ArgumentException($"unknown sink ID '{sinkId}'", nameof(sinkId));
            if (entry.Sink is not IClockedSink)
                throw new ArgumentException($"sink '{sinkId}' does not implement IClockedSink", nameof(sinkId));

            _slaveClockSinkId = sinkId;
            _clock = new SinkSlavedRouterClock(_sampleRate, _chunkSamples, () => ResolveClockedSink(sinkId));
        }
    }

    private IClockedSink? ResolveClockedSink(string sinkId)
    {
        var snapshot = Volatile.Read(ref _state);
        return snapshot.Sinks.TryGetValue(sinkId, out var entry) ? entry.Sink as IClockedSink : null;
    }

    // --- lifecycle ---------------------------------------------------------

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning) return;

            CompletedNaturally = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _clock.Reset();
            _thread = new Thread(() => RunLoop(token))
            {
                IsBackground = true,
                Name = "AudioRouter",
                Priority = ThreadPriority.AboveNormal,
            };
            _isRunning = true;
            _thread.Start();
        }
    }

    /// <summary>
    /// Stops the run loop and drains pump queues so every chunk produced before
    /// <see cref="Stop"/> reaches its sink. Use <see cref="Pause"/> instead for
    /// "stop now and go silent immediately."
    /// </summary>
    public void Stop(CancellationToken cancellationToken = default) =>
        StopInternal(drain: true, flushAfterAbandon: false, cancellationToken);

    /// <summary>
    /// Immediate-silence stop: tears the run loop down, abandons any audio
    /// queued in the per-sink pumps, and calls <see cref="IFlushableSink.Flush"/>
    /// on any sink that implements it. Use <see cref="Resume"/> (alias for
    /// <see cref="Start"/>) to continue. Routes, sources, sinks, and the
    /// router clock are preserved across the pause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sink snapshots for <see cref="IFlushableSink.Flush"/> are taken in the same critical section
    /// that stops the run loop, so the list matches the graph that was active for the final mixed
    /// chunk (still invoke <see cref="Pause"/> from the same synchronization domain that owns routing
    /// if you mutate sinks while paused).
    /// </para>
    /// </remarks>
    public void Pause()
    {
        if (!IsRunning) return;
        StopInternal(drain: false, flushAfterAbandon: true, CancellationToken.None);
    }

    /// <summary>Alias for <see cref="Start"/>. Reads as a pair with <see cref="Pause"/>.</summary>
    /// <remarks>
    /// <para>
    /// The first chunk after a pause may be silence‑padded where sources emit
    /// short reads (see run‑loop scratch pad); this is usually inaudible.
    /// </para>
    /// <para>
    /// For seek/resume-heavy hosts using FFmpeg file decoders, a short gap can
    /// come from resampler / decoder internal queues: before <see cref="Start"/>,
    /// you may <see cref="ISeekableSource.Seek"/> to the current playhead (same
    /// timestamp) to reset converter state, or perform an application-level
    /// prebuffer pass so the first mixed chunk is full.
    /// </para>
    /// </remarks>
    public void Resume() => Start();

    /// <summary>
    /// Coordinated source seek: pauses the router (immediate silence), seeks
    /// the named source, then resumes. Throws if the source isn't registered
    /// or doesn't implement <see cref="ISeekableSource"/>. Pair with
    /// <c>mediaClock.Seek(position)</c> on your side to keep the visible
    /// playhead in sync.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pauses the <strong>entire</strong> router, so every sink goes silent during
    /// the seek — other routed sources briefly gap too. Prefer direct
    /// <see cref="ISeekableSource.Seek"/> if you must avoid startling other mixes.
    /// </para>
    /// </remarks>
    public void SeekSource(string sourceId, TimeSpan position)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));

        ISeekableSource seekable;
        lock (_gate)
        {
            if (!_state.Sources.TryGetValue(sourceId, out var entry))
                throw new ArgumentException($"unknown source ID '{sourceId}'", nameof(sourceId));
            if (entry.Source is not ISeekableSource s)
                throw new InvalidOperationException($"source '{sourceId}' does not implement ISeekableSource");
            seekable = s;
        }

        var wasRunning = IsRunning;
        if (wasRunning) Pause();
        seekable.Seek(position);
        if (wasRunning) Resume();
    }

    private void StopInternal(bool drain, bool flushAfterAbandon, CancellationToken cancellationToken = default)
    {
        Thread? toJoin;
        CancellationTokenSource? toDispose;
        SinkPump[] activePumps;
        IAudioSink[]? sinksForFlush = null;
        lock (_gate)
        {
            if (!_isRunning) return;
            toDispose = _cts;
            toJoin = _thread;
            _cts = null;
            _thread = null;
            _isRunning = false;
            activePumps = [.. _state.Sinks.Values.Select(e => e.Pump)];
            if (flushAfterAbandon)
                sinksForFlush = [.. _state.Sinks.Values.Select(e => e.Sink)];
        }
        toDispose?.Cancel();
        CooperativePlaybackJoin.JoinThread(toJoin, TimeSpan.FromSeconds(2), cancellationToken);
        toDispose?.Dispose();

        foreach (var p in activePumps)
        {
            if (drain) p.WaitForIdle(TimeSpan.FromSeconds(1), cancellationToken);
            else p.AbandonQueue();
        }
        while (_pumpsAwaitingDispose.TryDequeue(out var pending)) pending.Dispose();

        if (sinksForFlush is null) return;

        foreach (var s in sinksForFlush)
        {
            if (s is IFlushableSink f)
            {
                try { f.Flush(); }
                catch (Exception ex)
                {
                    if (_log is { } l)
                        l.LogError(ex, "IFlushableSink.Flush failed");
                    else
                        MediaDiagnostics.LogError(ex, "AudioRouter Pause Flush");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        lock (_gate)
        {
            foreach (var (_, entry) in _state.Sinks)
            {
                try
                {
                    entry.Pump.Dispose();
                }
#if DEBUG
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, "AudioRouter.Dispose: SinkPump.Dispose");
                }
#else
                catch
                {
                    // best-effort shutdown
                }
#endif
            }

            _state = RouterState.Empty;
        }
    }

    private void RaiseSinkErrored(string sinkId, Exception ex)
    {
        SinkErrored?.Invoke(this, new AudioRouterSinkErrorEventArgs(sinkId, ex));
        if (_log is { } l)
            l.LogError(ex, "Sink {SinkId} Submit failed", sinkId);
        else
            MediaDiagnostics.LogError(ex, $"AudioRouter sink '{sinkId}' Submit");
    }

    private void RaisePumpPressure(string sinkId, long droppedTotal)
    {
        PumpPressure?.Invoke(this, new AudioRouterPumpPressureEventArgs(sinkId, droppedTotal));
        if (_log is { } l && l.IsEnabled(LogLevel.Trace))
            l.LogTrace("Sink {SinkId} audio pump drop (running total {Dropped})", sinkId, droppedTotal);
    }

    // --- inner loop --------------------------------------------------------

    private void RunLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Tear down any pumps that were detached on the previous iteration.
                // By now the previous chunk's enqueues are flushed (we're at the top
                // of a new iteration), so the pump is safe to dispose.
                while (_pumpsAwaitingDispose.TryDequeue(out var p)) p.Dispose();

                if (!_clock.WaitForNextChunk(token)) break;

                // Snapshot the immutable state at chunk start. Concurrent mutations
                // replace the whole RouterState atomically; this loop sees a
                // consistent view per chunk.
                var snapshot = Volatile.Read(ref _state);

                // "Empty source set" means the router never auto-stops — it just
                // keeps producing silence. With sources present, we stop when every
                // last one is exhausted.
                var keepRunning = snapshot.Sources.IsEmpty;
                foreach (var (_, src) in snapshot.Sources)
                {
                    var read = src.Source.ReadInto(src.Scratch);
                    if (read < src.Scratch.Length)
                        src.Scratch.AsSpan(read).Clear(); // silence-pad partial reads
                    if (!src.Source.IsExhausted) keepRunning = true;
                }

                // Per-sink working buffers come from each pump's free-pool. The
                // router writes the mixed audio directly into them and Commit()s
                // by reference — no second copy on the producer thread.
                foreach (var (_, sink) in snapshot.Sinks)
                    Array.Clear(sink.Pump.WorkingBuffer);

                foreach (var route in snapshot.Routes)
                {
                    if (!snapshot.Sources.TryGetValue(route.SourceId, out var src)) continue;
                    if (!snapshot.Sinks.TryGetValue(route.SinkId, out var sink)) continue;

                    var rk = RouteGainDictionaryKey(route.SourceId, route.SinkId);
                    var fromGain = _currentGains.GetValueOrDefault(rk, route.Gain);
                    var toGain = _routeTargetGains.TryGetValue(rk, out var tg) ? tg : route.Gain;
                    ApplyRoute(src.Scratch, src.Source.Format.Channels,
                               sink.Pump.WorkingBuffer, sink.Sink.Format.Channels,
                               route.Map, fromGain, toGain, _chunkSamples);
                    if (fromGain != toGain) _currentGains[rk] = toGain;
                }

                // Publish each sink's mixed buffer to its pump (zero-copy hand-off);
                // the drainer thread does the actual Submit so the run loop is
                // never blocked by a slow sink.
                foreach (var (_, sink) in snapshot.Sinks)
                    sink.Pump.Commit();

                Interlocked.Increment(ref _chunksProduced);

                if (!keepRunning)
                {
                    CompletedNaturally = true;
                    lock (_gate) _isRunning = false;
                    break;
                }
            }
        }
        finally
        {
            // When the loop exits on natural EOF, StopInternal was never called — release CTS,
            // clear the thread field, drain pumps, and flush hardware sinks so Pause/Dispose and
            // hosts (e.g. smoke tools) can shut down promptly.
            FinishRunLoopThreadLifetime(naturalEof: CompletedNaturally);
        }
    }

    /// <summary>
    /// Runs on the router thread when <see cref="RunLoop"/> exits for any reason, unless
    /// <see cref="StopInternal"/> already captured <see cref="_cts"/> (then this is a no-op).
    /// </summary>
    private void FinishRunLoopThreadLifetime(bool naturalEof)
    {
        CancellationTokenSource? cts;
        SinkPump[] pumps;
        IAudioSink[]? sinksForFlush = null;
        lock (_gate)
        {
            cts = _cts;
            if (cts is null)
                return;

            _cts = null;
            _thread = null;
            _isRunning = false;
            pumps = [.. _state.Sinks.Values.Select(e => e.Pump)];
            if (naturalEof)
                sinksForFlush = [.. _state.Sinks.Values.Select(e => e.Sink)];
        }

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* racing StopInternal */ }
        cts.Dispose();

        foreach (var p in pumps)
        {
            try { p.WaitForIdle(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (Exception ex)
            {
                if (_log is { } l)
                    l.LogWarning(ex, "AudioRouter: pump WaitForIdle after run loop exit");
                else
                    MediaDiagnostics.LogWarning("AudioRouter: pump WaitForIdle after run loop exit: {0}", ex.Message);
            }
        }

        if (sinksForFlush is null) return;

        foreach (var s in sinksForFlush)
        {
            if (s is IFlushableSink f)
            {
                try { f.Flush(); }
                catch (Exception ex)
                {
                    if (_log is { } l)
                        l.LogError(ex, "IFlushableSink.Flush after natural router stop");
                    else
                        MediaDiagnostics.LogError(ex, "AudioRouter natural EOF Flush");
                }
            }
        }
    }

    internal static void ApplyRoute(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        ChannelMap map, float fromGain, float toGain, int samplesPerChannel)
    {
        var profileRoutes = ChannelRouteMixProfiling.ShouldProfileApplyRoute();

        // Both ends silent — nothing to mix in.
        if (fromGain == 0f && toGain == 0f) return;

        // Steady-state: same gain at both ends, take the constant-gain fast path.
        if (fromGain == toGain)
        {
            if (ChannelMap.TryAccumulateStereoFullSilenceStereoInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateStereoIdentityInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateStereoDupSingleChannelInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateStereoSilenceOrZeroDupInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateMonoSilenceOrZeroDupInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateMonoDupStereoInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateMonoDupNInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateStereoDuplexWideInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateStereoDuplexWideSwappedInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateStereoToNInterleavedSwapped(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulateStereoToNInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (fromGain == 1.0f)
            {
                if (profileRoutes)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    map.ApplyAdditive(src, srcChannels, dst, samplesPerChannel);
                    ChannelRouteMixProfiling.RecordApplyAdditive(Stopwatch.GetTimestamp() - t0);
                }
                else
                    map.ApplyAdditive(src, srcChannels, dst, samplesPerChannel);
                return;
            }

            long tUniform = 0;
            if (profileRoutes)
                tUniform = Stopwatch.GetTimestamp();
            for (var s = 0; s < samplesPerChannel; s++)
            {
                var srcBase = s * srcChannels;
                var dstBase = s * dstChannels;
                for (var oc = 0; oc < dstChannels; oc++)
                {
                    var ic = map[oc];
                    if (ic >= 0) dst[dstBase + oc] += src[srcBase + ic] * fromGain;
                }
            }

            if (profileRoutes)
                ChannelRouteMixProfiling.RecordScalarUniformGain(Stopwatch.GetTimestamp() - tUniform);
            return;
        }

        // Linear ramp from fromGain to toGain across the chunk. Gain is
        // evaluated at sample-mid (s + 0.5) so the average across the chunk
        // is exactly (fromGain + toGain) / 2 — no DC drift.
        var step = (toGain - fromGain) / samplesPerChannel;
        var gain = fromGain + step * 0.5f;
        long tRamp = 0;
        if (profileRoutes)
            tRamp = Stopwatch.GetTimestamp();
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcBase = s * srcChannels;
            var dstBase = s * dstChannels;
            for (var oc = 0; oc < dstChannels; oc++)
            {
                var ic = map[oc];
                if (ic >= 0) dst[dstBase + oc] += src[srcBase + ic] * gain;
            }
            gain += step;
        }

        if (profileRoutes)
            ChannelRouteMixProfiling.RecordScalarRamp(Stopwatch.GetTimestamp() - tRamp);
    }

    /// <summary>One side of a routing connection.</summary>
    public sealed record Route(string SourceId, string SinkId, ChannelMap Map, float Gain);

    /// <summary>Snapshot of pump throughput stats.</summary>
    public readonly record struct SinkPumpStats(long Enqueued, long Processed, long Dropped, int PumpCapacityChunks);

    private sealed record SourceEntry(string Id, IAudioSource Source, float[] Scratch);
    private sealed record SinkEntry(string Id, IAudioSink Sink, SinkPump Pump);

    private sealed record RouterState(
        ImmutableDictionary<string, SourceEntry> Sources,
        ImmutableDictionary<string, SinkEntry> Sinks,
        ImmutableArray<Route> Routes)
    {
        public static readonly RouterState Empty = new(
            ImmutableDictionary<string, SourceEntry>.Empty,
            ImmutableDictionary<string, SinkEntry>.Empty,
            ImmutableArray<Route>.Empty);
    }

    // --- per-sink pump -----------------------------------------------------

    /// <summary>
    /// Bounded SPSC chunk queue + drainer thread per sink. Producer is the
    /// router's run loop; consumer is the pump's <see cref="DrainLoop"/> which
    /// calls <see cref="IAudioSink.Submit"/>. On overflow the oldest queued
    /// chunk is dropped (sink can't keep up).
    /// </summary>
    /// <remarks>
    /// Zero-copy producer path: the router fills <see cref="WorkingBuffer"/>
    /// in place and calls <see cref="Commit"/> to publish it. The pump
    /// rotates the working buffer with one from its free-pool on every
    /// commit; if both the pool and consumer queue are empty, the mixed chunk
    /// is dropped in place (no fresh allocation) and the same buffer is reused for the next mix.
    /// </remarks>
    private sealed class SinkPump : IDisposable
    {
        private readonly AudioRouter _router;
        private readonly string _sinkId;
        private readonly IAudioSink _sink;
        private readonly BlockingCollection<float[]> _ready;
        private readonly ConcurrentQueue<float[]> _free = new();
        private readonly Thread _thread;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _floatsPerChunk;
        private readonly int _pumpCapacityChunks;
        private float[] _working;
        private long _enqueued;
        private long _processed;
        private long _dropped;
        private volatile bool _disposed;

        public SinkPumpStats Stats => new(
            Volatile.Read(ref _enqueued),
            Volatile.Read(ref _processed),
            Volatile.Read(ref _dropped),
            _pumpCapacityChunks);

        /// <summary>
        /// Producer-thread scratch — the buffer the router currently writes
        /// into. <see cref="Commit"/> publishes it and rotates a fresh one in.
        /// Only the producer thread should access this.
        /// </summary>
        public float[] WorkingBuffer => _working;

        public SinkPump(AudioRouter router, IAudioSink sink, int capacityChunks, int floatsPerChunk, string sinkId)
        {
            _router = router;
            _sinkId = sinkId;
            _sink = sink;
            _floatsPerChunk = floatsPerChunk;
            _pumpCapacityChunks = capacityChunks;
            _ready = new BlockingCollection<float[]>(boundedCapacity: capacityChunks);
            for (var i = 0; i < capacityChunks; i++)
                _free.Enqueue(new float[floatsPerChunk]);
            _working = new float[floatsPerChunk];

            _thread = new Thread(() => DrainLoop(_cts.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = $"SinkPump:{sinkId}",
            };
            _thread.Start();
        }

        private long RecordDrop()
        {
            var total = Interlocked.Increment(ref _dropped);
            _router.RaisePumpPressure(_sinkId, total);
            return total;
        }

        /// <summary>
        /// Publish <see cref="WorkingBuffer"/> to the consumer queue and
        /// rotate in a fresh buffer for the next chunk. On pool exhaustion
        /// (consumer behind), evict the oldest queued chunk to make room
        /// and count the drop.
        /// </summary>
        public void Commit()
        {
            if (_disposed) return;

            var buf = _working;
            if (!_free.TryDequeue(out var next))
            {
                if (_ready.TryTake(out next!))
                    RecordDrop();
                else
                {
                    // Pool and consumer queue are empty: drop this mixed chunk and
                    // reuse the same buffer next iteration — avoids allocating on the producer thread.
                    RecordDrop();
                    return;
                }
            }
            _working = next;

            try
            {
                if (_ready.TryAdd(buf))
                {
                    Interlocked.Increment(ref _enqueued);
                }
                else
                {
                    _free.Enqueue(buf);
                    RecordDrop();
                }
            }
            catch (ObjectDisposedException)   { RecordDrop(); }
            catch (InvalidOperationException) { RecordDrop(); }
        }

        /// <summary>
        /// Discard every chunk currently queued. Does <em>not</em> interrupt an
        /// in-flight <see cref="IAudioSink.Submit"/> on the drainer thread —
        /// the caller should follow with <see cref="WaitForIdle"/> if they
        /// need quiescence guarantees.
        /// </summary>
        public void AbandonQueue()
        {
            while (_ready.TryTake(out var buf))
            {
                _free.Enqueue(buf);
                RecordDrop();
            }
        }

        /// <summary>
        /// Block until <c>processed == enqueued</c> (drainer has caught up) or
        /// <paramref name="timeout"/> elapses.
        /// </summary>
        public void WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Volatile.Read(ref _processed) < Volatile.Read(ref _enqueued))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Environment.TickCount64 > deadline) return;
                Thread.Sleep(1);
            }
        }

        private void DrainLoop(CancellationToken token)
        {
            try
            {
                foreach (var buf in _ready.GetConsumingEnumerable(token))
                {
                    try { _sink.Submit(buf); }
                    catch (Exception ex) { _router.RaiseSinkErrored(_sinkId, ex); }
                    Interlocked.Increment(ref _processed);
                    _free.Enqueue(buf);
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _ready.CompleteAdding(); }
#if DEBUG
            catch (ObjectDisposedException ex) { MediaDiagnostics.LogError(ex, "SinkPump.CompleteAdding"); }
#else
            catch (ObjectDisposedException) { /* queue may already be completed or disposed */ }
#endif

            CooperativePlaybackJoin.JoinThread(_thread, TimeSpan.FromSeconds(2));
            if (_thread.IsAlive)
            {
                try { _cts.Cancel(); }
                catch (Exception ex)
                {
#if DEBUG
                    MediaDiagnostics.LogError(ex, "SinkPump.Dispose: CancellationTokenSource.Cancel");
#else
                    _ = ex;
#endif
                }

                CooperativePlaybackJoin.JoinThread(_thread, TimeSpan.FromSeconds(1));
            }

            _ready.Dispose();
            _cts.Dispose();
        }
    }
}
