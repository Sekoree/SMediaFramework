using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Core.Audio;

/// <summary>
/// Routes packed-float audio between any number of <see cref="IAudioSource"/>s
/// and <see cref="IAudioOutput"/>s. Each connection is an explicit
/// <see cref="Route"/>: a source ID, a output ID, a mandatory
/// <see cref="ChannelMap"/>, and a per-route <see cref="Route.Gain"/>. Outputs
/// sum contributions from every route that targets them.
/// </summary>
/// <remarks>
/// <para>
/// There is no central mixer bus. Routes are direct. To play one source on
/// two outputs differently, register two routes from the same source ID to
/// each output ID with their own channel maps and gains. To mix two sources
/// into one output, register two routes both targeting the same output — they
/// sum.
/// </para>
/// <para>
/// All sources and outputs must agree on the router's nominal sample rate for
/// routing and mixing. While <see cref="IsRunning"/> is <see langword="true"/>, the nominal rate is fixed unless the host calls
/// <see cref="ReconfigureSampleRateWhileRunning"/> after every registered source and output already reports the new rate on
/// <see cref="AudioFormat.SampleRate"/> (see that method's remarks). When stopped, <see cref="ReconfigureSampleRate"/> performs the same
/// validation and clock rebuild without requiring a running loop. Optional per-leaf wrappers (e.g. FFmpeg
/// <c>AdaptiveRateAudioOutput</c>) may apply a tiny rate tweak only on the path
/// into that output without changing the router's graph rate.
/// </para>
/// <para>
/// The graph is <strong>fully dynamic</strong> — sources, outputs, and routes
/// can be added or removed at any time, including while the router is
/// running. Updates take effect on the next chunk. Mutations swap an immutable
/// <see cref="RouterState"/> under a lock; the run loop reads it without
/// blocking.
/// </para>
/// <para>
/// <strong>Per-output threading</strong>: every output gets its own bounded chunk
/// queue plus a drainer thread that calls <see cref="IAudioOutput.Submit"/>.
/// Slow or blocking outputs (e.g. a clocked NDI sender) cannot throttle the router
/// or any other output; they only fill their own queue and eventually drop oldest chunks.
/// Queue depth defaults to the router constructor's <c>pumpCapacityChunks</c>;
/// <see cref="AddOutput(IAudioOutput, string?, int?)"/> can override depth per output.
/// </para>
/// <para>
/// <strong>Pacing</strong>: the router is paced by an <see cref="IRouterClock"/>.
/// Default is <see cref="WallClockRouterClock"/>; call <see cref="SlaveTo"/>
/// to bind production to a specific <see cref="IClockedOutput"/> (typically a
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
/// <strong>Multi-output drift</strong>: only the output wired via <see cref="SlaveTo"/> paces the router; every
/// other output runs off its own physical clock and drifts at typical ±50 ppm. Subscribe to <see cref="PumpPressure"/>
/// to react when a non-slaved output starts dropping; wrap it in the FFmpeg <c>AdaptiveRateAudioOutput</c> for automatic
/// per-output resample-driven easing. A single coordinated master-ppm policy and synchronized cross-output drop/repeat
/// are <strong>not</strong> implemented here. See <c>Doc/MediaFramework-Architecture.md</c> for the in-depth
/// discussion plus the optional <c>MF_MEDIA_PROFILE_CHANNEL_MAP</c> profiling switch.
/// </para>
/// </remarks>
public sealed class AudioRouter : IDisposable
{
    private int _sampleRate;
    private readonly int _chunkSamples;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<OutputPump> _pumpsAwaitingDispose = new();
    /// <summary>Per-route "currently applied" gain. <see cref="Route.Gain"/> is the target; this tracks the value we ramped to last chunk.</summary>
    private readonly ConcurrentDictionary<string, float> _currentGains = new();
    /// <summary>Concurrent target gain for routing (hot updates from <see cref="SetRouteGain"/> without rewriting <see cref="RouterState"/>).</summary>
    private readonly ConcurrentDictionary<string, float> _routeTargetGains = new();
    private readonly ILogger? _log;
    private readonly int _pumpCapacityChunks;

    private RouterState _state = RouterState.Empty;
    private IRouterClock _clock;
    /// <summary>When pacing uses <see cref="OutputSlavedRouterClock"/>, the output id passed to <see cref="SlaveTo"/> / <see cref="RetargetSlaveClock"/>.</summary>
    private string? _slaveClockOutputId;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _disposed;
    private long _chunksProduced;
    private int _idCounter;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Audio.AudioRouter");

    /// <summary>
    /// Nominal mix sample rate in Hz.
    /// </summary>
    /// <remarks>
    /// While <see cref="IsRunning"/> is <see langword="true"/>, use <see cref="ReconfigureSampleRateWhileRunning"/> to move this value
    /// (every source/output must already match the new rate). When stopped, <see cref="ReconfigureSampleRate"/> applies the same rule.
    /// For per-leaf drift at a fixed nominal graph rate without retuning the router, wrap outputs with FFmpeg <c>AdaptiveRateAudioOutput</c>.
    /// </remarks>
    public int SampleRate => _sampleRate;
    public int ChunkSamples => _chunkSamples;

    public bool IsRunning { get { lock (_gate) return _isRunning; } }
    /// <summary>True after the router stopped on its own because every source was exhausted.</summary>
    public bool CompletedNaturally { get; private set; }
    public long ChunksProduced => Volatile.Read(ref _chunksProduced);

    /// <summary>The active <see cref="IRouterClock"/>. Swapped via <see cref="SlaveTo"/> / <see cref="SetClock"/> — only safe while stopped.</summary>
    public IRouterClock Clock { get { lock (_gate) return _clock; } }

    /// <summary>Raised when a output throws from <see cref="IAudioOutput.Submit"/> (non-fatal; pump keeps running).</summary>
    public event EventHandler<AudioRouterOutputErrorEventArgs>? OutputErrored;

    /// <summary>Raised when a output pump drops chunks — sustained drops mean the output is behind.</summary>
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
    /// <param name="autoResample">
    /// When <c>true</c> and <paramref name="source"/>'s rate doesn't match the router's nominal rate,
    /// the source is transparently wrapped via <see cref="AudioRouterAutoResample.SourceWrapper"/>
    /// (installed by <c>S.Media.FFmpeg</c>'s <c>FFmpegRuntime.EnsureInitialized</c>). The router owns
    /// and disposes that wrapper, but it does not assume ownership of the caller's original source;
    /// wrapper factories must make their own inner-source ownership policy explicit. The default
    /// FFmpeg wrapper deliberately leaves the original source caller-owned. Throws
    /// <see cref="InvalidOperationException"/> when a rate mismatch is observed but no resampler
    /// factory is registered.
    /// </param>
    public string AddSource(IAudioSource source, string? id = null, bool autoResample = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        source.Format.Validate(nameof(source));
        IDisposable? ownedWrapper = null;
        if (source.Format.SampleRate != _sampleRate)
        {
            if (!autoResample)
                throw new InvalidOperationException(
                    $"source sample rate {source.Format.SampleRate} doesn't match router's {_sampleRate} (pass autoResample: true to wrap, or resample upstream)");
            if (AudioRouterAutoResample.SourceWrapper is not { } factory)
                throw new InvalidOperationException(
                    "AudioRouter.AddSource: autoResample requested but no resampler factory is installed " +
                    "(reference S.Media.FFmpeg and call FFmpegRuntime.EnsureInitialized() to install the default swresample wrapper).");
            var wrapped = factory(source, _sampleRate);
            wrapped.Format.Validate(nameof(source));
            if (wrapped.Format.SampleRate != _sampleRate)
                throw new InvalidOperationException(
                    $"AudioRouter.AddSource: resampler wrapper produced format {wrapped.Format} which still doesn't match router rate {_sampleRate}.");
            source = wrapped;
            ownedWrapper = wrapped as IDisposable;
        }

        lock (_gate)
        {
            id ??= $"__auto_src_{++_idCounter}";
            ArgumentException.ThrowIfNullOrEmpty(id);
            if (_state.Sources.ContainsKey(id))
                throw new ArgumentException($"source ID '{id}' is already registered", nameof(id));

            var entry = new SourceEntry(id, source, new float[_chunkSamples * source.Format.Channels], ownedWrapper);
            Volatile.Write(ref _state, _state with { Sources = _state.Sources.Add(id, entry) });
            Trace.LogDebug("AddSource: id={SourceId} type={SourceType} format={Format} wrapped={Wrapped}",
                id, source.GetType().Name, source.Format, ownedWrapper is not null);
            return id;
        }
    }

    /// <summary>Removes a source and any routes that reference it. Returns false if no source had that ID.</summary>
    public bool RemoveSource(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        IDisposable? ownedWrapper;
        lock (_gate)
        {
            if (!_state.Sources.TryGetValue(id, out var entry)) return false;
            ownedWrapper = entry.OwnedWrapper;
            foreach (var r in _state.Routes)
                if (r.SourceId == id)
                {
                    _currentGains.TryRemove(r.RouteId, out _);
                    _routeTargetGains.TryRemove(r.RouteId, out _);
                }
            Volatile.Write(ref _state, _state with
            {
                Sources = _state.Sources.Remove(id),
                Routes = _state.Routes.RemoveAll(r => r.SourceId == id),
            });
        }
        if (ownedWrapper is not null)
        {
            MediaDiagnostics.SwallowDisposeErrors(ownedWrapper.Dispose, "AudioRouter.RemoveSource: owned wrapper");
        }
        return true;
    }

    /// <param name="pumpCapacityChunks">
    /// Bounded depth of this output's background chunk queue (see remarks on
    /// <see cref="AudioRouter"/>). <c>null</c> uses the router constructor default
    /// (currently <c>8</c>). When set, must be at least 2.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Latency budget</strong>. The pump queue is a worst-case staging buffer
    /// in front of the output. End-to-end producer-to-output latency in the steady state
    /// is roughly <c>pumpCapacityChunks × chunkSamples / sampleRate</c>: at the framework
    /// defaults (8 chunks × 480 samples @ 48&#160;kHz) that's about <strong>80&#160;ms</strong>,
    /// which is the slack a slow output can absorb before the router starts dropping
    /// oldest chunks. Multiply by your own <c>chunkSamples</c> / sample rate to recompute.
    /// </para>
    /// <para>
    /// <strong>Hardware outputs</strong> (anything implementing <see cref="IClockedOutput"/>,
    /// e.g. a PortAudio output) already maintain their own ring with its own dedicated
    /// latency knobs; the pump in front of them only needs to absorb router-to-output
    /// scheduling jitter. A depth of <c>2</c>–<c>4</c> (≈ 20–40&#160;ms at defaults) is
    /// typically plenty and keeps overall latency low.
    /// </para>
    /// <para>
    /// <strong>Network / non-clocked outputs</strong> (e.g. an NDI sender) want a deeper
    /// queue so a transient send stall doesn't immediately translate into a dropped chunk
    /// on the producer side. The default <c>8</c> is sized for that case; increase further
    /// if the sender is known to stall longer than 80&#160;ms.
    /// </para>
    /// <para>
    /// Auto-tuning of the default for <see cref="IClockedOutput"/> at <c>AddOutput</c> time is
    /// not implemented — callers pass <paramref name="pumpCapacityChunks"/> explicitly when
    /// they want non-default behaviour. <see cref="AudioPlayer.AddOutput"/> exposes the same
    /// knob via its <c>outputPumpCapacityChunks</c> parameter.
    /// </para>
    /// </remarks>
    public string AddOutput(IAudioOutput output, string? id = null, int? pumpCapacityChunks = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);

        output.Format.Validate(nameof(output));
        if (output.Format.SampleRate != _sampleRate)
            throw new InvalidOperationException(
                $"output sample rate {output.Format.SampleRate} doesn't match router's {_sampleRate}");

        var capacity = pumpCapacityChunks ?? _pumpCapacityChunks;
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(pumpCapacityChunks), "must be >= 2 when specified");

        lock (_gate)
        {
            id ??= $"__auto_sink_{++_idCounter}";
            ArgumentException.ThrowIfNullOrEmpty(id);
            if (_state.Outputs.ContainsKey(id))
                throw new ArgumentException($"output ID '{id}' is already registered", nameof(id));

            var floatsPerChunk = _chunkSamples * output.Format.Channels;
            var pump = new OutputPump(this, output, capacity, floatsPerChunk, id);
            var entry = new OutputEntry(id, output, pump);
            Volatile.Write(ref _state, _state with { Outputs = _state.Outputs.Add(id, entry) });
            Trace.LogDebug("AddOutput: id={SinkId} type={SinkType} format={Format} clocked={Clocked} flushable={Flushable} pumpCap={PumpCapacity}",
                id, output.GetType().Name, output.Format, output is IClockedOutput, output is IFlushableOutput, capacity);
            return id;
        }
    }

    /// <summary>
    /// Removes a output and any routes that target it. Any chunks queued in the
    /// output's pump are abandoned synchronously; an in-flight
    /// <see cref="IAudioOutput.Submit"/> on the pump's drainer thread completes
    /// (briefly waited for) before the pump's thread teardown is scheduled.
    /// Returns false if no output had that ID.
    /// </summary>
    public bool RemoveOutput(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        OutputPump? pump;
        bool wasRunning;
        lock (_gate)
        {
            if (!_state.Outputs.TryGetValue(id, out var entry)) return false;
            pump = entry.Pump;
            // Use the pre-removal Routes snapshot to know exactly which
            // (sourceId, outputId) keys belong to this output — avoids enumerating
            // the entire _currentGains dictionary while concurrent
            // AddRoute/RemoveRoute calls mutate it.
            foreach (var route in _state.Routes)
                if (route.SinkId == id)
                {
                    _currentGains.TryRemove(route.RouteId, out _);
                    _routeTargetGains.TryRemove(route.RouteId, out _);
                }
            Volatile.Write(ref _state, _state with
            {
                Outputs = _state.Outputs.Remove(id),
                Routes = _state.Routes.RemoveAll(r => r.SinkId == id),
            });
            wasRunning = _isRunning;
        }
        // Drop any pending chunks (caller asked for "stop delivering"), then
        // wait briefly for an in-flight Submit on the drainer thread to
        // complete. The next run-loop iteration sees the new state and won't
        // enqueue further; the pump's thread join is deferred so RemoveOutput
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
    /// <paramref name="outputId"/>. The route's <paramref name="map"/> describes
    /// how source channels feed output channels — its
    /// <see cref="ChannelMap.OutputChannels"/> must match the output's channel
    /// count. <paramref name="gain"/> scales the contribution before summation.
    /// Re-adding for an existing pair replaces it.
    /// </summary>
    public string AddRoute(string sourceId, string outputId, ChannelMap map, float gain = 1.0f)
    {
        var routeId = LegacyRouteId(sourceId, outputId);
        AddRoute(sourceId, outputId, routeId, map, gain);
        return routeId;
    }

    /// <summary>
    /// Phase C (§4.3.4) — add (or replace) a route under an explicit <paramref name="routeId"/>. Multiple
    /// routes may target the same <c>(source, output)</c> pair when they carry distinct ids; their
    /// per-cell contributions sum additively into the output (the run loop already iterates and
    /// accumulates every route per chunk). Re-adding the same <paramref name="routeId"/>
    /// replaces the previous route in-place (channel map + gain hard-reset, no fade — same semantics as
    /// re-registering via the legacy overload).
    /// </summary>
    public void AddRoute(string sourceId, string outputId, string routeId, ChannelMap map, float gain = 1.0f)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        ArgumentException.ThrowIfNullOrEmpty(routeId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!_state.Sources.TryGetValue(sourceId, out var src))
                throw new ArgumentException($"unknown source ID '{sourceId}'", nameof(sourceId));
            if (!_state.Outputs.TryGetValue(outputId, out var output))
                throw new ArgumentException($"unknown output ID '{outputId}'", nameof(outputId));

            if (map.OutputChannels != output.Output.Format.Channels)
                throw new InvalidOperationException(
                    $"map outputs {map.OutputChannels} channels but output '{outputId}' expects {output.Output.Format.Channels}");
            if (map.RequiredInputChannels > src.Source.Format.Channels)
                throw new InvalidOperationException(
                    $"map requires {map.RequiredInputChannels} input channels but source '{sourceId}' has {src.Source.Format.Channels}");

            var existing = FindRouteIndexById(_state.Routes, routeId);
            if (existing >= 0)
            {
                var prior = _state.Routes[existing];
                if (prior.SourceId != sourceId || prior.SinkId != outputId)
                    throw new ArgumentException(
                        $"route id '{routeId}' is already registered for ('{prior.SourceId}' -> '{prior.SinkId}'); cannot reuse for ('{sourceId}' -> '{outputId}')",
                        nameof(routeId));
            }

            var route = new Route(sourceId, outputId, routeId, map, gain);
            var newRoutes = existing >= 0
                ? _state.Routes.SetItem(existing, route)
                : _state.Routes.Add(route);
            Volatile.Write(ref _state, _state with { Routes = newRoutes });
            _routeTargetGains[routeId] = gain;
            // Brand-new route starts at its target gain (no ramp on first chunk).
            // Re-adding an existing route is treated as a fresh registration —
            // user explicitly replaced the route, no fade.
            _currentGains[routeId] = gain;
        }
    }

    /// <summary>
    /// Removes the route(s) between <paramref name="sourceId"/> and <paramref name="outputId"/>.
    /// Returns false if no such route existed.
    /// </summary>
    /// <remarks>
    /// Legacy single-route-per-pair API. If multiple routes share the pair (post Phase C per-cell matrix),
    /// removes <em>all</em> of them. Prefer <see cref="RemoveRouteById"/> when you registered the route
    /// under an explicit id.
    /// </remarks>
    public bool RemoveRoute(string sourceId, string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            var any = false;
            for (var i = _state.Routes.Length - 1; i >= 0; i--)
            {
                if (_state.Routes[i].SourceId == sourceId && _state.Routes[i].SinkId == outputId)
                {
                    var rid = _state.Routes[i].RouteId;
                    _currentGains.TryRemove(rid, out _);
                    _routeTargetGains.TryRemove(rid, out _);
                    Volatile.Write(ref _state, _state with { Routes = _state.Routes.RemoveAt(i) });
                    any = true;
                }
            }
            return any;
        }
    }

    /// <summary>Phase C (§4.3.4) — remove a single route by its registration id. Returns false when no such id.</summary>
    public bool RemoveRouteById(string routeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeId);
        lock (_gate)
        {
            var idx = FindRouteIndexById(_state.Routes, routeId);
            if (idx < 0) return false;
            Volatile.Write(ref _state, _state with { Routes = _state.Routes.RemoveAt(idx) });
            _currentGains.TryRemove(routeId, out _);
            _routeTargetGains.TryRemove(routeId, out _);
            return true;
        }
    }

    /// <summary>
    /// Updates the target gain on an existing route. The next chunk linearly
    /// interpolates from the previously-applied gain to <paramref name="gain"/>
    /// across its samples (sample-accurate, click-free fade). Subsequent
    /// chunks then run at the new gain. Throws if the route doesn't exist.
    /// </summary>
    /// <remarks>
    /// Legacy single-route-per-pair API. If multiple routes share the pair, applies the change to all
    /// of them. Prefer <see cref="SetRouteGainById"/> when targeting a specific matrix cell.
    /// </remarks>
    public void SetRouteGain(string sourceId, string outputId, float gain)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            var any = false;
            foreach (var route in _state.Routes)
            {
                if (route.SourceId == sourceId && route.SinkId == outputId)
                {
                    _routeTargetGains[route.RouteId] = gain;
                    any = true;
                }
            }
            if (!any)
                throw new InvalidOperationException($"no route exists from '{sourceId}' to '{outputId}'");
        }
    }

    /// <summary>Phase C (§4.3.4) — update gain on a specific route by its id. Click-free fade as in <see cref="SetRouteGain"/>.</summary>
    public void SetRouteGainById(string routeId, float gain)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeId);
        lock (_gate)
        {
            if (FindRouteIndexById(_state.Routes, routeId) < 0)
                throw new InvalidOperationException($"no route registered under id '{routeId}'");
            _routeTargetGains[routeId] = gain;
        }
    }

    /// <summary>Synthesized routeId for the legacy single-route-per-pair API. Keeps the
    /// pre-Phase-C key shape (US-separator-delimited) so existing dictionary entries stay
    /// interpretable and dumps still read as before.</summary>
    private static string LegacyRouteId(string sourceId, string outputId) =>
        string.Concat(sourceId, '\u001f', outputId);

    private static int FindRouteIndexById(ImmutableArray<Route> routes, string routeId)
    {
        for (var i = 0; i < routes.Length; i++)
            if (routes[i].RouteId == routeId)
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
        get { lock (_gate) return _state.Outputs.Keys.ToArray(); }
    }
    public IReadOnlyList<Route> Routes
    {
        get { lock (_gate) return _state.Routes; }
    }

    /// <summary>Per-output stats (chunks enqueued, processed, dropped). Useful for diagnosing throughput.</summary>
    public OutputPumpStats GetPumpStats(string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            if (!_state.Outputs.TryGetValue(outputId, out var entry))
                throw new ArgumentException($"unknown output ID '{outputId}'", nameof(outputId));
            return entry.Pump.Stats;
        }
    }

    /// <summary>
    /// Sums <see cref="OutputPumpStats"/> for every registered output under one lock. For operator HUD / logging — not a
    /// multi-output master clock or automatic PPM policy (see class <see cref="AudioRouter"/> remarks).
    /// </summary>
    public AudioRouterAggregatePumpStats GetAggregatePumpStats()
    {
        lock (_gate)
        {
            long totalEnqueued = 0;
            long totalProcessed = 0;
            long totalDropped = 0;
            var maxPumpCapacityChunks = 0;
            var sinkCount = 0;
            foreach (var kv in _state.Outputs)
            {
                var st = kv.Value.Pump.Stats;
                totalEnqueued += st.Enqueued;
                totalProcessed += st.Processed;
                totalDropped += st.Dropped;
                if (st.PumpCapacityChunks > maxPumpCapacityChunks)
                    maxPumpCapacityChunks = st.PumpCapacityChunks;
                sinkCount++;
            }

            return new AudioRouterAggregatePumpStats(
                totalEnqueued,
                totalProcessed,
                totalDropped,
                maxPumpCapacityChunks,
                sinkCount);
        }
    }

    /// <summary>Try to resolve a live output instance by id (for capability checks outside the router).</summary>
    public bool TryGetOutput(string outputId, out IAudioOutput? output)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            if (_state.Outputs.TryGetValue(outputId, out var entry))
            {
                output = entry.Output;
                return true;
            }
        }

        output = null;
        return false;
    }

    /// <summary>
    /// Point <see cref="OutputSlavedRouterClock"/> pacing at <paramref name="outputId"/>.
    /// Safe while the router is running — the next <see cref="IRouterClock.WaitForNextChunk"/> uses the new output.
    /// </summary>
    public void RetargetSlaveClock(string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            if (!_state.Outputs.TryGetValue(outputId, out var entry))
                throw new ArgumentException($"unknown output ID '{outputId}'", nameof(outputId));
            if (entry.Output is not IClockedOutput)
                throw new ArgumentException($"output '{outputId}' does not implement IClockedOutput", nameof(outputId));

            _slaveClockOutputId = outputId;
            _clock = new OutputSlavedRouterClock(_sampleRate, _chunkSamples, () => ResolveClockedOutput(outputId));
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
            _slaveClockOutputId = null;
            _clock = clock;
        }
    }

    /// <summary>
    /// Moves the router's nominal sample rate to <paramref name="newSampleRate"/> while stopped.
    /// Every registered source and output must already use that rate. Rebuilds the wall or slaved clock.
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
            ApplySampleRateChangeLocked(newSampleRate);
        }
    }

    /// <summary>
    /// Same contract as <see cref="ReconfigureSampleRate"/> (every registered source and output must already report
    /// <paramref name="newSampleRate"/> on <see cref="AudioFormat.SampleRate"/>), but allowed while <see cref="IsRunning"/> is
    /// <see langword="true"/>. The router lock is held for the validation and clock swap; pacing restarts from
    /// <see cref="IRouterClock.Reset"/> on the rebuilt clock so the next chunk uses the new wall duration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The router does not resample: update or replace every <see cref="IAudioSource"/> and <see cref="IAudioOutput"/> so their
    /// <see cref="AudioFormat"/> matches <paramref name="newSampleRate"/> before calling this method. Chunk length in samples
    /// (<see cref="ChunkSamples"/>) is unchanged — only the nominal Hz and pacing interval change.
    /// </para>
    /// </remarks>
    public void ReconfigureSampleRateWhileRunning(int newSampleRate)
    {
        if (newSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(newSampleRate));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isRunning)
                throw new InvalidOperationException("ReconfigureSampleRateWhileRunning requires a started router; use ReconfigureSampleRate while stopped.");
            ApplySampleRateChangeLocked(newSampleRate);
        }
    }

    private void ApplySampleRateChangeLocked(int newSampleRate)
    {
        foreach (var kv in _state.Sources)
        {
            if (kv.Value.Source.Format.SampleRate != newSampleRate)
                throw new InvalidOperationException(
                    $"source '{kv.Key}' is {kv.Value.Source.Format.SampleRate} Hz; all sources must already match {newSampleRate} Hz before reconfiguration.");
        }

        foreach (var kv in _state.Outputs)
        {
            if (kv.Value.Output.Format.SampleRate != newSampleRate)
                throw new InvalidOperationException(
                    $"output '{kv.Key}' is {kv.Value.Output.Format.SampleRate} Hz; all outputs must already match {newSampleRate} Hz before reconfiguration.");
        }

        _sampleRate = newSampleRate;
        if (_slaveClockOutputId is { } sid)
        {
            if (!_state.Outputs.ContainsKey(sid))
                throw new InvalidOperationException($"slaved output '{sid}' is no longer registered.");
            _clock = new OutputSlavedRouterClock(newSampleRate, _chunkSamples, () => ResolveClockedOutput(sid));
        }
        else if (_clock is WallClockRouterClock)
        {
            _clock = new WallClockRouterClock(newSampleRate, _chunkSamples);
        }
        else
        {
            throw new InvalidOperationException(
                "Sample rate reconfiguration: clock must be WallClockRouterClock (default) or OutputSlavedRouterClock from SlaveTo / RetargetSlaveClock. Install a known clock with SetClock first.");
        }

        _clock.Reset();
    }

    /// <summary>
    /// Slave the router's pacing to the named output, which must implement
    /// <see cref="IClockedOutput"/>. If that output is later removed, the clock
    /// transparently falls back to a wall-clock impl. Only safe while
    /// stopped.
    /// </summary>
    public void SlaveTo(string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            if (_isRunning)
                throw new InvalidOperationException("cannot slave clock while router is running");

            if (!_state.Outputs.TryGetValue(outputId, out var entry))
                throw new ArgumentException($"unknown output ID '{outputId}'", nameof(outputId));
            if (entry.Output is not IClockedOutput)
                throw new ArgumentException($"output '{outputId}' does not implement IClockedOutput", nameof(outputId));

            _slaveClockOutputId = outputId;
            _clock = new OutputSlavedRouterClock(_sampleRate, _chunkSamples, () => ResolveClockedOutput(outputId));
            Trace.LogDebug("SlaveTo: pacing router from output {SinkId} ({SinkType})", outputId, entry.Output.GetType().Name);
        }
    }

    private IClockedOutput? ResolveClockedOutput(string outputId)
    {
        var snapshot = Volatile.Read(ref _state);
        return snapshot.Outputs.TryGetValue(outputId, out var entry) ? entry.Output as IClockedOutput : null;
    }

    // --- lifecycle ---------------------------------------------------------

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning)
            {
                Trace.LogTrace("Start: already running (sinkCount={SinkCount} sourceCount={SourceCount})",
                    _state.Outputs.Count, _state.Sources.Count);
                return;
            }

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
            Trace.LogDebug("Start: rate={SampleRate}Hz chunk={Chunk} clock={ClockType} outputs={SinkCount} sources={SourceCount} routes={RouteCount}",
                _sampleRate, _chunkSamples, _clock.GetType().Name, _state.Outputs.Count, _state.Sources.Count, _state.Routes.Length);
        }
    }

    /// <summary>
    /// Stops the run loop and drains pump queues so every chunk produced before
    /// <see cref="Stop"/> reaches its output. Use <see cref="Pause"/> instead for
    /// "stop now and go silent immediately."
    /// </summary>
    public void Stop(CancellationToken cancellationToken = default)
    {
        Trace.LogDebug("Stop: draining run loop (chunksProduced={Chunks})", Volatile.Read(ref _chunksProduced));
        StopInternal(drain: true, flushAfterAbandon: false, cancellationToken);
    }

    /// <summary>
    /// Immediate-silence stop: tears the run loop down, abandons any audio
    /// queued in the per-output pumps, and calls <see cref="IFlushableOutput.Flush"/>
    /// on any output that implements it. Use <see cref="Resume"/> (alias for
    /// <see cref="Start"/>) to continue. Routes, sources, outputs, and the
    /// router clock are preserved across the pause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Output snapshots for <see cref="IFlushableOutput.Flush"/> are taken in the same critical section
    /// that stops the run loop, so the list matches the graph that was active for the final mixed
    /// chunk (still invoke <see cref="Pause"/> from the same synchronization domain that owns routing
    /// if you mutate outputs while paused).
    /// </para>
    /// </remarks>
    public void Pause()
    {
        if (!IsRunning) return;
        Trace.LogDebug("Pause: stopping run loop (chunksProduced={Chunks})", Volatile.Read(ref _chunksProduced));
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
    /// Pauses the <strong>entire</strong> router, so every output goes silent during
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
        OutputPump[] activePumps;
        IAudioOutput[]? sinksForFlush = null;
        lock (_gate)
        {
            if (!_isRunning) return;
            toDispose = _cts;
            toJoin = _thread;
            _cts = null;
            _thread = null;
            _isRunning = false;
            activePumps = [.. _state.Outputs.Values.Select(e => e.Pump)];
            if (flushAfterAbandon)
                sinksForFlush = [.. _state.Outputs.Values.Select(e => e.Output)];
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
            if (s is IFlushableOutput f)
            {
                try { f.Flush(); }
                catch (Exception ex)
                {
                    if (_log is { } l)
                        l.LogError(ex, "IFlushableOutput.Flush failed");
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
            foreach (var (_, entry) in _state.Outputs)
            {
                MediaDiagnostics.SwallowDisposeErrors(entry.Pump.Dispose, "AudioRouter.Dispose: OutputPump.Dispose");
            }

            foreach (var (_, entry) in _state.Sources)
            {
                if (entry.OwnedWrapper is null) continue;
                MediaDiagnostics.SwallowDisposeErrors(entry.OwnedWrapper.Dispose, "AudioRouter.Dispose: owned source wrapper");
            }

            _state = RouterState.Empty;
        }
    }

    private void RaiseOutputErrored(string outputId, Exception ex)
    {
        OutputErrored?.Invoke(this, new AudioRouterOutputErrorEventArgs(outputId, ex));
        if (_log is { } l)
            l.LogError(ex, "Output {SinkId} Submit failed", outputId);
        else
            MediaDiagnostics.LogError(ex, $"AudioRouter output '{outputId}' Submit");
    }

    private void RaisePumpPressure(string outputId, long droppedTotal)
    {
        PumpPressure?.Invoke(this, new AudioRouterPumpPressureEventArgs(outputId, droppedTotal));
        if (_log is { } l && l.IsEnabled(LogLevel.Trace))
            l.LogTrace("Output {SinkId} audio pump drop (running total {Dropped})", outputId, droppedTotal);
    }

    // --- inner loop --------------------------------------------------------

    private void RunLoop(CancellationToken token)
    {
        Trace.LogDebug("RunLoop: entered (rate={SampleRate} chunk={Chunk})", _sampleRate, _chunkSamples);
        var loggedFirstChunk = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Tear down any pumps that were detached on the previous iteration.
                // By now the previous chunk's enqueues are flushed (we're at the top
                // of a new iteration), so the pump is safe to dispose.
                while (_pumpsAwaitingDispose.TryDequeue(out var p)) p.Dispose();

                if (!_clock.WaitForNextChunk(token))
                {
                    Trace.LogTrace("RunLoop: WaitForNextChunk returned false (cancelled={Cancelled})", token.IsCancellationRequested);
                    break;
                }

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

                // Per-output working buffers come from each pump's free-pool. The
                // router writes the mixed audio directly into them and Commit()s
                // by reference — no second copy on the producer thread.
                foreach (var (_, output) in snapshot.Outputs)
                    Array.Clear(output.Pump.WorkingBuffer);

                foreach (var route in snapshot.Routes)
                {
                    if (!snapshot.Sources.TryGetValue(route.SourceId, out var src)) continue;
                    if (!snapshot.Outputs.TryGetValue(route.SinkId, out var output)) continue;

                    var fromGain = _currentGains.GetValueOrDefault(route.RouteId, route.Gain);
                    var toGain = _routeTargetGains.TryGetValue(route.RouteId, out var tg) ? tg : route.Gain;
                    ApplyRoute(src.Scratch, src.Source.Format.Channels,
                               output.Pump.WorkingBuffer, output.Output.Format.Channels,
                               route.Map, fromGain, toGain, _chunkSamples);
                    if (fromGain != toGain) _currentGains[route.RouteId] = toGain;
                }

                // Publish each output's mixed buffer to its pump (zero-copy hand-off);
                // the drainer thread does the actual Submit so the run loop is
                // never blocked by a slow output.
                foreach (var (_, output) in snapshot.Outputs)
                    output.Pump.Commit();

                Interlocked.Increment(ref _chunksProduced);
                if (!loggedFirstChunk)
                {
                    Trace.LogDebug("RunLoop: first chunk committed (outputs={SinkCount} routes={RouteCount})",
                        snapshot.Outputs.Count, snapshot.Routes.Length);
                    loggedFirstChunk = true;
                }
                else if (Trace.IsEnabled(LogLevel.Trace))
                {
                    var produced = Volatile.Read(ref _chunksProduced);
                    // Trace level: every 200 chunks (~2s @ 480/48k). Spammy but bounded.
                    if (produced % 200 == 0)
                        Trace.LogTrace("RunLoop: chunk #{Produced} (outputs={SinkCount})", produced, snapshot.Outputs.Count);
                }

                if (!keepRunning)
                {
                    CompletedNaturally = true;
                    lock (_gate) _isRunning = false;
                    Trace.LogDebug("RunLoop: all sources exhausted, completed naturally (chunks={Chunks})",
                        Volatile.Read(ref _chunksProduced));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "RunLoop: unhandled exception, exiting");
            throw;
        }
        finally
        {
            // When the loop exits on natural EOF, StopInternal was never called — release CTS,
            // clear the thread field, drain pumps, and flush hardware outputs so Pause/Dispose and
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
        OutputPump[] pumps;
        IAudioOutput[]? sinksForFlush = null;
        lock (_gate)
        {
            cts = _cts;
            if (cts is null)
                return;

            _cts = null;
            _thread = null;
            _isRunning = false;
            pumps = [.. _state.Outputs.Values.Select(e => e.Pump)];
            if (naturalEof)
                sinksForFlush = [.. _state.Outputs.Values.Select(e => e.Output)];
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
            if (s is IFlushableOutput f)
            {
                try { f.Flush(); }
                catch (Exception ex)
                {
                    if (_log is { } l)
                        l.LogError(ex, "IFlushableOutput.Flush after natural router stop");
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

            if (ChannelMap.TryAccumulatePackedIdentityInterleaved(src, srcChannels,
                    dst, dstChannels, map, samplesPerChannel, fromGain))
                return;

            if (ChannelMap.TryAccumulatePackedPermutationInterleaved(src, srcChannels,
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

    /// <summary>
    /// One side of a routing connection. <see cref="RouteId"/> uniquely identifies the route within
    /// the router; the legacy <see cref="AddRoute(string, string, ChannelMap, float)"/> overload
    /// synthesizes it from <c>(SourceId, SinkId)</c> for back-compat replace-by-pair semantics.
    /// Explicit routeIds (via <see cref="AddRoute(string, string, string, ChannelMap, float)"/>) let
    /// callers register multiple routes per <c>(source, output)</c> pair — used by HaPlay's per-cell
    /// audio matrix to install one route per non-zero matrix cell.
    /// </summary>
    public sealed record Route(string SourceId, string SinkId, string RouteId, ChannelMap Map, float Gain);

    /// <summary>Snapshot of pump throughput stats.</summary>
    public readonly record struct OutputPumpStats(long Enqueued, long Processed, long Dropped, int PumpCapacityChunks);

    /// <summary>
    /// Single snapshot of summed per-output pump counters from <see cref="AudioRouter.GetAggregatePumpStats"/>. Hints only — hosts still own
    /// any coordinated multi-output clock or drop policy.
    /// </summary>
    public readonly record struct AudioRouterAggregatePumpStats(
        long TotalEnqueued,
        long TotalProcessed,
        long TotalDropped,
        int MaxPumpCapacityChunks,
        int SinkCount);

    /// <param name="OwnedWrapper">
    /// When the router created an internal wrapper for this source (currently only
    /// <c>autoResample: true</c>), this holds the wrapper as <see cref="IDisposable"/> so
    /// <see cref="RemoveSource"/> / <see cref="Dispose"/> can clean it up. The caller's original
    /// source is never disposed by the router.
    /// </param>
    private sealed record SourceEntry(string Id, IAudioSource Source, float[] Scratch, IDisposable? OwnedWrapper = null);
    private sealed record OutputEntry(string Id, IAudioOutput Output, OutputPump Pump);

    private sealed record RouterState(
        ImmutableDictionary<string, SourceEntry> Sources,
        ImmutableDictionary<string, OutputEntry> Outputs,
        ImmutableArray<Route> Routes)
    {
        public static readonly RouterState Empty = new(
            ImmutableDictionary<string, SourceEntry>.Empty,
            ImmutableDictionary<string, OutputEntry>.Empty,
            ImmutableArray<Route>.Empty);
    }

    // --- per-output pump -----------------------------------------------------

    /// <summary>
    /// Bounded SPSC chunk queue + drainer thread per output. Producer is the
    /// router's run loop; consumer is the pump's <see cref="DrainLoop"/> which
    /// calls <see cref="IAudioOutput.Submit"/>. On overflow the oldest queued
    /// chunk is dropped (output can't keep up).
    /// </summary>
    /// <remarks>
    /// Zero-copy producer path: the router fills <see cref="WorkingBuffer"/>
    /// in place and calls <see cref="Commit"/> to publish it. The pump
    /// rotates the working buffer with one from its free-pool on every
    /// commit; if both the pool and consumer queue are empty, the mixed chunk
    /// is dropped in place (no fresh allocation) and the same buffer is reused for the next mix.
    /// </remarks>
    private sealed class OutputPump : IDisposable
    {
        private readonly AudioRouter _router;
        private readonly string _sinkId;
        private readonly IAudioOutput _sink;
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

        public OutputPumpStats Stats => new(
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

        public OutputPump(AudioRouter router, IAudioOutput output, int capacityChunks, int floatsPerChunk, string outputId)
        {
            _router = router;
            _sinkId = outputId;
            _sink = output;
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
                Name = $"OutputPump:{outputId}",
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
        /// in-flight <see cref="IAudioOutput.Submit"/> on the drainer thread —
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
                    catch (Exception ex) { _router.RaiseOutputErrored(_sinkId, ex); }
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
            MediaDiagnostics.SwallowDisposeErrors(_ready.CompleteAdding, "OutputPump.CompleteAdding");

            CooperativePlaybackJoin.JoinThread(_thread, TimeSpan.FromSeconds(2));
            if (_thread.IsAlive)
            {
                try { _cts.Cancel(); }
                catch (Exception ex)
                {
#if DEBUG
                    MediaDiagnostics.LogError(ex, "OutputPump.Dispose: CancellationTokenSource.Cancel");
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
