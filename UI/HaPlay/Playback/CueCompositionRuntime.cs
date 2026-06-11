using HaPlay.ViewModels;
using Microsoft.Extensions.Logging;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Effects;
using S.Media.Playback;
using S.Media.SDL3;

namespace HaPlay.Playback;

/// <summary>
/// HaPlay adapter for the framework cue composition runtime. It translates HaPlay output-line VMs
/// into framework output leases and chooses the preferred compositor backend.
/// </summary>
internal sealed class CueCompositionRuntime : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.CueCompositionRuntime");

    private readonly CueComposition _composition;
    private readonly ClipCompositionRuntime _inner;
    private readonly HashSet<Guid> _leasedLineIds = new();

    public CueCompositionRuntime(
        CueComposition composition,
        IReadOnlyList<OutputLineViewModel> targetLines,
        OutputManagementViewModel outputs,
        IReadOnlyDictionary<Guid, CueOutputMapping?>? mappingsByLine = null)
    {
        _composition = composition;
        var definition = new ClipCompositionDefinition(
            composition.Id.ToString("N"),
            composition.Name,
            composition.Width,
            composition.Height,
            composition.FrameRateNum,
            composition.FrameRateDen);

        var leases = BuildOutputLeases(composition, targetLines, outputs, mappingsByLine);
        foreach (var lease in leases)
        {
            if (TryParseGuid(lease.OutputId) is { } lineId)
                _leasedLineIds.Add(lineId);
        }

        _inner = new ClipCompositionRuntime(
            definition,
            leases,
            canvas => CreateCompositor(canvas, composition));
        _inner.DriftWarning += OnInnerDriftWarning;
        _inner.PumpPressureWarning += OnInnerPumpPressureWarning;
    }

    /// <summary>True when this composition holds a video lease on the given output line (outputs-panel health probe).</summary>
    public bool DrivesLine(Guid outputLineId) => _leasedLineIds.Contains(outputLineId);

    /// <summary>Live-swaps the warp mapping of the given output line (null clears). False when this
    /// composition doesn't drive the line.</summary>
    public bool UpdateOutputMapping(Guid outputLineId, CueOutputMapping? mapping) =>
        _inner.UpdateOutputMapping(outputLineId.ToString("N"), ToMappingSpec(mapping));

    /// <summary>HaPlay model → framework spec (null-preserving).</summary>
    internal static ClipOutputMappingSpec? ToMappingSpec(CueOutputMapping? mapping) =>
        mapping is null
            ? null
            : new ClipOutputMappingSpec(
                mapping.Sections
                    .Select(s => new ClipOutputMappingSection(
                        s.Id.ToString("N"), s.Enabled,
                        s.SrcX, s.SrcY, s.SrcWidth, s.SrcHeight,
                        s.DestX, s.DestY, s.DestWidth, s.DestHeight,
                        s.RotationDegrees, s.Opacity, s.Brightness,
                        s.MeshColumns, s.MeshRows,
                        s.MeshPoints?.Select(p => new ClipMeshPoint(p.X, p.Y)).ToArray()))
                    .ToArray(),
                mapping.OutputWidth,
                mapping.OutputHeight);

    public Guid CompositionId => _composition.Id;

    public VideoFormat CanvasFormat => _inner.CanvasFormat;

    public bool RequiresBgraLayerConversion => _inner.RequiresBgraLayerConversion;

    public string CompositorBackendName => _inner.CompositorBackendName;

    public int LayerCount => _inner.LayerCount;

    internal long PumpStartCount => _inner.PumpStartCount;

    public event EventHandler<CueCompositionDriftWarning>? DriftWarning;

    public event EventHandler<CueCompositionPumpPressureWarning>? PumpPressureWarning;

    public ClipCompositionRuntimeStats GetStats() => _inner.GetStats();

    public void EnsurePumpStarted() => _inner.EnsurePumpStarted();

    public void SetClockMaster(IPlaybackClock master, IPlayhead? timeline = null) =>
        _inner.SetClockMaster(master, timeline);

    public LayerSlot AddLayer(VideoFormat sourceFormat, CueVideoPlacement placement)
    {
        return new LayerSlot(_inner.AddLayer(sourceFormat, ToPlacementSpec(placement)));
    }

    private static VideoPlacementSpec ToPlacementSpec(CueVideoPlacement placement)
    {
        // The editor/model use a top-left origin (DestY = 0 is the top), but the composited output's
        // vertical axis is the opposite, so a top placement would otherwise appear at the bottom. Mirror
        // the destination rectangle's Y here (content within the rect stays upright). This is a no-op for
        // full-frame layers (DestY 0 / DestHeight 1), so existing single-layer behaviour is unchanged.
        var destY = 1.0 - placement.DestY - placement.DestHeight;

        return new VideoPlacementSpec(
            placement.CompositionId.ToString("N"),
            placement.LayerIndex,
            placement.Opacity,
            placement.Position.ToString(),
            placement.DestX,
            destY,
            placement.DestWidth,
            placement.DestHeight,
            placement.CropLeft,
            placement.CropTop,
            placement.CropRight,
            placement.CropBottom);
    }

    public void Dispose()
    {
        _inner.DriftWarning -= OnInnerDriftWarning;
        _inner.PumpPressureWarning -= OnInnerPumpPressureWarning;
        _inner.Dispose();
    }

    private void OnInnerDriftWarning(object? sender, ClipCompositionDriftWarning warning)
    {
        _ = sender;
        try
        {
            DriftWarning?.Invoke(this, new CueCompositionDriftWarning(
                CompositionId,
                warning.CompositionName,
                warning.FramesBehindMaster,
                warning.LagFromMaster));
        }
        catch (Exception ex)
        {
            Trace.LogTrace(ex, "CueCompositionRuntime: DriftWarning handler threw");
        }
    }

    private void OnInnerPumpPressureWarning(object? sender, ClipCompositionPumpPressureWarning warning)
    {
        _ = sender;
        var outputId = TryParseGuid(warning.OutputId) ?? Guid.Empty;
        try
        {
            PumpPressureWarning?.Invoke(this, new CueCompositionPumpPressureWarning(
                CompositionId,
                _composition.Name,
                outputId,
                warning.OutputName,
                warning.DroppedSinceLastReport,
                warning.DroppedTotal));
        }
        catch (Exception ex)
        {
            Trace.LogTrace(ex, "CueCompositionRuntime: PumpPressureWarning handler threw");
        }
    }

    private static IReadOnlyList<ClipCompositionOutputLease> BuildOutputLeases(
        CueComposition composition,
        IReadOnlyList<OutputLineViewModel> targetLines,
        OutputManagementViewModel outputs,
        IReadOnlyDictionary<Guid, CueOutputMapping?>? mappingsByLine)
    {
        var leases = new List<ClipCompositionOutputLease>();
        foreach (var line in targetLines)
        {
            var mapping = mappingsByLine is not null && mappingsByLine.TryGetValue(line.Definition.Id, out var m)
                ? ToMappingSpec(m)
                : null;
            try
            {
                if (line.Definition is LocalVideoOutputDefinition)
                {
                    var output = outputs.TryAcquireLocalVideoOutputForPlayback(line);
                    if (output is not null)
                    {
                        leases.Add(new ClipCompositionOutputLease(
                            line.Definition.Id.ToString("N"),
                            line.Definition.DisplayName,
                            output,
                            Release: () => outputs.ReleaseLocalVideoOutputForPlayback(line),
                            Mapping: mapping));
                    }
                }
                else if (line.Definition is NDIOutputDefinition nd
                         && nd.StreamMode != NDIOutputStreamMode.AudioOnly)
                {
                    var ndi = outputs.TryAcquireNDICarrierForPlayback(line, needsVideo: true, needsAudio: false);
                    if (ndi is not null)
                    {
                        IVideoOutput sink = ndi.Video;
                        sink = WrapWithNDILockIfNeeded(sink, nd, $"cuecomp-ndi-{nd.Id:N}");
                        var pump = new VideoOutputPump(
                            sink,
                            maxQueuedFrames: 8,
                            name: $"cuecomp-ndi-{nd.Id:N}",
                            log: null,
                            disposeInnerOnDispose: !ReferenceEquals(sink, ndi.Video));
                        leases.Add(new ClipCompositionOutputLease(
                            nd.Id.ToString("N"),
                            nd.DisplayName,
                            pump,
                            Release: () => outputs.ReleaseNDICarrierForPlayback(line, releaseVideo: true, releaseAudio: false),
                            DisposeOutputOnRuntimeDispose: true,
                            Mapping: mapping));
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.LogWarning(
                    ex,
                    "CueCompositionRuntime: failed to acquire output {Line} for composition {Composition}",
                    line.Definition.DisplayName,
                    composition.Name);
            }
        }

        return leases;
    }

    private static ClipCompositionCompositor CreateCompositor(VideoFormat canvasFormat, CueComposition composition)
    {
        var requested = Environment.GetEnvironmentVariable("HAPLAY_CUE_COMPOSITOR");
        if (string.Equals(requested, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            Trace.LogInformation("CueCompositionRuntime: composition {Composition} using CPU compositor (env override)",
                composition.Name);
            return new ClipCompositionCompositor(
                new CpuVideoCompositor(canvasFormat),
                RequiresBgraLayerConversion: true,
                BackendName: "CPU");
        }

        if (SDL3GLVideoCompositor.TryProbe(out var glError))
        {
            var gpu = new SDL3GLVideoCompositor(canvasFormat);
            Trace.LogInformation("CueCompositionRuntime: composition {Composition} using OpenGL compositor", composition.Name);
            return new ClipCompositionCompositor(
                gpu,
                RequiresBgraLayerConversion: false,
                BackendName: "OpenGL",
                DisposeOnDriverThread: gpu.DisposeOnOwnerThread);
        }

        var explicitGpu =
            string.Equals(requested, "gl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "gpu", StringComparison.OrdinalIgnoreCase);
        if (explicitGpu)
        {
            Trace.LogWarning(
                "CueCompositionRuntime: OpenGL compositor requested for {Composition} but unavailable: {Error}; falling back to CPU",
                composition.Name,
                glError);
        }
        else
        {
            Trace.LogInformation(
                "CueCompositionRuntime: OpenGL compositor unavailable for {Composition}: {Error}; using CPU compositor",
                composition.Name,
                glError);
        }

        return new ClipCompositionCompositor(
            new CpuVideoCompositor(canvasFormat),
            RequiresBgraLayerConversion: true,
            BackendName: "CPU");
    }

    /// <summary>Mirrors <c>HaPlayPlaybackSession.WrapWithNDILockIfNeeded</c>.</summary>
    private static IVideoOutput WrapWithNDILockIfNeeded(IVideoOutput ndiSender, NDIOutputDefinition nd, string name)
    {
        if (nd.PixelFormatLock is null && nd.ResolutionLockWidth is null && nd.ResolutionLockHeight is null)
            return ndiSender;
        return new LockedFormatVideoOutput(
            ndiSender,
            nd.PixelFormatLock,
            nd.ResolutionLockWidth,
            nd.ResolutionLockHeight,
            name,
            disposeInnerOnDispose: false);
    }

    private static Guid? TryParseGuid(string id) =>
        Guid.TryParseExact(id, "N", out var guid) || Guid.TryParse(id, out guid)
            ? guid
            : null;

    internal sealed class LayerSlot : IDisposable
    {
        private readonly ClipCompositionRuntime.LayerSlot _inner;

        public LayerSlot(ClipCompositionRuntime.LayerSlot inner)
        {
            _inner = inner;
        }

        public IVideoOutput Output => _inner.Output;

        public int LayerIndex => _inner.LayerIndex;

        public float Opacity
        {
            get => _inner.Opacity;
            set => _inner.Opacity = value;
        }

        public long Sequence => _inner.Sequence;

        public void UpdatePlacement(CueVideoPlacement placement) =>
            _inner.UpdatePlacement(ToPlacementSpec(placement));

        public void Dispose() => _inner.Dispose();
    }
}

/// <summary>Per-composition drift sample. Emitted at most every ~5 s on sustained drift.</summary>
internal readonly record struct CueCompositionDriftWarning(
    Guid CompositionId,
    string CompositionName,
    long FramesBehindMaster,
    TimeSpan LagFromMaster);

/// <summary>NDI output pump dropped frames — receiver/network can't keep up.</summary>
internal readonly record struct CueCompositionPumpPressureWarning(
    Guid CompositionId,
    string CompositionName,
    Guid OutputLineId,
    string OutputLineName,
    long DroppedSinceLastReport,
    long DroppedTotal);
