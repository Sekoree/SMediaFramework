using HaPlay.Models;
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

    public CueCompositionRuntime(
        CueComposition composition,
        IReadOnlyList<OutputLineViewModel> targetLines,
        OutputManagementViewModel outputs)
    {
        _composition = composition;
        var definition = new ClipCompositionDefinition(
            composition.Id.ToString("N"),
            composition.Name,
            composition.Width,
            composition.Height,
            composition.FrameRateNum,
            composition.FrameRateDen);

        _inner = new ClipCompositionRuntime(
            definition,
            BuildOutputLeases(composition, targetLines, outputs),
            canvas => CreateCompositor(canvas, composition));
        _inner.DriftWarning += OnInnerDriftWarning;
        _inner.PumpPressureWarning += OnInnerPumpPressureWarning;
    }

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

    public void SetClockMaster(IPlaybackClock master) => _inner.SetClockMaster(master);

    public LayerSlot AddLayer(VideoFormat sourceFormat, CueVideoPlacement placement)
    {
        var spec = new VideoPlacementSpec(
            placement.CompositionId.ToString("N"),
            placement.LayerIndex,
            placement.Opacity,
            placement.Position.ToString());
        return new LayerSlot(_inner.AddLayer(sourceFormat, spec));
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
        OutputManagementViewModel outputs)
    {
        var leases = new List<ClipCompositionOutputLease>();
        foreach (var line in targetLines)
        {
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
                            Release: () => outputs.ReleaseLocalVideoOutputForPlayback(line)));
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
                            DisposeOutputOnRuntimeDispose: true));
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

        public long Sequence => _inner.Sequence;

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
