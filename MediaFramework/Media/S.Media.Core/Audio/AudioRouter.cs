using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.Core.Audio;

/// <summary>
/// Routes packed-float audio between any number of <see cref="IAudioSource"/>s
/// and <see cref="IAudioOutput"/>s. Each connection is an explicit
/// <see cref="AudioRoute"/>: a source ID, a output ID, a mandatory
/// <see cref="ChannelMap"/>, and a per-route <see cref="AudioRoute.Gain"/>. Outputs
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
public sealed partial class AudioRouter : IDisposable
{
    /// <summary>Process-wide default for <see cref="AddSource(IAudioSource, string?, bool)"/> when
    /// <paramref name="autoResample"/> is omitted and no per-instance <see cref="AutoResampleDefault"/>
    /// is set. Kept as an escape hatch; prefer the per-instance default for session-scoped behavior so
    /// one session/test can't change another's policy.</summary>
    public static bool DefaultAutoResample { get; set; }

    /// <summary>
    /// Per-instance auto-resample default. When set, <see cref="AddSource(IAudioSource, string?, bool)"/>
    /// (with <c>autoResample: null</c>) uses this instead of the process-wide <see cref="DefaultAutoResample"/>.
    /// Resolution order is per-call argument → this → <see cref="DefaultAutoResample"/>. Lets a session set
    /// its own policy without mutating a process-wide static.
    /// </summary>
    public bool? AutoResampleDefault { get; set; }

    private static int _defaultAutoResampleMismatchLogged;
    private int _sampleRate;
    private readonly int _chunkSamples;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<OutputPump> _pumpsAwaitingDispose = new();
    private readonly ConcurrentDictionary<string, byte> _stuckOutputPumps = new(StringComparer.Ordinal);
    private readonly ILogger? _log;
    private readonly int _pumpCapacityChunks;

    private RouterState _state = RouterState.Empty;
    private IRouterClock _clock;
    /// <summary>When pacing uses <see cref="OutputSlavedRouterClock"/>, the output id passed to <see cref="SlaveTo"/> / <see cref="RetargetSlaveClock"/>.</summary>
    private string? _slaveClockOutputId;
    private bool _wrapAdaptiveRateOnNonMasterOutputs;
    private int _adaptiveRateMaxDeltaHz = 3;
    /// <summary>When pacing uses <see cref="PlaybackSlavedRouterClock"/> via <see cref="SlaveToIngest"/>.</summary>
    private IPlaybackClock? _ingestPaceMaster;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _disposed;
    private volatile bool _runThreadStuck;
    private volatile Exception? _fault;
    private long _chunksProduced;
    /// <summary>Router-thread-only scratch: source ids already read this chunk (a source feeding many
    /// routes is read once). Reused across chunks to avoid per-chunk allocation.</summary>
    private readonly HashSet<string> _chunkReadSources = new(StringComparer.Ordinal);
    private int _idCounter;
    private string? _lastSourceId;
    private string? _lastOutputId;
    private readonly Dictionary<string, HashSet<IAudioSource>> _chokeGroups = new(StringComparer.Ordinal);
    private readonly Lock _chokeGate = new();

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


    /// <summary>Raised when a output throws from <see cref="IAudioOutput.Submit"/> (non-fatal; pump keeps running).</summary>
    public event EventHandler<AudioRouterOutputErrorEventArgs>? OutputErrored;

    /// <summary>Raised when the router loop hits an unhandled error (bad source read, clock, mix) and stops.
    /// The router transitions to stopped/faulted instead of crashing the host; inspect <see cref="Fault"/>.
    /// Handler runs on the router thread as it unwinds.</summary>
    public event EventHandler<AudioRouterFaultedEventArgs>? Faulted;

    /// <summary>Non-null after the router loop faulted and stopped (see <see cref="Faulted"/>). Cleared by <see cref="Start"/>.</summary>
    public Exception? Fault => _fault;

    /// <summary>Raised when a output pump drops chunks — sustained drops mean the output is behind.</summary>
    public event EventHandler<AudioRouterPumpPressureEventArgs>? PumpPressure;

    /// <summary>
    /// Output ids whose pump drainer was still alive after bounded dispose joins.
    /// These pumps intentionally keep their queue/CTS alive to avoid use-after-dispose.
    /// </summary>
    public IReadOnlyList<string> StuckOutputPumpIds => _stuckOutputPumps.Keys.ToArray();

    public AudioRouter(int sampleRate, int chunkSamples = 480, int pumpCapacityChunks = 8)
        : this(sampleRate, chunkSamples, clock: null, pumpCapacityChunks, logger: null) { }

    internal AudioRouter(int sampleRate, int chunkSamples, IRouterClock? clock, int pumpCapacityChunks = 8, ILogger? logger = null)
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
    public string AddSource(IAudioSource source, string? id = null, bool? autoResample = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var resample = autoResample ?? AutoResampleDefault ?? DefaultAutoResample;
        source.Format.Validate(nameof(source));
        IDisposable? ownedWrapper = null;
        if (source.Format.SampleRate != _sampleRate)
        {
            if (!resample)
            {
                if (Interlocked.Exchange(ref _defaultAutoResampleMismatchLogged, 1) == 0)
                {
                    MediaDiagnostics.LogWarning(
                        "AudioRouter.AddSource: source rate {0} differs from router rate {1} and autoResample is off — set AudioRouter.DefaultAutoResample = true or pass autoResample: true.",
                        source.Format.SampleRate,
                        _sampleRate);
                }

                throw new InvalidOperationException(
                    $"source sample rate {source.Format.SampleRate} doesn't match router's {_sampleRate} (pass autoResample: true to wrap, or resample upstream)");
            }

            if (MediaFrameworkPlugins.AudioResampleSourceWrapper is not { } factory)
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
            if (_disposed)
            {
                if (ownedWrapper is not null)
                    MediaDiagnostics.SwallowDisposeErrors(ownedWrapper.Dispose, "AudioRouter.AddSource: rollback source wrapper after dispose race");
                throw new ObjectDisposedException(nameof(AudioRouter));
            }

            id ??= $"__auto_src_{++_idCounter}";
            ArgumentException.ThrowIfNullOrEmpty(id);
            if (_state.Sources.ContainsKey(id))
                throw new ArgumentException($"source ID '{id}' is already registered", nameof(id));

            var entry = new SourceEntry(id, source, new float[_chunkSamples * source.Format.Channels], ownedWrapper);
            Volatile.Write(ref _state, _state with { Sources = _state.Sources.Add(id, entry) });
            Trace.LogDebug("AddSource: id={SourceId} type={SourceType} format={Format} wrapped={Wrapped}",
                id, source.GetType().Name, source.Format, ownedWrapper is not null);
            _lastSourceId = id;
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_state.Sources.TryGetValue(id, out var entry)) return false;
            ownedWrapper = entry.OwnedWrapper;
            Volatile.Write(ref _state, _state with
            {
                Sources = _state.Sources.Remove(id),
                Routes = _state.Routes.RemoveAll(r => r.Route.SourceId == id),
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
    /// <summary>
    /// When enabled, each subsequently registered non-master output is wrapped via
    /// <see cref="MediaFrameworkPlugins.WrapAdaptiveRateOutput"/> (requires FFmpeg init).
    /// Call before adding secondary outputs (NDI, file record, etc.).
    /// </summary>
    public void EnableAdaptiveRateOnNonMasterOutputs(int maxRateDeltaHz = 3)
    {
        if (MediaFrameworkPlugins.WrapAdaptiveRateOutput is null)
            throw new InvalidOperationException(
                "EnableAdaptiveRateOnNonMasterOutputs requires MediaFrameworkPlugins.WrapAdaptiveRateOutput — call MediaFrameworkRuntime.Init().UseFFmpeg().");
        if (maxRateDeltaHz < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRateDeltaHz));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _wrapAdaptiveRateOnNonMasterOutputs = true;
            _adaptiveRateMaxDeltaHz = maxRateDeltaHz;
        }
    }

    /// <summary>Snapshot of registered output ids. Allocates a fresh array per call (same as
    /// <see cref="SourceIds"/>/<see cref="OutputIds"/>) — for HUD/status polling, sample at a low
    /// rate and reuse the snapshot rather than calling per frame.</summary>
    public IReadOnlyList<string> GetRegisteredOutputIds()
    {
        lock (_gate)
            return _state.Outputs.Keys.ToArray();
    }

    public string AddOutput(IAudioOutput output, string? id = null, int? pumpCapacityChunks = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.Format.Validate(nameof(output));
        if (output.Format.SampleRate != _sampleRate)
            throw new InvalidOperationException(
                $"output sample rate {output.Format.SampleRate} doesn't match router's {_sampleRate}");

        var capacity = pumpCapacityChunks ?? _pumpCapacityChunks;
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(pumpCapacityChunks), "must be >= 2 when specified");

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id ??= $"__auto_sink_{++_idCounter}";
            ArgumentException.ThrowIfNullOrEmpty(id);
            if (_state.Outputs.ContainsKey(id))
                throw new ArgumentException($"output ID '{id}' is already registered", nameof(id));

            output = MaybeWrapAdaptiveRateOutputLocked(output, id);
            var floatsPerChunk = _chunkSamples * output.Format.Channels;
            var pump = new OutputPump(this, output, capacity, floatsPerChunk, id);
            // Pumps start idle; if the router is already running the drainer must start now so
            // this output receives chunks. If stopped, Start() will launch it later.
            if (_isRunning)
                pump.EnsureStarted();
            var entry = new OutputEntry(id, output, pump);
            _sinkFormats[id] = output.Format;
            Volatile.Write(ref _state, _state with { Outputs = _state.Outputs.Add(id, entry) });
            AutoWirePrimaryOutputIfNeeded(id, output);
            Trace.LogDebug("AddOutput: id={OutputId} type={OutputType} format={Format} clocked={Clocked} flushable={Flushable} pumpCap={PumpCapacity}",
                id, output.GetType().Name, output.Format, output is IClockedOutput, output is IFlushableOutput, capacity);
            _lastOutputId = id;
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
        IAudioOutput? removedOutput;
        bool wasRunning;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_state.Outputs.TryGetValue(id, out var entry)) return false;
            pump = entry.Pump;
            removedOutput = entry.Output;
            Volatile.Write(ref _state, _state with
            {
                Outputs = _state.Outputs.Remove(id),
                Routes = _state.Routes.RemoveAll(r => r.Route.OutputId == id),
            });
            _sinkFormats.Remove(id);
            PromoteNextPrimaryIfNeeded(id);
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

        // Dispose the router-created adaptive-rate wrapper (if any) so its monitor subscription /
        // resampler don't leak. We only dispose wrappers WE created (IAdaptiveRateWrappedOutput) — the
        // pump doesn't own its inner and we must never dispose the caller's own output.
        if (removedOutput is IAdaptiveRateWrappedOutput and IDisposable wrapper)
            MediaDiagnostics.SwallowDisposeErrors(wrapper.Dispose, "AudioRouter.RemoveOutput: adaptive wrapper");
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

    /// <summary>Routes <paramref name="sourceId"/> to <paramref name="outputId"/> with an identity channel map.</summary>
    public string Route(string sourceId, string outputId, float gain = 1.0f)
    {
        if (!TryGetOutput(outputId, out var output) || output is null)
            throw new ArgumentException($"output '{outputId}' is not registered", nameof(outputId));
        return AddRoute(sourceId, outputId, ChannelMap.Identity(output.Format.Channels), gain);
    }

    /// <summary>Routes with an explicit <paramref name="map"/> (alias for <see cref="AddRoute(string, string, ChannelMap, float)"/>).</summary>
    public string Route(string sourceId, string outputId, ChannelMap map, float gain = 1.0f) =>
        AddRoute(sourceId, outputId, map, gain);

    /// <summary>Id of the most recent <see cref="AddSource"/> on this router.</summary>
    public string? LastSourceId
    {
        get { lock (_gate) return _lastSourceId; }
    }

    /// <summary>Id of the most recent <see cref="AddOutput"/> on this router.</summary>
    public string? LastOutputId
    {
        get { lock (_gate) return _lastOutputId; }
    }

    /// <summary>
    /// Routes <see cref="LastSourceId"/> to <see cref="LastOutputId"/> (soundboard / one-clip-one-output wiring).
    /// </summary>
    public string RouteLast(ChannelMap? map = null, float gain = 1.0f)
    {
        lock (_gate)
        {
            if (_lastSourceId is null)
                throw new InvalidOperationException("RouteLast: no source registered yet — call AddSource first.");
            if (_lastOutputId is null)
                throw new InvalidOperationException("RouteLast: no output registered yet — call AddOutput first.");
        }

        var routeMap = map ?? (TryGetOutput(_lastOutputId!, out var output) && output is not null
            ? ChannelMap.Identity(output.Format.Channels)
            : throw new InvalidOperationException($"RouteLast: output '{_lastOutputId}' is not registered."));

        return Route(_lastSourceId!, _lastOutputId!, routeMap, gain);
    }

    /// <summary>Alias for <see cref="Start"/> — starts the router pump.</summary>
    public void Play() => Start();

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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
                var prior = _state.Routes[existing].Route;
                if (prior.SourceId != sourceId || prior.OutputId != outputId)
                    throw new ArgumentException(
                        $"route id '{routeId}' is already registered for ('{prior.SourceId}' -> '{prior.OutputId}'); cannot reuse for ('{sourceId}' -> '{outputId}')",
                        nameof(routeId));
            }

            RouteGainSlot gainSlot;
            if (existing >= 0)
            {
                gainSlot = _state.Routes[existing].Route.GainSlot;
                gainSlot.Target = gain;
                gainSlot.Current = gain;
            }
            else
            {
                gainSlot = new RouteGainSlot(gain);
            }

            var route = new AudioRoute(sourceId, outputId, routeId, map, gain, gainSlot);
            var resolved = new ResolvedRoute(route, src, output);
            var newRoutes = existing >= 0
                ? _state.Routes.SetItem(existing, resolved)
                : _state.Routes.Add(resolved);
            Volatile.Write(ref _state, _state with { Routes = newRoutes });
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
                if (_state.Routes[i].Route.SourceId == sourceId && _state.Routes[i].Route.OutputId == outputId)
                {
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
                if (route.Route.SourceId == sourceId && route.Route.OutputId == outputId)
                {
                    route.Route.GainSlot.Target = gain;
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
            var idx = FindRouteIndexById(_state.Routes, routeId);
            if (idx < 0)
                throw new InvalidOperationException($"no route registered under id '{routeId}'");
            _state.Routes[idx].Route.GainSlot.Target = gain;
        }
    }

    /// <summary>Synthesized routeId for the legacy single-route-per-pair API. Keeps the
    /// pre-Phase-C key shape (US-separator-delimited) so existing dictionary entries stay
    /// interpretable and dumps still read as before.</summary>
    private static string LegacyRouteId(string sourceId, string outputId) =>
        string.Concat(sourceId, '\u001f', outputId);

    private static int FindRouteIndexById(ImmutableArray<ResolvedRoute> routes, string routeId)
    {
        for (var i = 0; i < routes.Length; i++)
            if (routes[i].Route.RouteId == routeId)
                return i;
        return -1;
    }

    // --- inspection --------------------------------------------------------
    // These are snapshots: each read takes the gate and allocates a fresh array. Fine for
    // occasional inspection/diagnostics; poll at a low rate (and reuse the result) from HUDs.

    public IReadOnlyCollection<string> SourceIds
    {
        get { lock (_gate) return _state.Sources.Keys.ToArray(); }
    }
    public IReadOnlyCollection<string> OutputIds
    {
        get { lock (_gate) return _state.Outputs.Keys.ToArray(); }
    }
    public IReadOnlyList<AudioRoute> Routes
    {
        get
        {
            lock (_gate)
            {
                var routes = _state.Routes;
                if (routes.IsDefaultOrEmpty)
                    return Array.Empty<AudioRoute>();
                var copy = new AudioRoute[routes.Length];
                for (var i = 0; i < routes.Length; i++)
                    copy[i] = routes[i].Route;
                return copy;
            }
        }
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
            _ingestPaceMaster = null;
            _clock = new OutputSlavedRouterClock(_sampleRate, _chunkSamples, () => ResolveClockedOutput(outputId));
        }
    }

    // --- clocking ----------------------------------------------------------

    /// <summary>
    /// Pace the router from an ingest / media <see cref="IPlaybackClock"/> (e.g. NDI ingest timeline).
    /// Only safe while stopped — call before <see cref="Start"/> or after <see cref="Stop"/>.
    /// </summary>
    public void SlaveToIngest(IPlaybackClock ingestClock)
    {
        ArgumentNullException.ThrowIfNull(ingestClock);
        lock (_gate)
        {
            if (_isRunning)
                throw new InvalidOperationException("cannot slave clock while router is running");

            _slaveClockOutputId = null;
            _ingestPaceMaster = ingestClock;
            _clock = new PlaybackSlavedRouterClock(ingestClock, _sampleRate, _chunkSamples);
            Trace.LogDebug("SlaveToIngest: pacing router from {ClockType}", ingestClock.GetType().Name);
        }
    }

    /// <summary>
    /// Replace the active router clock. Only safe while stopped — for framework/tests.
    /// </summary>
    internal void SetClock(IRouterClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (_gate)
        {
            if (_isRunning)
                throw new InvalidOperationException("cannot replace clock while router is running");
            _slaveClockOutputId = null;
            _ingestPaceMaster = null;
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
        else if (_ingestPaceMaster is { } ingest)
        {
            _clock = new PlaybackSlavedRouterClock(ingest, newSampleRate, _chunkSamples);
        }
        else if (_clock is WallClockRouterClock)
        {
            _clock = new WallClockRouterClock(newSampleRate, _chunkSamples);
        }
        else
        {
            throw new InvalidOperationException(
                "Sample rate reconfiguration: use the default wall clock, SlaveTo(output), or SlaveToIngest(ingestClock).");
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
            _ingestPaceMaster = null;
            _clock = new OutputSlavedRouterClock(_sampleRate, _chunkSamples, () => ResolveClockedOutput(outputId));
            Trace.LogDebug("SlaveTo: pacing router from output {OutputId} ({OutputType})", outputId, entry.Output.GetType().Name);
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
            if (_runThreadStuck)
                throw new InvalidOperationException(
                    "AudioRouter cannot restart: a previous run loop thread is still alive after stop cancellation/timeout. Dispose this router and create a new one.",
                    _fault);
            if (_isRunning)
            {
                Trace.LogTrace("Start: already running (outputCount={OutputCount} sourceCount={SourceCount})",
                    _state.Outputs.Count, _state.Sources.Count);
                return;
            }

            CompletedNaturally = false;
            _fault = null;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _clock.Reset();

            // Launch each output's drainer now (lazy-start: the pumps were created idle at
            // AddOutput time). Done before the run loop thread so the first committed chunk
            // has a consumer.
            foreach (var entry in _state.Outputs.Values)
                entry.Pump.EnsureStarted();

            _thread = new Thread(() => RunLoop(token))
            {
                IsBackground = true,
                Name = "AudioRouter",
                Priority = ThreadPriority.AboveNormal,
            };
            _isRunning = true;
            _thread.Start();
            Trace.LogDebug("Start: rate={SampleRate}Hz chunk={Chunk} clock={ClockType} outputs={OutputCount} sources={SourceCount} routes={RouteCount}",
                _sampleRate, _chunkSamples, _clock.GetType().Name, _state.Outputs.Count, _state.Sources.Count, _state.Routes.Length);
        }
    }

    /// <summary>
    /// Stops the run loop and drains pump queues so every chunk produced before
    /// <see cref="Stop"/> reaches its output. Use <see cref="Pause"/> instead for
    /// "stop now and go silent immediately."
    /// </summary>
    /// <remarks>
    /// If <paramref name="cancellationToken"/> is cancelled while the run loop thread is still alive,
    /// the router becomes terminal/non-restartable; dispose it and create a new router.
    /// </remarks>
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

    /// <summary>Read-only position for resume drift checks (see <see cref="Playback.AvPlaybackCoordinator"/>).</summary>
    internal bool TryGetSeekableSourcePosition(string sourceId, out TimeSpan position)
    {
        position = default;
        if (string.IsNullOrEmpty(sourceId)) return false;
        lock (_gate)
        {
            if (!_state.Sources.TryGetValue(sourceId, out var entry))
                return false;
            if (entry.Source is not ISeekableSource seekable)
                return false;
            position = seekable.Position;
            return true;
        }
    }

    private void StopInternal(bool drain, bool flushAfterAbandon, CancellationToken cancellationToken = default)
    {
        Thread? toJoin;
        CancellationTokenSource? toDispose;
        OutputPump[] activePumps;
        ICooperativeAudioReadInterrupt[] cooperativeSources;
        IAudioOutput[]? sinksForFlush = null;
        lock (_gate)
        {
            if (!_isRunning) return;
            toDispose = _cts;
            toJoin = _thread;
            _cts = null;
            _thread = null;
            _isRunning = false;
            activePumps = CollectOutputPumps(_state.Outputs);
            cooperativeSources = CollectCooperativeAudioReadInterrupts(_state.Sources);
            if (flushAfterAbandon)
                sinksForFlush = CollectOutputs(_state.Outputs);
        }
        RequestCooperativeAudioReadYield(cooperativeSources);
        toDispose?.Cancel();
        var joinCancelled = false;
        try
        {
            CooperativePlaybackJoin.JoinThread(toJoin, TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            joinCancelled = true;
            if (toJoin is { IsAlive: true })
                MarkRunThreadStuck(new OperationCanceledException(
                    "AudioRouter stop was cancelled before the run loop thread exited; the router is now non-restartable.",
                    cancellationToken));
            throw;
        }
        finally
        {
            if (toJoin is not { IsAlive: true })
            {
                toDispose?.Dispose();
                ClearCooperativeAudioReadYield(cooperativeSources);
            }
            else
                MediaDiagnostics.LogWarning("AudioRouter.StopInternal: run loop still alive after join; leaking CancellationTokenSource to avoid use-after-dispose.");
        }

        if (toJoin is { IsAlive: true } && !joinCancelled)
            MarkRunThreadStuck(new TimeoutException(
                "AudioRouter run loop thread did not exit within the join cap; the router is now non-restartable."));

        foreach (var p in activePumps)
        {
            if (drain) p.WaitForIdle(TimeSpan.FromSeconds(1), cancellationToken);
            else
            {
                p.AbandonQueue();
                if (flushAfterAbandon)
                    p.WaitForIdle(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
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

    /// <summary>
    /// Registers <paramref name="voice"/> in a choke group and stops any other live
    /// members (e.g. prior <see cref="AudioClipVoice"/> instances in the same pad group).
    /// </summary>
    public void RegisterChokeGroup(string label, IAudioSource voice)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(voice);
        lock (_chokeGate)
        {
            if (!_chokeGroups.TryGetValue(label, out var members))
            {
                members = [];
                _chokeGroups[label] = members;
            }

            foreach (var other in members)
            {
                if (!ReferenceEquals(other, voice))
                    StopChokeMember(other);
            }

            members.Add(voice);
        }
    }

    /// <summary>Removes <paramref name="voice"/> from a choke group (no-op when not registered).</summary>
    public void UnregisterChokeGroup(string label, IAudioSource voice)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(voice);
        lock (_chokeGate)
        {
            if (_chokeGroups.TryGetValue(label, out var members))
                members.Remove(voice);
        }
    }

    private static void StopChokeMember(IAudioSource source)
    {
        if (source is AudioClipVoice voice)
            voice.Stop();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        lock (_gate)
        {
            foreach (var (_, entry) in _state.Outputs)
            {
                MediaDiagnostics.SwallowDisposeErrors(entry.Pump.Dispose, "AudioRouter.Dispose: OutputPump.Dispose");
                // Dispose router-created adaptive-rate wrappers (monitor subscription / resampler); never
                // the caller's own output (the pump doesn't own its inner).
                if (entry.Output is IAdaptiveRateWrappedOutput and IDisposable wrapper)
                    MediaDiagnostics.SwallowDisposeErrors(wrapper.Dispose, "AudioRouter.Dispose: adaptive wrapper");
            }

            foreach (var (_, entry) in _state.Sources)
            {
                if (entry.OwnedWrapper is null) continue;
                MediaDiagnostics.SwallowDisposeErrors(entry.OwnedWrapper.Dispose, "AudioRouter.Dispose: owned source wrapper");
            }

            _state = RouterState.Empty;
        }

        DisposeOwnedSources();
    }

    private static OutputPump[] CollectOutputPumps(ImmutableDictionary<string, OutputEntry> outputs)
    {
        if (outputs.IsEmpty)
            return [];
        var pumps = new OutputPump[outputs.Count];
        var i = 0;
        foreach (var (_, entry) in outputs)
            pumps[i++] = entry.Pump;
        return pumps;
    }

    private static IAudioOutput[] CollectOutputs(ImmutableDictionary<string, OutputEntry> outputs)
    {
        if (outputs.IsEmpty)
            return [];
        var collected = new IAudioOutput[outputs.Count];
        var i = 0;
        foreach (var (_, entry) in outputs)
            collected[i++] = entry.Output;
        return collected;
    }

    private static ICooperativeAudioReadInterrupt[] CollectCooperativeAudioReadInterrupts(ImmutableDictionary<string, SourceEntry> sources)
    {
        if (sources.IsEmpty)
            return [];

        List<ICooperativeAudioReadInterrupt>? collected = null;
        foreach (var (_, entry) in sources)
        {
            if (entry.Source is not ICooperativeAudioReadInterrupt interrupt)
                continue;

            collected ??= new List<ICooperativeAudioReadInterrupt>();
            collected.Add(interrupt);
        }

        return collected is null ? [] : collected.ToArray();
    }

    private static void RequestCooperativeAudioReadYield(ICooperativeAudioReadInterrupt[] sources)
    {
        foreach (var source in sources)
        {
            try { source.RequestYieldBetweenReads(); }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning("AudioRouter.StopInternal: cooperative audio read-yield request failed: {0}", ex.Message);
            }
        }
    }

    private static void ClearCooperativeAudioReadYield(ICooperativeAudioReadInterrupt[] sources)
    {
        foreach (var source in sources)
        {
            try { source.ClearYieldRequest(); }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning("AudioRouter.StopInternal: cooperative audio read-yield clear failed: {0}", ex.Message);
            }
        }
    }

    private void RaiseFaulted(Exception ex)
    {
        try { Faulted?.Invoke(this, new AudioRouterFaultedEventArgs(ex)); }
        catch (Exception hex) { MediaDiagnostics.LogError(hex, "AudioRouter.Faulted handler threw"); }
    }

    private void MarkRunThreadStuck(Exception ex)
    {
        _runThreadStuck = true;
        _fault = ex;
        if (_log is { } l)
            l.LogError(ex, "AudioRouter run loop did not stop cleanly; router is now non-restartable");
        else
            MediaDiagnostics.LogError(ex, "AudioRouter run loop did not stop cleanly; router is now non-restartable");
        RaiseFaulted(ex);
    }

    private void RaiseOutputErrored(string outputId, Exception ex)
    {
        OutputErrored?.Invoke(this, new AudioRouterOutputErrorEventArgs(outputId, ex));
        if (_log is { } l)
            l.LogError(ex, "Output {OutputId} Submit failed", outputId);
        else
            MediaDiagnostics.LogError(ex, $"AudioRouter output '{outputId}' Submit");
    }

    private void RaisePumpPressure(string outputId, long droppedTotal)
    {
        PumpPressure?.Invoke(this, new AudioRouterPumpPressureEventArgs(outputId, droppedTotal));
        if (_log is { } l && l.IsEnabled(LogLevel.Trace))
            l.LogTrace("Output {OutputId} audio pump drop (running total {Dropped})", outputId, droppedTotal);
    }

    private void MarkOutputPumpStuck(string outputId) => _stuckOutputPumps.TryAdd(outputId, 0);

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

                // Only consume sources that have at least one route this chunk. Registering a source must
                // NOT drain it — a cue/soundboard can load a clip before routing/firing it (otherwise the
                // clip is silently consumed and plays back empty when finally routed). A source feeding
                // several routes is still read exactly once; muted-but-routed sources keep advancing
                // (standard mixer "mute" — the timeline keeps running, only the gain is zero).
                //
                // Auto-stop (CompletedNaturally) fires only when routes exist AND every routed source is
                // exhausted. With no sources we run forever (silence); with sources but no routes yet we
                // also keep running so dynamic "route-last" graphs aren't killed before the route lands.
                _chunkReadSources.Clear();
                var keepRunning = snapshot.Sources.IsEmpty || snapshot.Routes.IsEmpty;
                foreach (var resolved in snapshot.Routes)
                {
                    var src = resolved.Source;
                    if (!_chunkReadSources.Add(src.Id))
                        continue; // already read this chunk for another route

                    var read = src.Source.ReadInto(src.Scratch);
                    if ((uint)read > (uint)src.Scratch.Length)
                    {
                        // Contract: ReadInto returns a count in [0, scratch.Length]. A negative
                        // count would throw in AsSpan below (crashing the router thread); an
                        // over-count would skip silence-padding and leak stale data. Don't trust a
                        // misbehaving source — clear the whole chunk and keep routing.
                        Trace.LogError("RunLoop: source returned invalid read count {Read} (scratch={Len}); clearing chunk",
                            read, src.Scratch.Length);
                        Array.Clear(src.Scratch);
                    }
                    else if (read < src.Scratch.Length)
                        src.Scratch.AsSpan(read).Clear(); // silence-pad partial reads
                    if (!src.Source.IsExhausted) keepRunning = true;
                }

                // Per-output working buffers come from each pump's free-pool. The
                // router writes the mixed audio directly into them and Commit()s
                // by reference — no second copy on the producer thread.
                foreach (var (_, output) in snapshot.Outputs)
                    Array.Clear(output.Pump.WorkingBuffer);

                foreach (var resolved in snapshot.Routes)
                {
                    var route = resolved.Route;
                    var src = resolved.Source;
                    var output = resolved.Output;
                    var slot = route.GainSlot;
                    var fromGain = slot.Current;
                    var toGain = slot.Target;
                    ApplyRoute(src.Scratch, src.Source.Format.Channels,
                               output.Pump.WorkingBuffer, output.Output.Format.Channels,
                               route.Map, fromGain, toGain, _chunkSamples);
                    if (fromGain != toGain)
                        slot.Current = toGain;
                }

                // Publish each output's mixed buffer to its pump (zero-copy hand-off); the drainer
                // thread does the actual Submit. For the pacing (primary) output the producer applies
                // backpressure — it briefly waits for its drainer to recycle a buffer instead of
                // dropping — so a jittery drainer (notably under NativeAOT scheduling) can't overflow
                // the pump. A dropped chunk on the master output skips audio CONTENT forward while the
                // sample-counted clock keeps advancing, permanently desyncing A/V; the pump is meant to
                // absorb that jitter, not discard it. Non-primary outputs keep dropping so one slow
                // output can't stall the shared router.
                var primaryPumpId = _slaveClockOutputId;
                foreach (var (id, output) in snapshot.Outputs)
                    output.Pump.Commit(applyBackpressure: primaryPumpId is not null && id == primaryPumpId);

                Interlocked.Increment(ref _chunksProduced);
                if (!loggedFirstChunk)
                {
                    Trace.LogDebug("RunLoop: first chunk committed (outputs={OutputCount} routes={RouteCount})",
                        snapshot.Outputs.Count, snapshot.Routes.Length);
                    loggedFirstChunk = true;
                }
                else if (Trace.IsEnabled(LogLevel.Trace))
                {
                    var produced = Volatile.Read(ref _chunksProduced);
                    // Trace level: every 200 chunks (~2s @ 480/48k). Spammy but bounded.
                    if (produced % 200 == 0)
                        Trace.LogTrace("RunLoop: chunk #{Produced} (outputs={OutputCount})", produced, snapshot.Outputs.Count);
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
            // A source-read / clock / mix error must NOT crash the host. Record a terminal fault,
            // stop the loop, and surface it via Fault / Faulted so the host can decide policy (swap
            // the bad source, restart, surface to UI). The finally still tears the thread/cts down.
            _fault = ex;
            Trace.LogError(ex, "RunLoop: unhandled exception — router faulted and stopped");
            RaiseFaulted(ex);
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
            pumps = CollectOutputPumps(_state.Outputs);
            if (naturalEof)
                sinksForFlush = CollectOutputs(_state.Outputs);
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
    /// synthesizes it from <c>(SourceId, OutputId)</c> for back-compat replace-by-pair semantics.
    /// Explicit routeIds (via <see cref="AddRoute(string, string, string, ChannelMap, float)"/>) let
    /// callers register multiple routes per <c>(source, output)</c> pair — used by HaPlay's per-cell
    /// audio matrix to install one route per non-zero matrix cell.
    /// </summary>
    public sealed record AudioRoute(
        string SourceId,
        string OutputId,
        string RouteId,
        ChannelMap Map,
        float Gain,
        RouteGainSlot GainSlot);

    private sealed record ResolvedRoute(AudioRoute Route, SourceEntry Source, OutputEntry Output);

    /// <summary>Snapshot of pump throughput/lifetime stats.</summary>
    public readonly record struct OutputPumpStats(long Enqueued, long Processed, long Dropped, int PumpCapacityChunks)
    {
        /// <summary>
        /// True when pump disposal hit the bounded join cap while the drainer thread was still alive.
        /// In that state the pump intentionally leaks its queue/CTS to avoid use-after-dispose.
        /// </summary>
        public bool IsStuck { get; init; }
    }

    /// <summary>
    /// Single snapshot of summed per-output pump counters from <see cref="AudioRouter.GetAggregatePumpStats"/>. Hints only — hosts still own
    /// any coordinated multi-output clock or drop policy.
    /// </summary>
    public readonly record struct AudioRouterAggregatePumpStats(
        long TotalEnqueued,
        long TotalProcessed,
        long TotalDropped,
        int MaxPumpCapacityChunks,
        int OutputCount);

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
        ImmutableArray<ResolvedRoute> Routes)
    {
        public static readonly RouterState Empty = new(
            ImmutableDictionary<string, SourceEntry>.Empty,
            ImmutableDictionary<string, OutputEntry>.Empty,
            ImmutableArray<ResolvedRoute>.Empty);
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
        /// <summary>Upper bound on how long the primary-output producer waits for the drainer to recycle
        /// a buffer (backpressure) before falling back to dropping — keeps a dead device from hanging the
        /// router thread while being far longer than any healthy drainer scheduling gap.</summary>
        private const int BackpressureCapMs = 1000;
        // Start/dispose are serialized so a late EnsureStarted can never launch the
        // drainer after Dispose has completed the collection / disposed the cts —
        // that would crash the freshly started thread (disposed _ready / _cts.Token).
        private readonly object _startGate = new();
        private bool _started;
        private float[] _working;
        private long _enqueued;
        private long _processed;
        private long _dropped;
        private int _stuck;
        private volatile bool _disposed;

        public OutputPumpStats Stats => new(
            Volatile.Read(ref _enqueued),
            Volatile.Read(ref _processed),
            Volatile.Read(ref _dropped),
            _pumpCapacityChunks)
        {
            IsStuck = Volatile.Read(ref _stuck) != 0,
        };

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

            // Create the managed Thread object now (cheap), but do NOT Start() it here.
            // Start() is what allocates the OS thread + stack; starting one per output at
            // AddOutput time — even for outputs whose router never runs — is the source of
            // the suite-level thread pressure / intermittent OOM. The drainer is launched
            // lazily via EnsureStarted() when the router actually starts (or when an output
            // is added to an already-running router).
            _thread = new Thread(() => DrainLoop(_cts.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = $"OutputPump:{outputId}",
            };
        }

        /// <summary>
        /// Idempotently launches the drainer thread. Called when the router starts (for every
        /// registered pump) and when an output is added to an already-running router. A no-op
        /// after <see cref="Dispose"/> or once already started.
        /// </summary>
        public void EnsureStarted()
        {
            lock (_startGate)
            {
                if (_disposed || _started) return;
                _started = true;
                _thread.Start();
            }
        }

        private long RecordDrop()
        {
            var total = Interlocked.Increment(ref _dropped);
            _router.RaisePumpPressure(_sinkId, total);
            return total;
        }

        /// <summary>
        /// Publish <see cref="WorkingBuffer"/> to the consumer queue and rotate in a fresh buffer for
        /// the next chunk. On pool exhaustion (consumer behind): when <paramref name="applyBackpressure"/>
        /// is <see langword="false"/> (non-primary outputs), evict the oldest queued chunk and count the
        /// drop so a slow output never stalls the shared router. When <see langword="true"/> (the pacing
        /// primary output, which is the master clock), wait briefly for the drainer to recycle a buffer
        /// instead — a dropped chunk there permanently desyncs A/V (the played sample count keeps
        /// advancing while the audio content skips), and the pump exists to absorb that jitter.
        /// </summary>
        public void Commit(bool applyBackpressure = false)
        {
            if (_disposed) return;

            var buf = _working;
            if (!_free.TryDequeue(out var next))
            {
                if (applyBackpressure && WaitForFreeBuffer(out next))
                {
                    // Recycled a buffer via backpressure — the drainer took one from _ready, so there
                    // is room below; fall through to publish without dropping.
                }
                else if (_ready.TryTake(out next!))
                    RecordDrop();
                else
                {
                    // Pool and consumer queue are empty: drop this mixed chunk and
                    // reuse the same buffer next iteration — avoids allocating on the producer thread.
                    RecordDrop();
                    return;
                }
            }
            _working = next!;

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
        /// Pace the producer to the drainer for the primary output: wait (bounded) for the drainer to
        /// recycle a buffer into the free pool rather than dropping. The drainer's <c>Submit</c> is
        /// non-blocking, so while the device is healthy it always drains <c>_ready</c> and recycles a
        /// buffer within ~one chunk; the cap is a safety valve so a wedged/closed device can never hang
        /// the shared router thread (it falls back to the drop path).
        /// </summary>
        private bool WaitForFreeBuffer([NotNullWhen(true)] out float[]? buf)
        {
            var deadline = Environment.TickCount64 + BackpressureCapMs;
            var spin = new SpinWait();
            while (!_disposed && !_cts.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                if (_free.TryDequeue(out buf))
                    return true;
                spin.SpinOnce(); // escalates to Thread.Yield/Sleep — never a hot busy-spin
            }
            buf = null;
            return false;
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
                // Each chunk we take here was counted in _enqueued and will never reach the
                // drainer, so count it as processed too — otherwise WaitForIdle sees
                // processed < enqueued forever and blocks for the full timeout after a flush.
                Interlocked.Increment(ref _processed);
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
            lock (_startGate)
            {
                if (_disposed) return;
                _disposed = true;
                // Block any concurrent EnsureStarted from here on. If the thread was never
                // started, the joins below are no-ops (JoinThread/IsAlive guard on IsAlive,
                // which is false for an unstarted Thread).
            }
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

            if (_thread.IsAlive)
            {
                Volatile.Write(ref _stuck, 1);
                _router.MarkOutputPumpStuck(_sinkId);
                Trace.LogError(
                    "AudioRouter.OutputPump '{OutputId}': drainer did not exit within the join cap; leaking pump state to avoid use-after-dispose.",
                    _sinkId);
                return;
            }

            _ready.Dispose();
            _cts.Dispose();
        }
    }
}
