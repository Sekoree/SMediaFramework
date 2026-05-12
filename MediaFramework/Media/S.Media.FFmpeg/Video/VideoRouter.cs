using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using S.Media.Core.Video;

namespace S.Media.FFmpeg.Video;

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
/// per-branch <see cref="VideoCpuFrameConverter"/>). Each output receives an
/// independent <see cref="VideoFrame"/> sharing refcounted file descriptors
/// via <see cref="VideoFrame.CreateNv12DmabufSharedReference"/>. If any branch
/// needs a different pixel format, use CPU decode or a single dma-buf output.
/// </para>
/// <para>
/// For slow sinks (NDI, remote encoders), pass <see cref="VideoSinkPumpAttachOptions"/> to
/// <see cref="AddOutput"/> or wrap the sink in <see cref="VideoSinkPump"/> before registering.
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

    public VideoRouter(ILogger? logger = null) => _log = logger;

    /// <summary>Registers a sink. <paramref name="id"/> defaults to <c>vout_1</c>, <c>vout_2</c>, …</summary>
    /// <param name="asyncPump">
    /// When set, the sink is wrapped in a <see cref="VideoSinkPump"/> so <see cref="IVideoSink.Submit"/> does not block
    /// the router. The router always disposes the pump on <see cref="Dispose"/> (the pump is the registered sink).
    /// </param>
    public string AddOutput(IVideoSink sink, string? id = null, bool disposeSinkOnRouterDispose = false,
        VideoSinkPumpAttachOptions? asyncPump = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                sink = new VideoSinkPump(sink, ap.MaxQueuedFrames, pumpName, ap.Logger,
                    disposeInnerOnDispose: ap.DisposeInnerSinkWhenPumpDisposes);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var reg in _inputs.Values)
            {
                reg.TearDownPaths();
                reg.Sink.MarkDisposed();
            }
            _inputs.Clear();
            _outputOwner.Clear();
            foreach (var o in _outputs.Values)
            {
                if (o.DisposeSinkOnRouterDispose && o.Sink is IDisposable d)
                    d.Dispose();
            }
            _outputs.Clear();
        }
    }

    /// <summary>When <paramref name="outputId"/> was registered with an async <see cref="VideoSinkPump"/>, returns its counters.</summary>
    public bool TryGetVideoSinkPumpMetrics(string outputId, out long droppedFrames, out long submittedFrames)
    {
        droppedFrames = 0;
        submittedFrames = 0;
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            if (!_outputs.TryGetValue(outputId, out var reg))
                return false;
            if (reg.Sink is VideoSinkPump pump)
            {
                droppedFrames = pump.DroppedFrames;
                submittedFrames = pump.SubmittedFrames;
                return true;
            }
        }

        return false;
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

        public void ApplyConfigureLocked(VideoFormat negotiated)
        {
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            if (RoutedOutputIds.Count == 0)
                throw new InvalidOperationException($"VideoRouter input '{Id}' has no routed outputs.");

            NegotiatedFormat = negotiated;
            var primarySink = _owner._outputs[PrimaryOutputId].Sink;
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

        public void SubmitLocked(VideoFrame frame)
        {
            if (!Configured || NegotiatedFormat is null)
            {
                frame.Dispose();
                throw new InvalidOperationException($"VideoRouter input '{Id}'.Submit called before Configure");
            }

            if (frame.DmabufNv12 is not null && RoutedOutputIds.Count > 1)
            {
                for (var j = 1; j < RoutedOutputIds.Count; j++)
                {
                    if (_paths[RoutedOutputIds[j]].Converter != null)
                    {
                        frame.Dispose();
                        throw new NotSupportedException(
                            "VideoRouter cannot convert DRM dma-buf NV12 for a branch output — use NV12 sinks for all routes, a single output, or CPU decode.");
                    }
                }
            }

            if (frame.Win32Nv12 is not null && RoutedOutputIds.Count > 1)
            {
                for (var j = 1; j < RoutedOutputIds.Count; j++)
                {
                    if (_paths[RoutedOutputIds[j]].Converter != null)
                    {
                        frame.Dispose();
                        throw new NotSupportedException(
                            "VideoRouter cannot convert Win32 D3D11 shared-handle NV12 for a branch output — use NV12 sinks for all routes, a single output, or CPU decode.");
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
                for (var i = 1; i < RoutedOutputIds.Count; i++)
                {
                    var oid = RoutedOutputIds[i];
                    var path = _paths[oid];
                    branchFrames[i - 1] = path.Converter != null
                        ? path.Converter.Convert(frame, frame.ColorTransferHint)
                        : frame.DmabufNv12 is not null
                            ? VideoFrame.CreateNv12DmabufSharedReference(frame)
                            : frame.Win32Nv12 is not null
                                ? VideoFrame.CreateNv12Win32SharedReference(frame)
                                : VideoCpuFrameConverter.DuplicateCpuBacking(frame, frame.ColorTransferHint);
                }

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
                foreach (var bf in branchFrames)
                    bf?.Dispose();
                frame?.Dispose();
                throw;
            }
        }
    }

    /// <param name="Converter">When null, branch uses <see cref="VideoCpuFrameConverter.DuplicateCpuBacking"/> (same pixel format as negotiated).</param>
    private readonly record struct OutputPathState(VideoCpuFrameConverter? Converter);

    private sealed class VideoRouterInputSink : IVideoSink
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
