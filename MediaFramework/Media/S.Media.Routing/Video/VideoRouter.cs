using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace S.Media.Routing;

/// <summary>
/// Pixel format delivered to one <see cref="VideoRouter"/> output after negotiation and fan-out
/// configuration (see <see cref="VideoRouter.TryGetInputFanOutPixelFormats"/>).
/// </summary>
/// <param name="UsesRouterCpuConverter">
/// True when this branch uses <see cref="IVideoCpuFrameConverter"/> to convert from the negotiated stream format.
/// Always false for the primary output.
/// </param>
public readonly record struct VideoRouterFanOutPixelFormat(string OutputId, PixelFormat PixelFormat, bool UsesRouterCpuConverter);

/// <summary>
/// Routes one or more logical video inputs to many <see cref="IVideoOutput"/> outputs.
/// Each <strong>output</strong> may receive from <strong>at most one</strong> input at a time
/// (unlike <see cref="Audio.AudioRouter"/>, which sums many sources into one output).
/// A single input may still fan out to many outputs (one stream, multiple displays / encoders).
/// </summary>
/// <remarks>
/// <para>
/// Pixel negotiation uses the <strong>primary</strong> output registered with
/// <see cref="AddInput"/> - its <see cref="IVideoOutput.AcceptedPixelFormats"/> are re-exposed
/// on the returned <see cref="IVideoOutput"/>. Branch outputs receive
/// <see cref="IVideoCpuFrameConverter"/> conversion when needed, using the converter factory on
/// <see cref="VideoRouterOptions"/> (wired from the media registry).
/// </para>
/// <para>
/// <strong>DRM dma-buf NV12</strong> can fan out to multiple outputs when every
/// branch accepts the same negotiated <see cref="PixelFormat.Nv12"/> (no
/// per-branch <see cref="IVideoCpuFrameConverter"/>). <strong>P010</strong> and <strong>P016</strong> dma-buf fan-out follow the same rule
/// when every branch stays <see cref="PixelFormat.P010"/> or <see cref="PixelFormat.P016"/> via <see cref="VideoFrame.CreateP010DmabufSharedReference"/> or <see cref="VideoFrame.CreateP016DmabufSharedReference"/>.
/// Each NV12 output receives an independent <see cref="VideoFrame"/> sharing refcounted fds via
/// <see cref="VideoFrame.CreateNv12DmabufSharedReference"/>; each P010 output uses <see cref="VideoFrame.CreateP010DmabufSharedReference"/>; each P016 output uses <see cref="VideoFrame.CreateP016DmabufSharedReference"/>.
/// <strong>CPU NV12</strong> with the same constraints uses <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/> so every output shares one backing instead of <see cref="VideoFrameCpuClone.DuplicateCpuBacking"/>.
/// When a branch needs a different pixel format and the input is Linux DRM dma-buf semi-planar, <see cref="VideoDmabufCpuReadback"/> performs a best-effort <c>mmap</c> copy into CPU memory for that branch’s <see cref="IVideoCpuFrameConverter"/> (may fail for tiled / protected buffers - see that type).
/// <strong>Win32</strong> shared NV12 with a branch CPU converter remains unsupported at <see cref="IVideoOutput.Submit"/> time.
/// Configure-time cannot know whether NV12, P010, or P016 frames will be CPU-backed or hardware-backed; when hardware-backed semi-planar dma-buf frames are delivered,
/// the router logs a warning if any branch uses a CPU converter (see <see cref="InputRegistration.ApplyConfigureLocked"/>).
/// </para>
/// <para>
/// For slow outputs (NDI, remote encoders), pass <see cref="VideoOutputPumpAttachOptions"/> to
/// <see cref="AddOutput"/> or wrap the output in <see cref="VideoOutputPump"/> before registering.
/// Use <see cref="TryGetVideoOutputPumpMetrics(string, out VideoOutputPumpMetrics)"/> for queue depth, drops, and capacity.
/// Subscribe to <see cref="PumpPressure"/> when an output uses <see cref="VideoOutputPumpAttachOptions"/> to react to queue-full drops without polling metrics (same role as <see cref="S.Media.Core.Audio.AudioRouter.PumpPressure"/> for audio).
/// </para>
/// <para>
/// <see cref="Dispose"/> tears down inputs then owned output outputs under the router lock. In <c>DEBUG</c> builds, failures from
/// <see cref="InputRegistration.TearDownPaths"/>, <see cref="IVideoOutput.MarkDisposed"/>, or an individual output <see cref="IDisposable.Dispose"/>
/// are logged via <see cref="MediaDiagnostics"/> and teardown continues; <c>Release</c> remains best-effort silent.
/// </para>
/// </remarks>
public sealed class VideoRouter : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ILogger? _log;
    private readonly VideoRouterOptions _options;
    private readonly Dictionary<string, OutputRegistration> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InputRegistration> _inputs = new(StringComparer.Ordinal);
    /// <summary>Each output id → owning input id (exclusive).</summary>
    private readonly Dictionary<string, string> _outputOwner = new(StringComparer.Ordinal);
    private int _idCounter;
    private bool _disposed;
    private EventHandler<VideoRouterPumpPressureEventArgs>? _pumpPressure;
    /// <summary>Total submit-plan cache rebuilds across all inputs (test/diagnostic hook for the H1
    /// cache: steady-state submits must not rebuild).</summary>
    internal int _submitPlanRebuilds;

    internal int SubmitPlanRebuilds => Volatile.Read(ref _submitPlanRebuilds);

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Video.VideoRouter");
    private const double SlowSubmitPhaseWarningMs = 50;

    public VideoRouter(ILogger? logger = null, VideoRouterOptions? options = null)
    {
        _log = logger;
        _options = options ?? VideoRouterOptions.Default;
    }

    /// <summary>Optional: raised when an async <see cref="VideoOutputPump"/> on an output drops an oldest frame; arguments include <see cref="VideoRouterPumpPressureEventArgs.OutputId"/> and cumulative drops. Handler runs on the pump's <see cref="IVideoOutput.Submit"/> caller thread (typically the router input path).</summary>
    public event EventHandler<VideoRouterPumpPressureEventArgs>? PumpPressure
    {
        add => _pumpPressure += value;
        remove => _pumpPressure -= value;
    }

    /// <summary>Snapshot of registered output ids.</summary>
    public IReadOnlyList<string> GetRegisteredOutputIds()
    {
        lock (_gate)
            return _outputs.Keys.ToArray();
    }

    /// <summary>Registers a output. <paramref name="id"/> defaults to <c>vout_1</c>, <c>vout_2</c>, …</summary>
    /// <param name="asyncPump">
    /// Explicit <see cref="VideoOutputPump"/> attach options. Overrides the default pump-by-default path
    /// described under <paramref name="synchronous"/>. The router always disposes the pump on
    /// <see cref="Dispose"/> (the pump is the registered output); the pump itself disposes the inner output
    /// only when <see cref="VideoOutputPumpAttachOptions.DisposeInnerOutputWhenPumpDisposes"/> is set.
    /// </param>
    /// <param name="synchronous">
    /// When <c>true</c>, the output is registered as-is - <see cref="IVideoOutput.Submit"/> runs on the
    /// clock-driver thread. Use this for outputs that are guaranteed to return promptly
    /// (e.g. <see cref="DiscardingVideoOutput"/>) or that manage their own threading. Mutually exclusive
    /// with <paramref name="asyncPump"/>; passing both throws <see cref="ArgumentException"/>.
    ///
    /// When <c>false</c> (default) <strong>and</strong> <paramref name="asyncPump"/> is <c>null</c>,
    /// the output is wrapped in a <see cref="VideoOutputPump"/> with default capacity so a slow
    /// <see cref="IVideoOutput.Submit"/> cannot stall the clock thread. The pump propagates dispose to
    /// the inner output only when <paramref name="disposeOutputOnRouterDispose"/> is <c>true</c>; the pump
    /// itself is always disposed with the router.
    /// </param>
    public string AddOutput(IVideoOutput output, string? id = null, bool disposeOutputOnRouterDispose = false,
        VideoOutputPumpAttachOptions? asyncPump = null, bool synchronous = false)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (synchronous && asyncPump is not null)
            throw new ArgumentException(
                "synchronous: true is mutually exclusive with asyncPump - pick one.",
                nameof(synchronous));

        // Default ergonomics (Phase 2): pump-by-default so new outputs don't silently stall the clock
        // thread on a slow Submit. Callers that know their output is prompt opt out with synchronous: true;
        // callers that want a tuned pump pass asyncPump explicitly.
        if (!synchronous && asyncPump is null)
        {
            asyncPump = new VideoOutputPumpAttachOptions(
                DisposeInnerOutputWhenPumpDisposes: disposeOutputOnRouterDispose);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id ??= $"vout_{++_idCounter}";
            if (_outputs.ContainsKey(id))
                throw new ArgumentException($"video output id '{id}' is already registered", nameof(id));

            if (asyncPump is { } ap)
            {
                if (ap.MaxQueuedFrames < 1)
                    throw new ArgumentOutOfRangeException(nameof(asyncPump), "MaxQueuedFrames must be >= 1");
                var pumpName = ap.ThreadName ?? $"VideoOutputPump-{id}";
                var pump = new VideoOutputPump(output, ap.MaxQueuedFrames, pumpName, ap.Logger,
                    disposeInnerOnDispose: ap.DisposeInnerOutputWhenPumpDisposes);
                var outputId = id;
                pump.PumpPressure += (_, e) => RaisePumpPressure(outputId, e.DroppedFramesTotal);
                output = pump;
                disposeOutputOnRouterDispose = true;
            }

            _outputs.Add(id, new OutputRegistration(id, output, disposeOutputOnRouterDispose));
            Trace.LogDebug("AddOutput: id={OutputId} type={OutputType} async={IsAsync} disposeOnRouterDispose={Dispose}",
                id, output.GetType().Name, output is VideoOutputPump, disposeOutputOnRouterDispose);
            return id;
        }
    }

    /// <summary>Removes an output and any routes targeting it. Inputs whose primary output is removed are removed entirely.</summary>
    public bool RemoveOutput(string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "VideoRouter.RemoveOutput", slowWarningMs: 250);
        IDisposable? ownedToDispose = null;
        var removedInputs = 0;
        var removedRoutes = 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_outputs.TryGetValue(outputId, out var removed))
            {
                timing?.SetOutcome($"output={outputId} not-found");
                return false;
            }

            foreach (var kv in _inputs.ToArray())
            {
                if (kv.Value.PrimaryOutputId == outputId)
                {
                    removedInputs++;
                    RemoveInputLocked(kv.Key);
                }
            }

            _outputs.Remove(outputId);
            _outputOwner.Remove(outputId);

            foreach (var reg in _inputs.Values.ToArray())
            {
                if (reg.RemoveOutputFromRoutes(outputId))
                {
                    removedRoutes++;
                    ReconfigureInputIfNeededLocked(reg);
                }
            }

            // The router owns the registered output/pump when it was added with
            // disposeOutputOnRouterDispose (always true on the default pump-by-default
            // path). Dispose it on removal too - Dispose() does, but RemoveOutput
            // previously dropped the registration without disposing, leaking the pump's
            // drainer thread and the inner output's native resources.
            if (removed.DisposeOutputOnRouterDispose && removed.Output is IDisposable d)
                ownedToDispose = d;
        }

        // Outside the lock: VideoOutputPump.Dispose joins its drainer thread, so we must
        // not hold _gate during teardown.
        if (ownedToDispose is not null)
            MediaDiagnostics.SwallowDisposeErrors(ownedToDispose.Dispose, $"VideoRouter.RemoveOutput: output '{outputId}'");

        timing?.SetOutcome($"output={outputId} inputs={removedInputs} routes={removedRoutes}");
        Trace.LogDebug("RemoveOutput: id={OutputId} removedInputs={RemovedInputs} removedRoutes={RemovedRoutes}",
            outputId, removedInputs, removedRoutes);
        return true;
    }

    /// <summary>
    /// Registers a logical input bound to <paramref name="primaryOutputId"/> (negotiation lead).
    /// That output must be free - it is claimed immediately.
    /// </summary>
    public VideoRouterInputRegistration AddInput(string primaryOutputId, string? inputId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(primaryOutputId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            inputId ??= $"vin_{++_idCounter}";
            if (_inputs.ContainsKey(inputId))
                throw new ArgumentException($"video input id '{inputId}' is already registered", nameof(inputId));
            if (!_outputs.TryGetValue(primaryOutputId, out var primaryOut))
                throw new ArgumentException($"unknown video output id '{primaryOutputId}'", nameof(primaryOutputId));
            if (_outputOwner.ContainsKey(primaryOutputId))
            {
                var msg =
                    $"VideoRouter: cannot add input '{inputId}' - output '{primaryOutputId}' is already routed from input '{_outputOwner[primaryOutputId]}'.";
                _log?.LogError("{Message}", msg);
                throw new InvalidOperationException(msg);
            }

            var accepted = primaryOut.Output.AcceptedPixelFormats;
            var reg = new InputRegistration(this, inputId, primaryOutputId, accepted);
            _inputs.Add(inputId, reg);
            _outputOwner[primaryOutputId] = inputId;
            reg.RoutedOutputIds.Add(primaryOutputId);
            reg.RoutedSet.Add(primaryOutputId);
            Trace.LogDebug("AddInput: id={InputId} primaryOut={Primary} acceptedFormats=[{Accepted}]",
                inputId, primaryOutputId, string.Join(",", accepted));
            return new VideoRouterInputRegistration(inputId, reg.Output);
        }
    }

    /// <summary>
    /// Adds a fan-out route from <paramref name="inputId"/> to <paramref name="outputId"/>.
    /// Fails when <paramref name="outputId"/> is already owned by another input.
    /// </summary>
    public bool TryAddRoute(string inputId, string outputId, [NotNullWhen(false)] out string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        errorMessage = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inputs.TryGetValue(inputId, out var reg))
            {
                errorMessage = $"unknown video input id '{inputId}'";
                return false;
            }
            if (!_outputs.ContainsKey(outputId))
            {
                errorMessage = $"unknown video output id '{outputId}'";
                return false;
            }

            if (reg.RoutedSet.Contains(outputId))
            {
                errorMessage = null;
                return true;
            }

            if (_outputOwner.TryGetValue(outputId, out var owner) && owner != inputId)
            {
                errorMessage =
                    $"output '{outputId}' is already routed from input '{owner}'; cannot route from '{inputId}'.";
                _log?.LogError(
                    "VideoRouter: declined route - output {OutputId} is owned by input {Owner}; attempted input {Attempted}.",
                    outputId, owner, inputId);
                return false;
            }

            // Capture the pre-mutation configure state so a failed branch negotiation can be rolled
            // back completely. Without this, a throw out of ApplyConfigureLocked used to leave a
            // half-added route behind with Configured=false - every subsequent Submit on the input
            // then failed until the route was manually removed.
            var wasConfigured = reg.Configured;
            var previousFormat = reg.NegotiatedFormat;

            _outputOwner[outputId] = inputId;
            reg.RoutedOutputIds.Add(outputId);
            reg.RoutedSet.Add(outputId);
            try
            {
                ReconfigureInputIfNeededLocked(reg);
            }
            catch (Exception ex)
            {
                _outputOwner.Remove(outputId);
                reg.RoutedOutputIds.Remove(outputId);
                reg.RoutedSet.Remove(outputId);
                if (wasConfigured && previousFormat is { } prevFmt)
                {
                    // The previous graph was valid; re-applying it must succeed. Guard anyway so a
                    // restore failure degrades to "input unconfigured" instead of unwinding TryAddRoute.
                    try
                    {
                        reg.TearDownPaths();
                        reg.ApplyConfigureLocked(prevFmt);
                    }
                    catch (Exception restoreEx)
                    {
                        _log?.LogError(restoreEx,
                            "VideoRouter: failed to restore input {InputId} after rejected route to {OutputId}; input left unconfigured.",
                            inputId, outputId);
                    }
                }

                errorMessage =
                    $"route '{inputId}' -> '{outputId}' was rejected during format negotiation and rolled back: {ex.Message}";
                _log?.LogWarning(ex, "VideoRouter: TryAddRoute {InputId} -> {OutputId} rejected; route rolled back.",
                    inputId, outputId);
                return false;
            }

            Trace.LogDebug("TryAddRoute: input={InputId} -> output={OutputId} (totalRoutes={Total})",
                inputId, outputId, reg.RoutedOutputIds.Count);
            return true;
        }
    }

    /// <summary>Removes one fan-out branch. The primary output for an input cannot be removed while the input exists - remove the input instead.</summary>
    public bool TryRemoveRoute(string inputId, string outputId, [NotNullWhen(false)] out string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        errorMessage = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inputs.TryGetValue(inputId, out var reg))
            {
                errorMessage = $"unknown video input id '{inputId}'";
                return false;
            }
            if (outputId == reg.PrimaryOutputId)
            {
                errorMessage =
                    $"cannot remove primary output '{outputId}' from input '{inputId}' - call RemoveInput instead.";
                return false;
            }
            if (!reg.RoutedSet.Remove(outputId))
            {
                errorMessage = null;
                return true;
            }
            reg.RoutedOutputIds.Remove(outputId);
            _outputOwner.Remove(outputId);
            ReconfigureInputIfNeededLocked(reg);
            Trace.LogDebug("TryRemoveRoute: input={InputId} -> output={OutputId} (remainingRoutes={Total})",
                inputId, outputId, reg.RoutedOutputIds.Count);
            return true;
        }
    }

    /// <summary>Removes an input, all of its routes, and disposes its per-output converters.</summary>
    public bool RemoveInput(string inputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var removed = RemoveInputLocked(inputId);
            if (removed)
                Trace.LogDebug("RemoveInput: id={InputId}", inputId);
            return removed;
        }
    }

    private bool RemoveInputLocked(string inputId)
    {
        if (!_inputs.Remove(inputId, out var reg)) return false;
        foreach (var oid in reg.RoutedOutputIds)
            _outputOwner.Remove(oid);
        reg.TearDownPaths();
        reg.Output.MarkDisposed();
        return true;
    }

    /// <summary>
    /// When <paramref name="inputId"/> is configured, returns the negotiated stream <see cref="VideoFormat"/> and
    /// each routed output's output pixel format in route order (primary output first).
    /// </summary>
    /// <returns>
    /// False when <paramref name="inputId"/> is unknown or not yet configured. On false, <paramref name="negotiated"/> is
    /// <c>default</c> and <paramref name="perOutput"/> is null.
    /// </returns>
    public bool TryGetInputFanOutPixelFormats(
        string inputId,
        out VideoFormat negotiated,
        [NotNullWhen(true)] out IReadOnlyList<VideoRouterFanOutPixelFormat>? perOutput)
    {
        negotiated = default;
        perOutput = null;
        ArgumentException.ThrowIfNullOrEmpty(inputId);
        lock (_gate)
        {
            if (!_inputs.TryGetValue(inputId, out var reg) || !reg.Configured || reg.NegotiatedFormat is not { } nf)
                return false;

            negotiated = nf;
            var list = new List<VideoRouterFanOutPixelFormat>(reg.RoutedOutputIds.Count);
            foreach (var oid in reg.RoutedOutputIds)
            {
                if (!_outputs.TryGetValue(oid, out var oreg))
                    continue;
                var px = oreg.Output.Format.PixelFormat;
                list.Add(new VideoRouterFanOutPixelFormat(oid, px, reg.BranchUsesCpuConverter(oid)));
            }

            perOutput = list;
            return true;
        }
    }

    public void Dispose()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "VideoRouter.Dispose", slowWarningMs: 1000);
        lock (_gate)
        {
            if (_disposed)
            {
                timing?.SetOutcome("already-disposed");
                return;
            }
            _disposed = true;
            _pumpPressure = null;
            var inputCount = _inputs.Count;
            var outputCount = _outputs.Count;
            foreach (var reg in _inputs.Values)
            {
                MediaDiagnostics.SwallowDisposeErrors(() =>
                {
                    reg.TearDownPaths();
                    reg.Output.MarkDisposed();
                }, "VideoRouter.Dispose: input teardown");
            }

            _inputs.Clear();
            _outputOwner.Clear();
            foreach (var o in _outputs.Values)
            {
                if (!o.DisposeOutputOnRouterDispose || o.Output is not IDisposable d)
                    continue;
                MediaDiagnostics.SwallowDisposeErrors(d.Dispose, $"VideoRouter.Dispose: output output '{o.Id}'");
            }

            _outputs.Clear();
            timing?.SetOutcome($"inputs={inputCount} outputs={outputCount}");
        }
    }

    /// <summary>When <paramref name="outputId"/> was registered with an async <see cref="VideoOutputPump"/>, returns its counters.</summary>
    public bool TryGetVideoOutputPumpMetrics(string outputId, out VideoOutputPumpMetrics metrics)
    {
        metrics = default;
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        VideoOutputPump? pump = null;
        lock (_gate)
        {
            if (_outputs.TryGetValue(outputId, out var reg) && reg.Output is VideoOutputPump p)
                pump = p;
        }

        if (pump is null)
            return false;

        // ROUTE-01: read the pump counters OUTSIDE the router lock. CurrentQueuedDepth takes the pump's own
        // gate, so resolving the pump reference under _gate and then reading its (Interlocked/Volatile)
        // counters without it keeps the router→pump lock nest from ever pairing with a pump→router path
        // (a pump-pressure subscriber that calls back into the router while the pump held its gate).
        metrics = new VideoOutputPumpMetrics(
            pump.DroppedFrames,
            pump.SubmittedFrames,
            pump.MaxQueueDepth,
            pump.CurrentQueuedDepth);
        return true;
    }

    /// <summary>When <paramref name="outputId"/> was registered with an async <see cref="VideoOutputPump"/>, returns dropped and submitted counts.</summary>
    public bool TryGetVideoOutputPumpMetrics(string outputId, out long droppedFrames, out long submittedFrames)
    {
        if (!TryGetVideoOutputPumpMetrics(outputId, out var m))
        {
            droppedFrames = 0;
            submittedFrames = 0;
            return false;
        }

        droppedFrames = m.DroppedFrames;
        submittedFrames = m.SubmittedFrames;
        return true;
    }

    private void RaisePumpPressure(string outputId, long droppedFramesTotal) =>
        _pumpPressure?.Invoke(this, new VideoRouterPumpPressureEventArgs(outputId, droppedFramesTotal));


    private IVideoCpuFrameConverter CreateCpuFrameConverter() =>
        _options.VideoCpuFrameConverterFactory?.Invoke()
        ?? throw new InvalidOperationException(
            "VideoRouter: no CPU frame converter - set VideoRouterOptions.VideoCpuFrameConverterFactory (wire registry.CreateCpuConverter).");

    private bool CanConvertCpuFrame(PixelFormat src, PixelFormat dst, int width, int height) =>
        _options.VideoCpuFrameCanConvertProbe?.Invoke(src, dst, width, height) ?? false;

    private static void ApplyD3D11GlBorrowVideoSourceToOutput(IVideoOutput output, IVideoSource? videoSource)
    {
        if (output is not IVideoOutputD3D11GlBorrowSetup borrowSetup)
            return;
        if (videoSource is IHardwareD3D11GlInteropSource)
            borrowSetup.SetBorrowVideoSourceForWin32Nv12Gl(videoSource);
        else
            borrowSetup.SetBorrowVideoSourceForWin32Nv12Gl(null);
    }

    private void ReconfigureInputIfNeededLocked(InputRegistration reg)
    {
        if (!reg.Configured || reg.NegotiatedFormat is not { } fmt) return;
        reg.TearDownPaths();
        reg.ApplyConfigureLocked(fmt);
    }

    private sealed class OutputRegistration(string Id, IVideoOutput Output, bool DisposeOutputOnRouterDispose)
    {
        public string Id { get; } = Id;
        public IVideoOutput Output { get; } = Output;
        public bool DisposeOutputOnRouterDispose { get; } = DisposeOutputOnRouterDispose;
    }

    private sealed class InputRegistration
    {
        private readonly VideoRouter _owner;
        public string Id { get; }
        public string PrimaryOutputId { get; }
        public VideoRouterInputOutput Output { get; }
        public List<string> RoutedOutputIds { get; } = [];
        public HashSet<string> RoutedSet { get; } = new(StringComparer.Ordinal);
        public bool Configured { get; private set; }
        public VideoFormat? NegotiatedFormat { get; private set; }
        private readonly Dictionary<string, OutputPathState> _paths = new(StringComparer.Ordinal);
        private IVideoSource? _borrowVideoSourceForWin32Nv12Gl;
        private int _nv12FanoutCpuBranchWarned;

        // P2-2: branch CPU conversion runs OUTSIDE the router _gate. _submitLock serializes submits on
        // this input (swscale converters are not reentrant) without taking _gate, so it never blocks
        // other inputs or route mutations. _convertInFlight (guarded by _owner._gate) tells TearDownPaths
        // a submit is mid-conversion, so it defers disposing the leased converters into _deferredConverters
        // instead of freeing them under the in-flight Convert; the submit drains them when it re-takes the
        // lock to deliver. (A plain per-input lock taken by both submit and TearDownPaths would deadlock
        // against router-level RemoveOutput, which holds _gate and reconfigures multiple inputs.)
        private readonly Lock _submitLock = new();
        private bool _convertInFlight;
        private readonly List<IVideoCpuFrameConverter> _deferredConverters = [];

        // H1: route-derived submit plan, cached per configuration version. Every route/converter
        // mutation funnels through ApplyConfigureLocked or TearDownPaths (both under _owner._gate),
        // which bump _routesVersion; the per-frame submit only rebuilds the plan arrays when the
        // version moved. Frame-dependent bits (hardware backing → readback / fan-out eligibility)
        // are computed per submit from the cached aggregates below.
        private int _routesVersion;
        private int _planVersion = -1;
        private string[] _planOutputIds = [];
        private IVideoCpuFrameConverter?[] _planConverters = [];
        private bool[] _planPumpConverts = [];
        private bool _planAnySyncConverter;
        private bool _planAnyConverterOrPump;
        private long _lastSlowSubmitPhaseLogTicks;
        /// <summary>Branch-frame scratch reused across submits (serialized by <see cref="_submitLock"/>;
        /// every slot is null outside a submit).</summary>
        private VideoFrame?[] _branchFrameScratch = [];

        public InputRegistration(VideoRouter owner, string id, string primaryOutputId, IReadOnlyList<PixelFormat> accepted)
        {
            _owner = owner;
            Id = id;
            PrimaryOutputId = primaryOutputId;
            Output = new VideoRouterInputOutput(owner, this, accepted);
        }

        public bool RemoveOutputFromRoutes(string outputId)
        {
            if (!RoutedSet.Remove(outputId)) return false;
            RoutedOutputIds.Remove(outputId);
            return true;
        }

        public bool BranchUsesCpuConverter(string outputId) =>
            outputId != PrimaryOutputId && _paths.TryGetValue(outputId, out var st) && (st.Converter != null || st.PumpConverts);

        public void ApplyConfigureLocked(VideoFormat negotiated)
        {
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            if (RoutedOutputIds.Count == 0)
                throw new InvalidOperationException($"VideoRouter input '{Id}' has no routed outputs.");

            _routesVersion++; // invalidate the cached submit plan (H1)

            VideoRouter.Trace.LogDebug("ApplyConfigure: input={InputId} negotiated={Format} routedOutputs=[{Outputs}]",
                Id, negotiated, string.Join(",", RoutedOutputIds));
            NegotiatedFormat = negotiated;
            var primaryOutput = _owner._outputs[PrimaryOutputId].Output;
            VideoRouter.ApplyD3D11GlBorrowVideoSourceToOutput(primaryOutput, _borrowVideoSourceForWin32Nv12Gl);
            primaryOutput.Configure(negotiated);
            _paths.Clear();

            for (var i = 1; i < RoutedOutputIds.Count; i++)
            {
                var oid = RoutedOutputIds[i];
                var branchOutput = _owner._outputs[oid].Output;
                var branchFmt = VideoOutputFanoutFormats.PickBranchPixelFormat(
                    negotiated,
                    branchOutput.AcceptedPixelFormats,
                    _owner.CanConvertCpuFrame);
                var needsConversion = branchFmt != negotiated.PixelFormat;
                var branchVideoFormat = new VideoFormat(negotiated.Width, negotiated.Height, branchFmt, negotiated.FrameRate);

                // P2-7: when the branch output is asynchronous (a VideoOutputPump - the default wrapping), hand
                // any pixel conversion to the pump so the swscale repack runs on the pump's drain thread instead
                // of the player submit thread (matters for heavy branches like NDI yuv422p10le→UYVY at 4K60). The
                // branch then flows through the no-converter fan-out path in SubmitPhased and receives a
                // zero-copy raw view. Always (re)set the pump converter so a reconfigure that no longer needs one
                // clears the previous converter rather than leaving it repacking stale frames.
                if (branchOutput is VideoOutputPump pump)
                {
                    IVideoCpuFrameConverter? pumpConv = null;
                    if (needsConversion)
                    {
                        pumpConv = _owner.CreateCpuFrameConverter();
                        pumpConv.Configure(negotiated.PixelFormat, branchFmt, negotiated.Width, negotiated.Height);
                    }
                    branchOutput.Configure(branchVideoFormat);
                    pump.SetBranchConverter(pumpConv);
                    _paths[oid] = new OutputPathState(Converter: null, PumpConverts: needsConversion);
                    continue;
                }

                IVideoCpuFrameConverter? conv = null;
                if (needsConversion)
                {
                    conv = _owner.CreateCpuFrameConverter();
                    conv.Configure(negotiated.PixelFormat, branchFmt, negotiated.Width, negotiated.Height);
                }
                branchOutput.Configure(branchVideoFormat);
                _paths[oid] = new OutputPathState(Converter: conv);
            }

            if (RoutedOutputIds.Count > 1 && negotiated.PixelFormat is PixelFormat.Nv12 or PixelFormat.P010 or PixelFormat.P016)
            {
                for (var i = 1; i < RoutedOutputIds.Count; i++)
                {
                    var oid = RoutedOutputIds[i];
                    if (_paths.TryGetValue(oid, out var st) && (st.Converter != null || st.PumpConverts))
                    {
                        if (Interlocked.Exchange(ref _nv12FanoutCpuBranchWarned, 1) == 0)
                        {
                            _owner._log?.LogWarning(
                                "VideoRouter input {InputId}: NV12/P010/P016 fan-out includes branch output(s) that use a CPU pixel converter - if the decoder delivers DRM dma-buf NV12/P010/P016, the router attempts an mmap readback for swscale (see VideoDmabufCpuReadback); Win32 shared NV12 with a branch converter is still unsupported.",
                                Id);
                        }

                        break;
                    }
                }
            }

            Configured = true;
        }

        public void TearDownPaths()
        {
            _routesVersion++; // invalidate the cached submit plan (H1)
            foreach (var p in _paths.Values)
            {
                if (p.Converter is not { } conv) continue;
                if (_convertInFlight)
                    _deferredConverters.Add(conv); // a submit may be converting with it - free it when the submit drains
                else
                    conv.Dispose();
            }
            _paths.Clear();
            Configured = false;
            NegotiatedFormat = null;
            if (!_convertInFlight)
                DrainDeferredConvertersLocked();
        }

        /// <summary>Disposes converters that were deferred while a submit held them. Caller holds <c>_owner._gate</c>.</summary>
        private void DrainDeferredConvertersLocked()
        {
            if (_deferredConverters.Count == 0) return;
            foreach (var c in _deferredConverters)
                MediaDiagnostics.SwallowDisposeErrors(c.Dispose, "VideoRouter: deferred branch converter");
            _deferredConverters.Clear();
        }

        internal void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource)
        {
            _borrowVideoSourceForWin32Nv12Gl = videoSource;
            var primaryOutput = _owner._outputs[PrimaryOutputId].Output;
            VideoRouter.ApplyD3D11GlBorrowVideoSourceToOutput(primaryOutput, _borrowVideoSourceForWin32Nv12Gl);
        }

        public void AbandonQueuedFrames(VideoRouterInputOutput sink)
        {
            foreach (var output in SnapshotRoutedOutputs(sink))
            {
                if (output is IVideoOutputQueueControl control)
                    control.AbandonQueuedFrames();
            }
        }

        public bool WaitForIdle(VideoRouterInputOutput sink, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            var outputs = SnapshotRoutedOutputs(sink);
            var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
            foreach (var output in outputs)
            {
                if (output is not IVideoOutputQueueControl control)
                    continue;
                var remainingMs = Math.Max(0, deadline - Environment.TickCount64);
                if (!control.WaitForIdle(TimeSpan.FromMilliseconds(remainingMs), cancellationToken))
                {
                    VideoRouter.Trace.LogWarning(
                        "WaitForIdle: input={InputId} timed out after {ElapsedMs:0.00}ms (timeout={TimeoutMs:0.00}ms, routedOutputs={OutputCount}, blockedOutput={OutputType})",
                        Id,
                        MediaDiagnostics.ElapsedMillisecondsSince(started),
                        timeout.TotalMilliseconds,
                        outputs.Length,
                        output.GetType().Name);
                    return false;
                }
            }

            return true;
        }

        private IVideoOutput[] SnapshotRoutedOutputs(VideoRouterInputOutput sink)
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(sink.IsSinkDisposed, sink);
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                if (!_owner._inputs.TryGetValue(Id, out var current) || !ReferenceEquals(current, this))
                    return [];

                var outputs = new List<IVideoOutput>(RoutedOutputIds.Count);
                foreach (var outputId in RoutedOutputIds)
                {
                    if (_owner._outputs.TryGetValue(outputId, out var output))
                        outputs.Add(output.Output);
                }

                return outputs.ToArray();
            }
        }

        /// <summary>
        /// P2-2: submit with branch conversion performed OUTSIDE the router <c>_gate</c>. Phase 1 snapshots
        /// the routed outputs + their branch converters under the lock and leases the converters; phase 2
        /// does the readback / convert / clone with the lock released (the expensive part, so route
        /// mutations on this and other inputs aren't blocked by it); phase 3 re-takes the lock, releases the
        /// lease, frees any converters a concurrent reconfigure deferred, and delivers to the outputs that
        /// still exist. <c>_submitLock</c> serializes submits on this input (converters aren't reentrant).
        /// </summary>
        public void SubmitPhased(VideoFrame frame, VideoRouterInputOutput sink)
        {
            var submitStarted = VideoRouter.Trace.IsEnabled(LogLevel.Warning) ? Stopwatch.GetTimestamp() : 0;
            try
            {
                lock (_submitLock)
                {
                    SubmitPlan plan;
                    lock (_owner._gate)
                    {
                        ObjectDisposedException.ThrowIf(sink.IsSinkDisposed, sink);
                        ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                        if (!Configured || NegotiatedFormat is null)
                        {
                            frame.Dispose();
                            throw new InvalidOperationException($"VideoRouter input '{Id}'.Submit called before Configure");
                        }

                        // Single output: no branch conversion - deliver directly under the lock (fast path).
                        if (RoutedOutputIds.Count == 1)
                        {
                            _owner._outputs[RoutedOutputIds[0]].Output.Submit(frame);
                            return;
                        }

                        plan = GetSubmitPlanLocked(frame);
                        _convertInFlight = true; // lease the snapshotted converters from disposal by TearDownPaths
                    }

                    var n = plan.OutputIds.Length;
                    var hint = frame.ColorTransferHint;
                    VideoFrame? primary = frame;
                    if (_branchFrameScratch.Length != n - 1)
                        _branchFrameScratch = new VideoFrame?[n - 1];
                    var branchFrames = _branchFrameScratch;
                    VideoFrame? converterReadback = null;
                    try
                    {
                        // ---- Phase 2: readback + build branch frames, NO _gate held ----
                        if (plan.NeedsHwReadback)
                        {
                            if (frame.Win32Nv12 is not null)
                            {
                                frame.Dispose();
                                throw new NotSupportedException(
                                    $"VideoRouter input '{Id}': cannot convert Win32 D3D11 shared-handle NV12 for a branch output - use NV12 outputs for all routes, a single output, or software decode (e.g. VideoPlaybackSmoke --no-hw).");
                            }
                            if (frame.DmabufNv12 is not null && !VideoDmabufCpuReadback.TryCreateNv12CpuCopy(frame, out converterReadback))
                            {
                                frame.Dispose();
                                throw new NotSupportedException(
                                    $"VideoRouter input '{Id}': DRM dma-buf NV12 could not be mmap-read for a branch CPU converter - use matching outputs, a single output, or software decode (e.g. VideoPlaybackSmoke --no-hw).");
                            }
                            if (frame.DmabufP010 is not null && !VideoDmabufCpuReadback.TryCreateP010CpuCopy(frame, out converterReadback))
                            {
                                frame.Dispose();
                                throw new NotSupportedException(
                                    $"VideoRouter input '{Id}': DRM dma-buf P010 could not be mmap-read for a branch CPU converter - use matching outputs, a single output, or software decode.");
                            }
                            if (frame.DmabufP016 is not null && !VideoDmabufCpuReadback.TryCreateP016CpuCopy(frame, out converterReadback))
                            {
                                frame.Dispose();
                                throw new NotSupportedException(
                                    $"VideoRouter input '{Id}': DRM dma-buf P016 could not be mmap-read for a branch CPU converter - use matching outputs, a single output, or software decode.");
                            }
                        }

                        if (plan.CanCpuFanOut &&
                            VideoFrame.TryCreateCpuFanOutViews(frame, n, hint, out var fanViews))
                        {
                            // Zero-copy: every output (primary + branches) shares the one backing. A pump-converts
                            // branch receives its raw view here and repacks it on its own drain thread.
                            for (var i = 1; i < n; i++)
                                branchFrames[i - 1] = fanViews[i];
                            frame.Dispose();
                            primary = fanViews[0];
                        }
                        else
                        {
                            for (var i = 1; i < n; i++)
                            {
                                var conv = plan.BranchConverters[i - 1];
                                if (conv != null)
                                {
                                    // Synchronous branch conversion on the submit thread.
                                    branchFrames[i - 1] = conv.Convert(converterReadback ?? frame, hint);
                                }
                                else if (plan.PumpConvertsBranch[i - 1])
                                {
                                    // Pump-converts branch on the per-branch path (a sync-converter sibling blocked
                                    // fan-out, or the frame was hardware-backed and read back): hand the pump a CPU
                                    // frame it can repack on its own thread (converterReadback is the dma-buf / Win32
                                    // CPU copy when applicable, else the CPU frame itself).
                                    branchFrames[i - 1] = VideoFrameCpuClone.DuplicateCpuBacking(converterReadback ?? frame, hint);
                                }
                                else
                                {
                                    branchFrames[i - 1] = frame.DmabufNv12 is not null
                                        ? VideoFrame.CreateNv12DmabufSharedReference(frame)
                                        : frame.DmabufP010 is not null
                                            ? VideoFrame.CreateP010DmabufSharedReference(frame)
                                            : frame.DmabufP016 is not null
                                                ? VideoFrame.CreateP016DmabufSharedReference(frame)
                                                : frame.Win32Nv12 is not null
                                                    ? VideoFrame.CreateNv12Win32SharedReference(frame)
                                                    : VideoFrameCpuClone.DuplicateCpuBacking(frame, hint);
                                }
                            }
                        }

                        converterReadback?.Dispose();
                        converterReadback = null;

                        // ---- Phase 3: release the lease, free deferred converters, deliver under _gate ----
                        lock (_owner._gate)
                        {
                            _convertInFlight = false;
                            DrainDeferredConvertersLocked();

                            // Re-validate each output: it may have been removed during the out-of-lock convert.
                            if (primary is not null)
                            {
                                if (IsStillCurrentRouteLocked(PrimaryOutputId) &&
                                    _owner._outputs.TryGetValue(PrimaryOutputId, out var pe))
                                    pe.Output.Submit(primary);
                                else
                                    primary.Dispose();
                                primary = null;
                            }

                            for (var i = 0; i < branchFrames.Length; i++)
                            {
                                var f = branchFrames[i];
                                if (f is null) continue;
                                if (IsStillCurrentRouteLocked(plan.OutputIds[i + 1]) &&
                                    _owner._outputs.TryGetValue(plan.OutputIds[i + 1], out var be))
                                {
                                    be.Output.Submit(f);
                                    branchFrames[i] = null;
                                }
                                else
                                {
                                    f.Dispose();
                                    branchFrames[i] = null;
                                }
                            }
                        }
                    }
                    catch
                    {
                        lock (_owner._gate)
                        {
                            _convertInFlight = false;
                            DrainDeferredConvertersLocked();
                        }
                        converterReadback?.Dispose();
                        for (var i = 0; i < branchFrames.Length; i++)
                        {
                            branchFrames[i]?.Dispose();
                            branchFrames[i] = null; // scratch is reused - leave every slot null
                        }
                        primary?.Dispose();
                        throw;
                    }
                }
            }
            finally
            {
                if (submitStarted != 0)
                    MaybeLogSlowSubmitPhase(submitStarted);
            }
        }

        private void MaybeLogSlowSubmitPhase(long started)
        {
            var elapsedMs = MediaDiagnostics.ElapsedMillisecondsSince(started);
            if (elapsedMs < SlowSubmitPhaseWarningMs ||
                !MediaDiagnostics.TryUpdateThrottle(ref _lastSlowSubmitPhaseLogTicks, TimeSpan.FromSeconds(2)))
                return;

            VideoRouter.Trace.LogWarning(
                "SubmitPhased: input={InputId} took {ElapsedMs:0.00}ms (threshold={ThresholdMs:0.00}ms, routes={Routes}, configured={Configured}, format={Format})",
                Id,
                elapsedMs,
                SlowSubmitPhaseWarningMs,
                RoutedOutputIds.Count,
                Configured,
                NegotiatedFormat);
        }

        /// <summary>Returns the submit plan for <paramref name="frame"/>: the route-derived arrays come from
        /// the version-stamped cache (rebuilt only after a route/converter mutation); only the
        /// frame-backing-dependent readback/fan-out bits are computed per call. Caller holds <c>_owner._gate</c>,
        /// so phase 2 can convert without it.</summary>
        private SubmitPlan GetSubmitPlanLocked(VideoFrame frame)
        {
            if (_planVersion != _routesVersion)
                RebuildSubmitPlanCacheLocked();

            var hasHw = frame.DmabufNv12 is not null || frame.DmabufP010 is not null
                        || frame.DmabufP016 is not null || frame.Win32Nv12 is not null;
            // Both a submit-thread converter and a pump-thread converter need CPU planes, so a hardware
            // (dma-buf / Win32) frame must be read back for either before it can be repacked.
            var needsReadback = hasHw && _planAnyConverterOrPump;
            // Any CPU-backed format can fan out into shared zero-copy views; a branch that converts on the
            // submit thread forces the per-branch path, but a pump-converts branch (repacked on its own
            // thread) does not - it just receives a raw view to convert there.
            var canFanOut = !hasHw && !_planAnySyncConverter;
            return new SubmitPlan(_planOutputIds, _planConverters, _planPumpConverts,
                NegotiatedFormat!.Value, needsReadback, canFanOut);
        }

        private void RebuildSubmitPlanCacheLocked()
        {
            var ids = RoutedOutputIds.ToArray();
            var n = ids.Length;
            var converters = new IVideoCpuFrameConverter?[n - 1];
            var pumpConverts = new bool[n - 1];
            var anySync = false;
            var anyConvOrPump = false;
            for (var i = 1; i < n; i++)
            {
                IVideoCpuFrameConverter? conv = null;
                var pc = false;
                if (_paths.TryGetValue(ids[i], out var st))
                {
                    conv = st.Converter;
                    pc = st.PumpConverts;
                }

                converters[i - 1] = conv;
                pumpConverts[i - 1] = pc;
                if (conv is not null) anySync = true;
                if (conv is not null || pc) anyConvOrPump = true;
            }

            _planOutputIds = ids;
            _planConverters = converters;
            _planPumpConverts = pumpConverts;
            _planAnySyncConverter = anySync;
            _planAnyConverterOrPump = anyConvOrPump;
            _planVersion = _routesVersion;
            Interlocked.Increment(ref _owner._submitPlanRebuilds);
        }

        private bool IsStillCurrentRouteLocked(string outputId)
            => _owner._inputs.TryGetValue(Id, out var current)
               && ReferenceEquals(current, this)
               && RoutedSet.Contains(outputId);
    }

    private readonly record struct SubmitPlan(
        string[] OutputIds,
        IVideoCpuFrameConverter?[] BranchConverters,
        bool[] PumpConvertsBranch,
        VideoFormat NegotiatedFmt,
        bool NeedsHwReadback,
        bool CanCpuFanOut);

    /// <param name="Converter">When null, branch uses a shared CPU fan-out view (<see cref="VideoFrame.TryCreateCpuFanOutViews"/>, or the NV12-specific <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/>) when the frame is CPU-backed, else <see cref="VideoFrameCpuClone.DuplicateCpuBacking"/> / a dma-buf / Win32 shared reference. Non-null means the conversion runs synchronously on the submit thread.</param>
    /// <param name="PumpConverts">True when this branch's <see cref="IVideoCpuFrameConverter"/> was handed to its <see cref="VideoOutputPump"/> (<see cref="VideoOutputPump.SetBranchConverter"/>) to run on the pump's drain thread; <see cref="Converter"/> is then null here and the branch receives an unconverted (fan-out) view to repack off the submit thread.</param>
    private readonly record struct OutputPathState(IVideoCpuFrameConverter? Converter, bool PumpConverts = false);

    private sealed class VideoRouterInputOutput : IVideoOutput, IVideoOutputD3D11GlBorrowSetup, IVideoOutputQueueControl
    {
        private readonly VideoRouter _owner;
        private readonly InputRegistration _reg;
        private readonly IReadOnlyList<PixelFormat> _accepted;
        private bool _sinkDisposed;

        public VideoRouterInputOutput(VideoRouter owner, InputRegistration reg, IReadOnlyList<PixelFormat> accepted)
        {
            _owner = owner;
            _reg = reg;
            _accepted = accepted;
        }

        public void MarkDisposed() => _sinkDisposed = true;

        internal bool IsSinkDisposed => _sinkDisposed;

        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

        public VideoFormat Format
        {
            get
            {
                lock (_owner._gate)
                {
                    ObjectDisposedException.ThrowIf(_sinkDisposed, this);
                    if (!_reg.Configured || _reg.NegotiatedFormat is not { } f)
                        throw new InvalidOperationException("VideoRouter input output is not configured yet.");
                    return f;
                }
            }
        }

        public void Configure(VideoFormat format)
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_sinkDisposed, this);
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                _reg.TearDownPaths();
                _reg.ApplyConfigureLocked(format);
            }
        }

        public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource)
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_sinkDisposed, this);
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                _reg.SetBorrowVideoSourceForWin32Nv12Gl(videoSource);
            }
        }

        public void Submit(VideoFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            // SubmitPhased does the disposed checks under _gate in its snapshot phase, then converts with
            // the lock released (P2-2). Do NOT wrap it in lock(_gate) here.
            _reg.SubmitPhased(frame, this);
        }

        public void AbandonQueuedFrames() => _reg.AbandonQueuedFrames(this);

        public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            _reg.WaitForIdle(this, timeout, cancellationToken);
    }
}

/// <summary>Return value of <see cref="VideoRouter.AddInput"/> - stable id plus the <see cref="IVideoOutput"/> the decoder connects to.</summary>
public readonly record struct VideoRouterInputRegistration(string Id, IVideoOutput Output);
