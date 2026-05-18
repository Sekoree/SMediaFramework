using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.FFmpeg.Video;

/// <summary>
/// Pixel format delivered to one <see cref="VideoRouter"/> output after negotiation and fan-out
/// configuration (see <see cref="VideoRouter.TryGetInputFanOutPixelFormats"/>).
/// </summary>
/// <param name="UsesRouterCpuConverter">
/// True when this branch uses <see cref="VideoCpuFrameConverter"/> to convert from the negotiated stream format.
/// Always false for the primary output.
/// </param>
public readonly record struct VideoRouterFanOutPixelFormat(string OutputId, PixelFormat PixelFormat, bool UsesRouterCpuConverter);

/// <summary>
/// Routes one or more logical video inputs to many <see cref="IVideoSink"/> outputs.
/// Each <strong>output</strong> may receive from <strong>at most one</strong> input at a time
/// (unlike <see cref="Audio.AudioRouter"/>, which sums many sources into one sink).
/// A single input may still fan out to many outputs (one stream, multiple displays / encoders).
/// </summary>
/// <remarks>
/// <para>
/// Pixel negotiation uses the <strong>primary</strong> output registered with
/// <see cref="AddInput"/> — its <see cref="IVideoSink.AcceptedPixelFormats"/> are re-exposed
/// on the returned <see cref="IVideoSink"/>. Branch outputs receive
/// <see cref="VideoCpuFrameConverter"/> conversion when needed, mirroring <see cref="VideoOutputRouter"/>.
/// </para>
/// <para>
/// <strong>DRM dma-buf NV12</strong> can fan out to multiple sinks when every
/// branch accepts the same negotiated <see cref="PixelFormat.Nv12"/> (no
/// per-branch <see cref="VideoCpuFrameConverter"/>). <strong>P010</strong> and <strong>P016</strong> dma-buf fan-out follow the same rule
/// when every branch stays <see cref="PixelFormat.P010"/> or <see cref="PixelFormat.P016"/> via <see cref="VideoFrame.CreateP010DmabufSharedReference"/> or <see cref="VideoFrame.CreateP016DmabufSharedReference"/>.
/// Each NV12 output receives an independent <see cref="VideoFrame"/> sharing refcounted fds via
/// <see cref="VideoFrame.CreateNv12DmabufSharedReference"/>; each P010 output uses <see cref="VideoFrame.CreateP010DmabufSharedReference"/>; each P016 output uses <see cref="VideoFrame.CreateP016DmabufSharedReference"/>.
/// <strong>CPU NV12</strong> with the same constraints uses <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/> so every sink shares one backing instead of <see cref="VideoCpuFrameConverter.DuplicateCpuBacking"/>.
/// When a branch needs a different pixel format and the input is Linux DRM dma-buf semi-planar, <see cref="VideoDmabufCpuReadback"/> performs a best-effort <c>mmap</c> copy into CPU memory for that branch’s <see cref="VideoCpuFrameConverter"/> (may fail for tiled / protected buffers — see that type).
/// <strong>Win32</strong> shared NV12 with a branch CPU converter remains unsupported at <see cref="IVideoSink.Submit"/> time.
/// Configure-time cannot know whether NV12, P010, or P016 frames will be CPU-backed or hardware-backed; when hardware-backed semi-planar dma-buf frames are delivered,
/// the router logs a warning if any branch uses a CPU converter (see <see cref="InputRegistration.ApplyConfigureLocked"/>).
/// </para>
/// <para>
/// For slow sinks (NDI, remote encoders), pass <see cref="VideoSinkPumpAttachOptions"/> to
/// <see cref="AddOutput"/> or wrap the sink in <see cref="VideoSinkPump"/> before registering.
/// Use <see cref="TryGetVideoSinkPumpMetrics(string, out VideoSinkPumpMetrics)"/> for queue depth, drops, and capacity.
/// Subscribe to <see cref="PumpPressure"/> when an output uses <see cref="VideoSinkPumpAttachOptions"/> to react to queue-full drops without polling metrics (same role as <see cref="S.Media.Core.Audio.AudioRouter.PumpPressure"/> for audio).
/// </para>
/// <para>
/// <see cref="Dispose"/> tears down inputs then owned output sinks under the router lock. In <c>DEBUG</c> builds, failures from
/// <see cref="InputRegistration.TearDownPaths"/>, <see cref="IVideoSink.MarkDisposed"/>, or an individual sink <see cref="IDisposable.Dispose"/>
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

    public VideoRouter(ILogger? logger = null) => _log = logger;

    /// <summary>Optional: raised when an async <see cref="VideoSinkPump"/> on an output drops an oldest frame; arguments include <see cref="VideoRouterPumpPressureEventArgs.OutputId"/> and cumulative drops. Handler runs on the pump's <see cref="IVideoSink.Submit"/> caller thread (typically the router input path).</summary>
    public event EventHandler<VideoRouterPumpPressureEventArgs>? PumpPressure
    {
        add => _pumpPressure += value;
        remove => _pumpPressure -= value;
    }

    /// <summary>Registers a sink. <paramref name="id"/> defaults to <c>vout_1</c>, <c>vout_2</c>, …</summary>
    /// <param name="asyncPump">
    /// Explicit <see cref="VideoSinkPump"/> attach options. Overrides the default pump-by-default path
    /// described under <paramref name="synchronous"/>. The router always disposes the pump on
    /// <see cref="Dispose"/> (the pump is the registered sink); the pump itself disposes the inner sink
    /// only when <see cref="VideoSinkPumpAttachOptions.DisposeInnerSinkWhenPumpDisposes"/> is set.
    /// </param>
    /// <param name="synchronous">
    /// When <c>true</c>, the sink is registered as-is — <see cref="IVideoSink.Submit"/> runs on the
    /// clock-driver thread. Use this for sinks that are guaranteed to return promptly
    /// (e.g. <see cref="DiscardingVideoSink"/>) or that manage their own threading. Mutually exclusive
    /// with <paramref name="asyncPump"/>; passing both throws <see cref="ArgumentException"/>.
    ///
    /// When <c>false</c> (default) <strong>and</strong> <paramref name="asyncPump"/> is <c>null</c>,
    /// the sink is wrapped in a <see cref="VideoSinkPump"/> with default capacity so a slow
    /// <see cref="IVideoSink.Submit"/> cannot stall the clock thread. The pump propagates dispose to
    /// the inner sink only when <paramref name="disposeSinkOnRouterDispose"/> is <c>true</c>; the pump
    /// itself is always disposed with the router.
    /// </param>
    public string AddOutput(IVideoSink sink, string? id = null, bool disposeSinkOnRouterDispose = false,
        VideoSinkPumpAttachOptions? asyncPump = null, bool synchronous = false)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (synchronous && asyncPump is not null)
            throw new ArgumentException(
                "synchronous: true is mutually exclusive with asyncPump — pick one.",
                nameof(synchronous));

        // Default ergonomics (Phase 2): pump-by-default so new sinks don't silently stall the clock
        // thread on a slow Submit. Callers that know their sink is prompt opt out with synchronous: true;
        // callers that want a tuned pump pass asyncPump explicitly.
        if (!synchronous && asyncPump is null)
        {
            asyncPump = new VideoSinkPumpAttachOptions(
                DisposeInnerSinkWhenPumpDisposes: disposeSinkOnRouterDispose);
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
                var pumpName = ap.ThreadName ?? $"VideoSinkPump-{id}";
                var pump = new VideoSinkPump(sink, ap.MaxQueuedFrames, pumpName, ap.Logger,
                    disposeInnerOnDispose: ap.DisposeInnerSinkWhenPumpDisposes);
                var outputId = id;
                pump.PumpPressure += (_, e) => RaisePumpPressure(outputId, e.DroppedFramesTotal);
                sink = pump;
                disposeSinkOnRouterDispose = true;
            }

            _outputs.Add(id, new OutputRegistration(id, sink, disposeSinkOnRouterDispose));
            return id;
        }
    }

    /// <summary>Removes an output and any routes targeting it. Inputs whose primary output is removed are removed entirely.</summary>
    public bool RemoveOutput(string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (!_outputs.ContainsKey(outputId)) return false;

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

            return true;
        }
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

            var accepted = primaryOut.Sink.AcceptedPixelFormats;
            var reg = new InputRegistration(this, inputId, primaryOutputId, accepted);
            _inputs.Add(inputId, reg);
            _outputOwner[primaryOutputId] = inputId;
            reg.RoutedOutputIds.Add(primaryOutputId);
            reg.RoutedSet.Add(primaryOutputId);
            return new VideoRouterInputRegistration(inputId, reg.Sink);
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
        reg.Sink.MarkDisposed();
        return true;
    }

    /// <summary>
    /// When <paramref name="inputId"/> is configured, returns the negotiated stream <see cref="VideoFormat"/> and
    /// each routed output's sink pixel format in route order (primary output first).
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
                var px = oreg.Sink.Format.PixelFormat;
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
                try
                {
                    reg.TearDownPaths();
                    reg.Sink.MarkDisposed();
                }
#if DEBUG
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, "VideoRouter.Dispose: input teardown");
                }
#else
                catch
                {
                    // best effort — continue clearing router inputs
                }
#endif
            }

            _inputs.Clear();
            _outputOwner.Clear();
            foreach (var o in _outputs.Values)
            {
                if (!o.DisposeSinkOnRouterDispose || o.Sink is not IDisposable d)
                    continue;
                try
                {
                    d.Dispose();
                }
#if DEBUG
                catch (Exception ex)
                {
                    MediaDiagnostics.LogError(ex, $"VideoRouter.Dispose: output sink '{o.Id}'");
                }
#else
                catch
                {
                    // best effort — continue disposing other outputs
                }
#endif
            }

            _outputs.Clear();
        }
    }

    /// <summary>When <paramref name="outputId"/> was registered with an async <see cref="VideoSinkPump"/>, returns its counters.</summary>
    public bool TryGetVideoSinkPumpMetrics(string outputId, out VideoSinkPumpMetrics metrics)
    {
        metrics = default;
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            if (!_outputs.TryGetValue(outputId, out var reg))
                return false;
            if (reg.Sink is VideoSinkPump pump)
            {
                metrics = new VideoSinkPumpMetrics(
                    pump.DroppedFrames,
                    pump.SubmittedFrames,
                    pump.MaxQueueDepth,
                    pump.CurrentQueuedDepth);
                return true;
            }
        }

        return false;
    }

    /// <summary>When <paramref name="outputId"/> was registered with an async <see cref="VideoSinkPump"/>, returns dropped and submitted counts.</summary>
    public bool TryGetVideoSinkPumpMetrics(string outputId, out long droppedFrames, out long submittedFrames)
    {
        if (!TryGetVideoSinkPumpMetrics(outputId, out var m))
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

    private static void ApplyD3D11GlBorrowVideoSourceToSink(IVideoSink sink, IVideoSource? videoSource)
    {
        if (sink is not IVideoSinkD3D11GlBorrowSetup borrowSetup)
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

    private sealed class OutputRegistration(string Id, IVideoSink Sink, bool DisposeSinkOnRouterDispose)
    {
        public string Id { get; } = Id;
        public IVideoSink Sink { get; } = Sink;
        public bool DisposeSinkOnRouterDispose { get; } = DisposeSinkOnRouterDispose;
    }

    private sealed class InputRegistration
    {
        private readonly VideoRouter _owner;
        public string Id { get; }
        public string PrimaryOutputId { get; }
        public VideoRouterInputSink Sink { get; }
        public List<string> RoutedOutputIds { get; } = [];
        public HashSet<string> RoutedSet { get; } = new(StringComparer.Ordinal);
        public bool Configured { get; private set; }
        public VideoFormat? NegotiatedFormat { get; private set; }
        private readonly Dictionary<string, OutputPathState> _paths = new(StringComparer.Ordinal);
        private IVideoSource? _borrowVideoSourceForWin32Nv12Gl;
        private int _nv12FanoutCpuBranchWarned;

        public InputRegistration(VideoRouter owner, string id, string primaryOutputId, IReadOnlyList<PixelFormat> accepted)
        {
            _owner = owner;
            Id = id;
            PrimaryOutputId = primaryOutputId;
            Sink = new VideoRouterInputSink(owner, this, accepted);
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

            NegotiatedFormat = negotiated;
            var primarySink = _owner._outputs[PrimaryOutputId].Sink;
            VideoRouter.ApplyD3D11GlBorrowVideoSourceToSink(primarySink, _borrowVideoSourceForWin32Nv12Gl);
            primarySink.Configure(negotiated);
            _paths.Clear();

            for (var i = 1; i < RoutedOutputIds.Count; i++)
            {
                var oid = RoutedOutputIds[i];
                var branchSink = _owner._outputs[oid].Sink;
                var branchFmt = VideoSinkFanoutFormats.PickBranchPixelFormat(negotiated, branchSink.AcceptedPixelFormats);
                var needsConversion = branchFmt != negotiated.PixelFormat;
                VideoCpuFrameConverter? conv = null;
                if (needsConversion)
                {
                    conv = new VideoCpuFrameConverter();
                    conv.Configure(negotiated.PixelFormat, branchFmt, negotiated.Width, negotiated.Height);
                }
                branchSink.Configure(new VideoFormat(negotiated.Width, negotiated.Height, branchFmt, negotiated.FrameRate));
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
                p.Converter?.Dispose();
            _paths.Clear();
            Configured = false;
            NegotiatedFormat = null;
        }

        internal void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource)
        {
            _borrowVideoSourceForWin32Nv12Gl = videoSource;
            var primarySink = _owner._outputs[PrimaryOutputId].Sink;
            VideoRouter.ApplyD3D11GlBorrowVideoSourceToSink(primarySink, _borrowVideoSourceForWin32Nv12Gl);
        }

        public void SubmitLocked(VideoFrame frame)
        {
            if (!Configured || NegotiatedFormat is null)
            {
                frame.Dispose();
                throw new InvalidOperationException($"VideoRouter input '{Id}'.Submit called before Configure");
            }

            var needsHwReadbackForConverter = false;
            if (RoutedOutputIds.Count > 1)
            {
                for (var j = 1; j < RoutedOutputIds.Count; j++)
                {
                    if (_paths[RoutedOutputIds[j]].Converter is null) continue;
                    if (frame.DmabufNv12 is not null || frame.DmabufP010 is not null || frame.DmabufP016 is not null || frame.Win32Nv12 is not null)
                    {
                        needsHwReadbackForConverter = true;
                        break;
                    }
                }
            }

            VideoFrame? converterReadback = null;
            if (needsHwReadbackForConverter)
            {
                if (frame.Win32Nv12 is not null)
                {
                    frame.Dispose();
                    throw new NotSupportedException(
                        $"VideoRouter input '{Id}': cannot convert Win32 D3D11 shared-handle NV12 for a branch output — use NV12 sinks for all routes, a single output, or software decode (e.g. VideoPlaybackSmoke --no-hw).");
                }

                if (frame.DmabufNv12 is not null)
                {
                    if (!VideoDmabufCpuReadback.TryCreateNv12CpuCopy(frame, out converterReadback))
                    {
                        frame.Dispose();
                        throw new NotSupportedException(
                            $"VideoRouter input '{Id}': DRM dma-buf NV12 could not be mmap-read for a branch CPU converter — use matching sinks, a single output, or software decode (e.g. VideoPlaybackSmoke --no-hw).");
                    }
                }
                else if (frame.DmabufP010 is not null)
                {
                    if (!VideoDmabufCpuReadback.TryCreateP010CpuCopy(frame, out converterReadback))
                    {
                        frame.Dispose();
                        throw new NotSupportedException(
                            $"VideoRouter input '{Id}': DRM dma-buf P010 could not be mmap-read for a branch CPU converter — use matching sinks, a single output, or software decode.");
                    }
                }
                else if (frame.DmabufP016 is not null)
                {
                    if (!VideoDmabufCpuReadback.TryCreateP016CpuCopy(frame, out converterReadback))
                    {
                        frame.Dispose();
                        throw new NotSupportedException(
                            $"VideoRouter input '{Id}': DRM dma-buf P016 could not be mmap-read for a branch CPU converter — use matching sinks, a single output, or software decode.");
                    }
                }
            }

            if (RoutedOutputIds.Count == 1)
            {
                _owner._outputs[RoutedOutputIds[0]].Sink.Submit(frame);
                return;
            }

            VideoFrame?[] branchFrames = new VideoFrame?[RoutedOutputIds.Count - 1];
            try
            {
                var n = RoutedOutputIds.Count;
                var negotiatedFmt = NegotiatedFormat!.Value;
                var canNv12CpuFanOut = n > 1
                    && negotiatedFmt.PixelFormat == PixelFormat.Nv12
                    && frame.DmabufNv12 is null
                    && frame.DmabufP010 is null
                    && frame.DmabufP016 is null
                    && frame.Win32Nv12 is null;
                if (canNv12CpuFanOut)
                {
                    for (var j = 1; j < n; j++)
                    {
                        if (_paths[RoutedOutputIds[j]].Converter != null)
                        {
                            canNv12CpuFanOut = false;
                            break;
                        }
                    }
                }

                if (canNv12CpuFanOut &&
                    VideoFrame.TryCreateNv12CpuFanOutViews(frame, n, frame.ColorTransferHint, out var fanViews))
                {
                    for (var i = 1; i < n; i++)
                        branchFrames[i - 1] = fanViews[i];
                    frame.Dispose();
                    frame = fanViews[0];
                }
                else
                {
                    for (var i = 1; i < n; i++)
                    {
                        var oid = RoutedOutputIds[i];
                        var path = _paths[oid];
                        branchFrames[i - 1] = path.Converter != null
                            ? path.Converter.Convert(converterReadback ?? frame, frame.ColorTransferHint)
                            : frame.DmabufNv12 is not null
                                ? VideoFrame.CreateNv12DmabufSharedReference(frame)
                                : frame.DmabufP010 is not null
                                    ? VideoFrame.CreateP010DmabufSharedReference(frame)
                                    : frame.DmabufP016 is not null
                                        ? VideoFrame.CreateP016DmabufSharedReference(frame)
                                        : frame.Win32Nv12 is not null
                                            ? VideoFrame.CreateNv12Win32SharedReference(frame)
                                            : VideoCpuFrameConverter.DuplicateCpuBacking(frame, frame.ColorTransferHint);
                    }
                }

                converterReadback?.Dispose();
                converterReadback = null;

                _owner._outputs[PrimaryOutputId].Sink.Submit(frame);
                frame = null!;

                for (var i = 0; i < branchFrames.Length; i++)
                {
                    var oid = RoutedOutputIds[i + 1];
                    var f = branchFrames[i]!;
                    branchFrames[i] = null;
                    _owner._outputs[oid].Sink.Submit(f);
                }
            }
            catch
            {
                converterReadback?.Dispose();
                foreach (var bf in branchFrames)
                    bf?.Dispose();
                frame?.Dispose();
                throw;
            }
        }
    }

    /// <param name="Converter">When null, branch uses <see cref="VideoCpuFrameConverter.DuplicateCpuBacking"/> for CPU frames unless <see cref="VideoFrame.TryCreateNv12CpuFanOutViews"/> applies (negotiated <see cref="PixelFormat.Nv12"/>, no dma-buf / Win32 backings, no branch converter).</param>
    private readonly record struct OutputPathState(VideoCpuFrameConverter? Converter);

    private sealed class VideoRouterInputSink : IVideoSink, IVideoSinkD3D11GlBorrowSetup
    {
        private readonly VideoRouter _owner;
        private readonly InputRegistration _reg;
        private readonly IReadOnlyList<PixelFormat> _accepted;
        private bool _sinkDisposed;

        public VideoRouterInputSink(VideoRouter owner, InputRegistration reg, IReadOnlyList<PixelFormat> accepted)
        {
            _owner = owner;
            _reg = reg;
            _accepted = accepted;
        }

        public void MarkDisposed() => _sinkDisposed = true;

        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

        public VideoFormat Format
        {
            get
            {
                lock (_owner._gate)
                {
                    ObjectDisposedException.ThrowIf(_sinkDisposed, this);
                    if (!_reg.Configured || _reg.NegotiatedFormat is not { } f)
                        throw new InvalidOperationException("VideoRouter input sink is not configured yet.");
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
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_sinkDisposed, this);
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                _reg.SubmitLocked(frame);
            }
        }
    }
}

/// <summary>Return value of <see cref="VideoRouter.AddInput"/> — stable id plus the <see cref="IVideoSink"/> the decoder connects to.</summary>
public readonly record struct VideoRouterInputRegistration(string Id, IVideoSink Sink);
