using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Video;

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
/// <see cref="AddInput"/> — its <see cref="IVideoOutput.AcceptedPixelFormats"/> are re-exposed
/// on the returned <see cref="IVideoOutput"/>. Branch outputs receive
/// <see cref="IVideoCpuFrameConverter"/> conversion (via <see cref="VideoCpuFrameConverterRegistry"/>)
/// when needed; the shipping converter is FFmpeg's swscale-backed implementation.
/// </para>
/// <para>
/// <strong>DRM dma-buf NV12</strong> can fan out to multiple outputs when every
/// branch accepts the same negotiated <see cref="PixelFormat.Nv12"/> (no
/// per-branch <see cref="IVideoCpuFrameConverter"/>). <strong>P010</strong> and <strong>P016</strong> dma-buf fan-out follow the same rule
/// when every branch stays <see cref="PixelFormat.P010"/> or <see cref="PixelFormat.P016"/> via <see cref="VideoFrame.CreateP010DmabufSharedReference"/> or <see cref="VideoFrame.CreateP016DmabufSharedReference"/>.
/// Each NV12 output receives an independent <see cref="VideoFrame"/> sharing refcounted fds via
/// <see cref="VideoFrame.CreateNv12DmabufSharedReference"/>; each P010 output uses <see cref="VideoFrame.CreateP010DmabufSharedReference"/>; each P016 output uses <see cref="VideoFrame.CreateP016DmabufSharedReference"/>.
/// <strong>CPU NV12</strong> with the same constraints uses <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/> so every output shares one backing instead of <see cref="VideoFrameCpuClone.DuplicateCpuBacking"/>.
/// When a branch needs a different pixel format and the input is Linux DRM dma-buf semi-planar, <see cref="VideoDmabufCpuReadback"/> performs a best-effort <c>mmap</c> copy into CPU memory for that branch’s <see cref="IVideoCpuFrameConverter"/> (may fail for tiled / protected buffers — see that type).
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
    private readonly Dictionary<string, OutputRegistration> _outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InputRegistration> _inputs = new(StringComparer.Ordinal);
    /// <summary>Each output id → owning input id (exclusive).</summary>
    private readonly Dictionary<string, string> _outputOwner = new(StringComparer.Ordinal);
    private int _idCounter;
    private bool _disposed;
    private EventHandler<VideoRouterPumpPressureEventArgs>? _pumpPressure;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Video.VideoRouter");

    public VideoRouter(ILogger? logger = null) => _log = logger;

    /// <summary>Optional: raised when an async <see cref="VideoOutputPump"/> on an output drops an oldest frame; arguments include <see cref="VideoRouterPumpPressureEventArgs.OutputId"/> and cumulative drops. Handler runs on the pump's <see cref="IVideoOutput.Submit"/> caller thread (typically the router input path).</summary>
    public event EventHandler<VideoRouterPumpPressureEventArgs>? PumpPressure
    {
        add => _pumpPressure += value;
        remove => _pumpPressure -= value;
    }

    /// <summary>Registers a output. <paramref name="id"/> defaults to <c>vout_1</c>, <c>vout_2</c>, …</summary>
    /// <param name="asyncPump">
    /// Explicit <see cref="VideoOutputPump"/> attach options. Overrides the default pump-by-default path
    /// described under <paramref name="synchronous"/>. The router always disposes the pump on
    /// <see cref="Dispose"/> (the pump is the registered output); the pump itself disposes the inner output
    /// only when <see cref="VideoOutputPumpAttachOptions.DisposeInnerOutputWhenPumpDisposes"/> is set.
    /// </param>
    /// <param name="synchronous">
    /// When <c>true</c>, the output is registered as-is — <see cref="IVideoOutput.Submit"/> runs on the
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
    /// <summary>Snapshot of registered output ids.</summary>
    public IReadOnlyList<string> GetRegisteredOutputIds()
    {
        lock (_gate)
            return _outputs.Keys.ToArray();
    }

    public string AddOutput(IVideoOutput output, string? id = null, bool disposeOutputOnRouterDispose = false,
        VideoOutputPumpAttachOptions? asyncPump = null, bool synchronous = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (synchronous && asyncPump is not null)
            throw new ArgumentException(
                "synchronous: true is mutually exclusive with asyncPump — pick one.",
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        IDisposable? ownedToDispose = null;
        lock (_gate)
        {
            if (!_outputs.TryGetValue(outputId, out var removed)) return false;

            foreach (var kv in _inputs.ToArray())
            {
                if (kv.Value.PrimaryOutputId == outputId)
                    RemoveInputLocked(kv.Key);
            }

            _outputs.Remove(outputId);
            _outputOwner.Remove(outputId);

            foreach (var reg in _inputs.Values.ToArray())
            {
                if (reg.RemoveOutputFromRoutes(outputId))
                    ReconfigureInputIfNeededLocked(reg);
            }

            // The router owns the registered output/pump when it was added with
            // disposeOutputOnRouterDispose (always true on the default pump-by-default
            // path). Dispose it on removal too — Dispose() does, but RemoveOutput
            // previously dropped the registration without disposing, leaking the pump's
            // drainer thread and the inner output's native resources.
            if (removed.DisposeOutputOnRouterDispose && removed.Output is IDisposable d)
                ownedToDispose = d;
        }

        // Outside the lock: VideoOutputPump.Dispose joins its drainer thread, so we must
        // not hold _gate during teardown.
        if (ownedToDispose is not null)
            MediaDiagnostics.SwallowDisposeErrors(ownedToDispose.Dispose, $"VideoRouter.RemoveOutput: output '{outputId}'");

        return true;
    }

    /// <summary>
    /// Registers a logical input bound to <paramref name="primaryOutputId"/> (negotiation lead).
    /// That output must be free — it is claimed immediately.
    /// </summary>
    public VideoRouterInputRegistration AddInput(string primaryOutputId, string? inputId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(primaryOutputId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            inputId ??= $"vin_{++_idCounter}";
            if (_inputs.ContainsKey(inputId))
                throw new ArgumentException($"video input id '{inputId}' is already registered", nameof(inputId));
            if (!_outputs.TryGetValue(primaryOutputId, out var primaryOut))
                throw new ArgumentException($"unknown video output id '{primaryOutputId}'", nameof(primaryOutputId));
            if (_outputOwner.ContainsKey(primaryOutputId))
            {
                var msg =
                    $"VideoRouter: cannot add input '{inputId}' — output '{primaryOutputId}' is already routed from input '{_outputOwner[primaryOutputId]}'.";
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
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
                    "VideoRouter: declined route — output {OutputId} is owned by input {Owner}; attempted input {Attempted}.",
                    outputId, owner, inputId);
                return false;
            }

            _outputOwner[outputId] = inputId;
            reg.RoutedOutputIds.Add(outputId);
            reg.RoutedSet.Add(outputId);
            Trace.LogDebug("TryAddRoute: input={InputId} -> output={OutputId} (totalRoutes={Total})",
                inputId, outputId, reg.RoutedOutputIds.Count);
            ReconfigureInputIfNeededLocked(reg);
            return true;
        }
    }

    /// <summary>Removes one fan-out branch. The primary output for an input cannot be removed while the input exists — remove the input instead.</summary>
    public bool TryRemoveRoute(string inputId, string outputId, [NotNullWhen(false)] out string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        errorMessage = null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (!_inputs.TryGetValue(inputId, out var reg))
            {
                errorMessage = $"unknown video input id '{inputId}'";
                return false;
            }
            if (outputId == reg.PrimaryOutputId)
            {
                errorMessage =
                    $"cannot remove primary output '{outputId}' from input '{inputId}' — call RemoveInput instead.";
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
            return true;
        }
    }

    /// <summary>Removes an input, all of its routes, and disposes its per-output converters.</summary>
    public bool RemoveInput(string inputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            return RemoveInputLocked(inputId);
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
        if (_disposed) return;
        _disposed = true;
        _pumpPressure = null;
        lock (_gate)
        {
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
        }
    }

    /// <summary>When <paramref name="outputId"/> was registered with an async <see cref="VideoOutputPump"/>, returns its counters.</summary>
    public bool TryGetVideoOutputPumpMetrics(string outputId, out VideoOutputPumpMetrics metrics)
    {
        metrics = default;
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            if (!_outputs.TryGetValue(outputId, out var reg))
                return false;
            if (reg.Output is VideoOutputPump pump)
            {
                metrics = new VideoOutputPumpMetrics(
                    pump.DroppedFrames,
                    pump.SubmittedFrames,
                    pump.MaxQueueDepth,
                    pump.CurrentQueuedDepth);
                return true;
            }
        }

        return false;
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
            outputId != PrimaryOutputId && _paths.TryGetValue(outputId, out var st) && st.Converter != null;

        public void ApplyConfigureLocked(VideoFormat negotiated)
        {
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            if (RoutedOutputIds.Count == 0)
                throw new InvalidOperationException($"VideoRouter input '{Id}' has no routed outputs.");

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
                var branchFmt = VideoOutputFanoutFormats.PickBranchPixelFormat(negotiated, branchOutput.AcceptedPixelFormats);
                var needsConversion = branchFmt != negotiated.PixelFormat;
                IVideoCpuFrameConverter? conv = null;
                if (needsConversion)
                {
                    conv = VideoCpuFrameConverterRegistry.Create();
                    conv.Configure(negotiated.PixelFormat, branchFmt, negotiated.Width, negotiated.Height);
                }
                branchOutput.Configure(new VideoFormat(negotiated.Width, negotiated.Height, branchFmt, negotiated.FrameRate));
                _paths[oid] = new OutputPathState(Converter: conv);
            }

            if (RoutedOutputIds.Count > 1 && negotiated.PixelFormat is PixelFormat.Nv12 or PixelFormat.P010 or PixelFormat.P016)
            {
                for (var i = 1; i < RoutedOutputIds.Count; i++)
                {
                    var oid = RoutedOutputIds[i];
                    if (_paths.TryGetValue(oid, out var st) && st.Converter != null)
                    {
                        if (Interlocked.Exchange(ref _nv12FanoutCpuBranchWarned, 1) == 0)
                        {
                            _owner._log?.LogWarning(
                                "VideoRouter input {InputId}: NV12/P010/P016 fan-out includes branch output(s) that use a CPU pixel converter — if the decoder delivers DRM dma-buf NV12/P010/P016, the router attempts an mmap readback for swscale (see VideoDmabufCpuReadback); Win32 shared NV12 with a branch converter is still unsupported.",
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
            foreach (var p in _paths.Values)
            {
                if (p.Converter is not { } conv) continue;
                if (_convertInFlight)
                    _deferredConverters.Add(conv); // a submit may be converting with it — free it when the submit drains
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

                    // Single output: no branch conversion — deliver directly under the lock (fast path).
                    if (RoutedOutputIds.Count == 1)
                    {
                        _owner._outputs[RoutedOutputIds[0]].Output.Submit(frame);
                        return;
                    }

                    plan = BuildSubmitPlanLocked(frame);
                    _convertInFlight = true; // lease the snapshotted converters from disposal by TearDownPaths
                }

                var n = plan.OutputIds.Length;
                var hint = frame.ColorTransferHint;
                VideoFrame? primary = frame;
                var branchFrames = new VideoFrame?[n - 1];
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
                                $"VideoRouter input '{Id}': cannot convert Win32 D3D11 shared-handle NV12 for a branch output — use NV12 outputs for all routes, a single output, or software decode (e.g. VideoPlaybackSmoke --no-hw).");
                        }
                        if (frame.DmabufNv12 is not null && !VideoDmabufCpuReadback.TryCreateNv12CpuCopy(frame, out converterReadback))
                        {
                            frame.Dispose();
                            throw new NotSupportedException(
                                $"VideoRouter input '{Id}': DRM dma-buf NV12 could not be mmap-read for a branch CPU converter — use matching outputs, a single output, or software decode (e.g. VideoPlaybackSmoke --no-hw).");
                        }
                        if (frame.DmabufP010 is not null && !VideoDmabufCpuReadback.TryCreateP010CpuCopy(frame, out converterReadback))
                        {
                            frame.Dispose();
                            throw new NotSupportedException(
                                $"VideoRouter input '{Id}': DRM dma-buf P010 could not be mmap-read for a branch CPU converter — use matching outputs, a single output, or software decode.");
                        }
                        if (frame.DmabufP016 is not null && !VideoDmabufCpuReadback.TryCreateP016CpuCopy(frame, out converterReadback))
                        {
                            frame.Dispose();
                            throw new NotSupportedException(
                                $"VideoRouter input '{Id}': DRM dma-buf P016 could not be mmap-read for a branch CPU converter — use matching outputs, a single output, or software decode.");
                        }
                    }

                    if (plan.CanNv12FanOut &&
                        VideoFrame.TryCreateNv12CpuFanOutViews(frame, n, hint, out var fanViews))
                    {
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
                            branchFrames[i - 1] = conv != null
                                ? conv.Convert(converterReadback ?? frame, hint)
                                : frame.DmabufNv12 is not null
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
                            if (_owner._outputs.TryGetValue(PrimaryOutputId, out var pe))
                                pe.Output.Submit(primary);
                            else
                                primary.Dispose();
                            primary = null;
                        }

                        for (var i = 0; i < branchFrames.Length; i++)
                        {
                            var f = branchFrames[i];
                            branchFrames[i] = null;
                            if (f is null) continue;
                            if (_owner._outputs.TryGetValue(plan.OutputIds[i + 1], out var be))
                                be.Output.Submit(f);
                            else
                                f.Dispose();
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
                    foreach (var bf in branchFrames)
                        bf?.Dispose();
                    primary?.Dispose();
                    throw;
                }
            }
        }

        /// <summary>Snapshots the routed outputs + their branch converters + fan-out/readback decisions for a
        /// submit, so phase 2 can convert without holding <c>_owner._gate</c>. Caller holds <c>_owner._gate</c>.</summary>
        private SubmitPlan BuildSubmitPlanLocked(VideoFrame frame)
        {
            var ids = RoutedOutputIds.ToArray();
            var n = ids.Length;
            var converters = new IVideoCpuFrameConverter?[n - 1];
            var hasHw = frame.DmabufNv12 is not null || frame.DmabufP010 is not null
                        || frame.DmabufP016 is not null || frame.Win32Nv12 is not null;
            var needsReadback = false;
            for (var i = 1; i < n; i++)
            {
                var conv = _paths.TryGetValue(ids[i], out var st) ? st.Converter : null;
                converters[i - 1] = conv;
                if (conv is not null && hasHw)
                    needsReadback = true;
            }

            var negotiated = NegotiatedFormat!.Value;
            var canFanOut = negotiated.PixelFormat == PixelFormat.Nv12 && !hasHw;
            if (canFanOut)
            {
                for (var i = 1; i < n; i++)
                {
                    if (converters[i - 1] is not null) { canFanOut = false; break; }
                }
            }

            return new SubmitPlan(ids, converters, negotiated, needsReadback, canFanOut);
        }
    }

    private readonly record struct SubmitPlan(
        string[] OutputIds,
        IVideoCpuFrameConverter?[] BranchConverters,
        VideoFormat NegotiatedFmt,
        bool NeedsHwReadback,
        bool CanNv12FanOut);

    /// <param name="Converter">When null, branch uses <see cref="VideoFrameCpuClone.DuplicateCpuBacking"/> for CPU frames unless <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/> applies (negotiated <see cref="PixelFormat.Nv12"/>, no dma-buf / Win32 backings, no branch converter).</param>
    private readonly record struct OutputPathState(IVideoCpuFrameConverter? Converter);

    private sealed class VideoRouterInputOutput : IVideoOutput, IVideoOutputD3D11GlBorrowSetup
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
    }
}

/// <summary>Return value of <see cref="VideoRouter.AddInput"/> — stable id plus the <see cref="IVideoOutput"/> the decoder connects to.</summary>
public readonly record struct VideoRouterInputRegistration(string Id, IVideoOutput Output);
