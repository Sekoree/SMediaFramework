using S.Media.Players;
using S.Media.Routing;
using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Time;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Compositor;

namespace S.Media.Session;

public sealed record ClipCompositionDefinition(
    string Id,
    string Name,
    int Width,
    int Height,
    int FrameRateNum,
    int FrameRateDen);

public sealed record ClipCompositionOutputLease(
    string OutputId,
    string DisplayName,
    IVideoOutput Output,
    Action? Release = null,
    bool DisposeOutputOnRuntimeDispose = false,
    ClipOutputMappingSpec? Mapping = null);

/// <summary>A host-provided audio output for a clip route's device - the audio analogue of
/// <see cref="ClipCompositionOutputLease"/>. Lets the host route a clip's audio to a sink the session's
/// <c>IAudioBackend</c> can't create, e.g. an NDI sender's audio side that must share the SAME carrier as the
/// composition's video. A BORROWED output declares <see cref="DisposeOutputOnRuntimeDispose"/> = false so the
/// session never disposes it (the host owns the carrier's lifetime); <see cref="Release"/> runs on teardown.</summary>
public sealed record ClipAudioOutputLease(
    IAudioOutput Output,
    bool DisposeOutputOnRuntimeDispose = false,
    Action? Release = null);

public sealed record ClipCompositionCompositor(
    IVideoCompositor Compositor,
    bool RequiresBgraLayerConversion,
    string BackendName,
    Action? DisposeOnDriverThread = null);

/// <summary>
/// Shared cue composition runtime: owns the compositor source, layer slots, output fan-out pump,
/// and optional clock-mastered presentation cadence for one composition canvas.
/// </summary>
public sealed class ClipCompositionRuntime : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Playback.ClipCompositionRuntime");

    private readonly ClipCompositionDefinition _definition;
    private readonly VideoFormat _canvasFormat;
    private readonly IVideoCompositor _compositor;
    private readonly Action? _disposeCompositorOnDriverThread;
    private readonly VideoCompositorSource _mixer;
    private readonly object _gate = new();
    private readonly List<AcquiredOutput> _acquired = [];
    // Lock-free, allocation-free read of the acquired outputs for the per-frame pump (NXT-11): republished under
    // _gate whenever _acquired changes, so PumpOneFrame reads a stable immutable view without a per-frame ToList.
    private volatile IReadOnlyList<AcquiredOutput> _acquiredSnapshot = [];
    private readonly List<LayerSlot> _slots = [];
    private readonly List<SurfaceLayerSlot> _surfaceLayers = [];
    private readonly TimeSpan _canvasPeriod;
    private long _nextLayerSequence;
    private long _framesComposited;
    private readonly TimingAccumulator _pumpTiming = new();
    private readonly TimingAccumulator _compositeTiming = new();
    private long _framesSubmitted;
    private long _pumpOverruns;
    private long _lastPumpFrameTicks;
    private long _maxPumpFrameTicks;
    private long _framesBehindMaster;
    private long _lastBehindMasterReport;
    private long _pumpStartCount;
    private long _lastDriftCheckTicks;
    private TimeSpan _lastMasterPosition;
    private IPlaybackClock? _master;
    private IPlayhead? _timeline;
    private ITransportTimeline? _transportTimeline;
    private MediaClock? _slaveClock;
    private int _driverDisposeState;
    private bool _disposed;

    private readonly Func<VideoFormat, ClipCompositionCompositor> _compositorFactory;

    /// <summary>Mapping stages whose compositor must be torn down on the pump (driver) thread -
    /// retired by live mapping updates or runtime dispose; drained at the next tick.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<OutputMappingStage> _retiredMappingStages = new();

    /// <summary>True when the single mapped output's warp runs inside the canvas compositor
    /// (<see cref="IWarpPassVideoCompositor"/>) - the mixer frame is already warped and the
    /// chained per-lease stage is skipped. Saves a full readback + re-upload per frame.</summary>
    private volatile bool _integratedWarpActive;

    /// <summary>Optional composition-level video FX: a canvas-sized mapping applied after all layers
    /// are composited and before per-output mappings/fan-out.</summary>
    private volatile OutputMappingStage? _compositionMappingStage;

    /// <summary>Clip-owned subtitle layers. Each feed has its own clip-position provider, allowing several
    /// subtitle tracks and clips on one composition without sharing a global subtitle timeline.</summary>
    private readonly List<SubtitleLayerFeed> _subtitleFeeds = [];
    // Same lock-free snapshot pattern as _acquiredSnapshot - the per-frame DriveSubtitleLayers reads this
    // instead of snapshotting _subtitleFeeds.ToArray() every tick (NXT-11).
    private volatile IReadOnlyList<SubtitleLayerFeed> _subtitleFeedsSnapshot = [];

    public ClipCompositionRuntime(
        ClipCompositionDefinition definition,
        IReadOnlyList<ClipCompositionOutputLease> outputs,
        Func<VideoFormat, ClipCompositionCompositor>? compositorFactory = null,
        ClipOutputMappingSpec? compositionMapping = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ArgumentNullException.ThrowIfNull(outputs);

        var den = Math.Max(1, definition.FrameRateDen);
        var num = Math.Max(1, definition.FrameRateNum);
        var rate = new Rational(num, den);
        _canvasFormat = new VideoFormat(
            Math.Max(16, definition.Width),
            Math.Max(16, definition.Height),
            PixelFormat.Bgra32,
            rate);
        _canvasPeriod = TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond * (long)den / Math.Max(1L, (long)num)));

        _compositorFactory = compositorFactory ?? CreateDefaultCompositor;
        var compositor = _compositorFactory(_canvasFormat);
        _compositor = compositor.Compositor ?? throw new InvalidOperationException("Compositor factory returned null compositor.");
        RequiresBgraLayerConversion = compositor.RequiresBgraLayerConversion;
        CompositorBackendName = string.IsNullOrWhiteSpace(compositor.BackendName) ? "Unknown" : compositor.BackendName;
        _disposeCompositorOnDriverThread = compositor.DisposeOnDriverThread;
        _mixer = new VideoCompositorSource(_canvasFormat, _compositor, disposeCompositorOnDispose: false);

        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} initialized ({Width}x{Height} {Rate}, compositor={Backend})",
            CompositionName, _canvasFormat.Width, _canvasFormat.Height, _canvasFormat.FrameRate, CompositorBackendName);

        foreach (var output in outputs)
        {
            if (output.Output is null)
                continue;
            var acquired = new AcquiredOutput(output);
            if (output.Mapping is not null)
                acquired.SetMapping(output.Mapping, _canvasFormat, out _);
            _acquired.Add(acquired);
            acquired.SubscribePumpPressure(this);
        }
        RepublishAcquiredSnapshot();

        SetCompositionMappingCore(compositionMapping, out _);
        ReevaluateIntegratedWarp();
    }

    // Rebuilds the lock-free pump snapshots after a mutation (NXT-11). Callers either hold _gate or run during
    // single-threaded construction; the volatile publish makes the new view visible to the pump thread.
    private void RepublishAcquiredSnapshot() => _acquiredSnapshot = _acquired.ToArray();
    private void RepublishSubtitleFeedsSnapshot() => _subtitleFeedsSnapshot = _subtitleFeeds.ToArray();

    /// <summary>
    /// Routes the warp through the canvas compositor itself when possible: with exactly one mapped
    /// output and a warp-capable compositor, the canvas pass renders straight into the warped output
    /// (one GPU pass, one readback). Multi-output mappings use <see cref="TryPumpIntegratedMultiWarp"/>.
    /// </summary>
    private void ReevaluateIntegratedWarp()
    {
        if (_compositor is not IWarpPassVideoCompositor warp)
            return;

        if (_compositionMappingStage is not null)
        {
            if (_integratedWarpActive)
                warp.SetWarpPass(_canvasFormat, null);
            _integratedWarpActive = false;
            return;
        }

        OutputMappingStage? single;
        lock (_gate)
            single = _acquired.Count == 1 ? _acquired[0].MappingStage : null;

        if (single is not null)
        {
            warp.SetWarpPass(single.OutputFormat, single.BuildWarpSections());
            _integratedWarpActive = true;
        }
        else if (_integratedWarpActive)
        {
            warp.SetWarpPass(_canvasFormat, null);
            _integratedWarpActive = false;
        }
    }

    public string CompositionId => _definition.Id;

    public string CompositionName => _definition.Name;

    public VideoFormat CanvasFormat => _canvasFormat;

    public bool RequiresBgraLayerConversion { get; }

    public string CompositorBackendName { get; }

    public int LayerCount
    {
        get { lock (_gate) return _slots.Count; }
    }

    public int OutputCount
    {
        get { lock (_gate) return _acquired.Count; }
    }

    public long PumpStartCount => Volatile.Read(ref _pumpStartCount);

    public event EventHandler<ClipCompositionDriftWarning>? DriftWarning;

    public event EventHandler<ClipCompositionPumpPressureWarning>? PumpPressureWarning;

    public ClipCompositionRuntimeStats GetStats()
    {
        long slotOverflow = 0;
        int layerCount;
        lock (_gate)
        {
            layerCount = _slots.Count;
            foreach (var slot in _slots)
                slotOverflow += slot.RawSlot.OverflowFrames;
        }

        return new ClipCompositionRuntimeStats(
            CompositionId,
            Volatile.Read(ref _framesComposited),
            Volatile.Read(ref _framesSubmitted),
            Volatile.Read(ref _pumpOverruns),
            slotOverflow,
            TimeSpan.FromTicks(Volatile.Read(ref _lastPumpFrameTicks)),
            TimeSpan.FromTicks(Volatile.Read(ref _maxPumpFrameTicks)),
            Volatile.Read(ref _framesBehindMaster),
            _master is not null,
            layerCount,
            _pumpTiming.Snapshot(),
            _compositeTiming.Snapshot(),
            _canvasPeriod);
    }

    public void EnsurePumpStarted()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_slaveClock is not null) return;
            StartPumpLocked();
        }
    }

    /// <summary>
    /// Live-swaps the output mapping of <paramref name="outputId"/> (null clears it - the output
    /// goes back to receiving the raw canvas). Safe while the pump runs; the editor calls this on
    /// every change. Returns false when the output isn't part of this runtime.
    /// </summary>
    public bool UpdateOutputMapping(string outputId, ClipOutputMappingSpec? mapping)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        AcquiredOutput? target;
        lock (_gate)
        {
            if (_disposed) return false;
            target = _acquired.FirstOrDefault(a => string.Equals(a.OutputId, outputId, StringComparison.Ordinal));
        }

        if (target is null)
            return false;

        if (!target.SetMapping(mapping, _canvasFormat, out var retired))
            return false;
        if (retired is not null)
            _retiredMappingStages.Enqueue(retired);
        ReevaluateIntegratedWarp();
        return true;
    }

    /// <summary>
    /// Removes one fan-out output from a running composition. Any in-flight submit for the output is allowed
    /// to finish before the lease is released; future pump snapshots that still hold the retired output drop
    /// their frame instead of touching a runtime the host may be about to dispose.
    /// </summary>
    /// <summary>
    /// Attach an additional live output (e.g. a UI preview surface) to this running composition; the pump picks it up
    /// on its next tick. Symmetric to <see cref="RemoveOutput"/>. The caller owns the output's lifetime unless the
    /// lease sets <see cref="ClipCompositionOutputLease.DisposeOutputOnRuntimeDispose"/>.
    /// </summary>
    public bool AddOutput(ClipCompositionOutputLease output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Output is null)
            return false;

        var acquired = new AcquiredOutput(output);
        if (output.Mapping is not null)
            acquired.SetMapping(output.Mapping, _canvasFormat, out _);

        lock (_gate)
        {
            // Disposed mid-attach: drop it. (A mapping stage, if any, is GC-reclaimed - preview attaches carry none.)
            if (_disposed)
                return false;
            _acquired.Add(acquired);
            RepublishAcquiredSnapshot();
        }

        acquired.SubscribePumpPressure(this);
        ReevaluateIntegratedWarp();
        return true;
    }

    public bool RemoveOutput(string outputId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputId);

        AcquiredOutput? removed = null;
        lock (_gate)
        {
            if (_disposed) return false;

            for (var i = 0; i < _acquired.Count; i++)
            {
                if (!string.Equals(_acquired[i].OutputId, outputId, StringComparison.Ordinal))
                    continue;

                removed = _acquired[i];
                _acquired.RemoveAt(i);
                RepublishAcquiredSnapshot();
                break;
            }
        }

        if (removed is null)
            return false;

        if (removed.Retire("ClipCompositionRuntime.RemoveOutput") is { } retired)
            _retiredMappingStages.Enqueue(retired);
        ReevaluateIntegratedWarp();
        return true;
    }

    /// <summary>
    /// Live-swaps the composition-level video FX mapping. Null clears it. The stage output is always
    /// the composition canvas size, regardless of any editor-side output-size fields.
    /// </summary>
    public bool UpdateCompositionMapping(ClipOutputMappingSpec? mapping)
    {
        OutputMappingStage? retired;
        lock (_gate)
        {
            if (_disposed) return false;
            SetCompositionMappingCore(mapping, out retired);
        }

        if (retired is not null)
            _retiredMappingStages.Enqueue(retired);
        ReevaluateIntegratedWarp();
        return true;
    }

    private void SetCompositionMappingCore(ClipOutputMappingSpec? mapping, out OutputMappingStage? retired)
    {
        retired = null;
        var current = _compositionMappingStage;
        if (mapping is null)
        {
            _compositionMappingStage = null;
            retired = current;
            return;
        }

        var canvasMapping = ForceCanvasSizedMapping(mapping);
        var sections = OutputMappingResolver.Resolve(canvasMapping, _canvasFormat.Width, _canvasFormat.Height);
        if (current is not null && current.OutputFormat == _canvasFormat)
        {
            _compositionMappingStage = current.WithSections(sections);
            return;
        }

        _compositionMappingStage = new OutputMappingStage(_canvasFormat, sections);
        retired = current;
    }

    private ClipOutputMappingSpec ForceCanvasSizedMapping(ClipOutputMappingSpec mapping) =>
        mapping with { OutputWidth = _canvasFormat.Width, OutputHeight = _canvasFormat.Height };

    private void DrainRetiredMappingStages()
    {
        while (_retiredMappingStages.TryDequeue(out var stage))
            stage.DisposeCompositor();
    }

    public void SetClockMaster(IPlaybackClock master, IPlayhead? timeline = null)
    {
        ArgumentNullException.ThrowIfNull(master);
        MediaClock? clockToRetarget = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_master is not null)
            {
                // Preserve the first-master contract. In particular, a legacy caller must not detach an
                // already-installed TransportTimeline and leave source selection reading master coordinates.
                if (_transportTimeline is null && timeline is not null)
                    _timeline = timeline;
                return;
            }
            _master = master;
            _timeline = timeline;
            _transportTimeline = null;
            foreach (var layer in _slots)
                layer.RawSlot.KeepPolicy = SlotKeepPolicy.MasterAligned;
            clockToRetarget = _slaveClock;
        }

        clockToRetarget?.SetMaster(master);
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} pump now slaved to master clock",
            CompositionName);
    }

    /// <summary>
    /// Masters this composition to the transport group's authoritative timeline. The master coordinate drives
    /// pump cadence/output scheduling while <see cref="TransportTimelineSnapshot.SourceTime"/> selects decoded
    /// frames. The same contract is also passed to subtitle feeds, keeping seek/trim/live correlation on one
    /// generation instead of combining a raw player playhead with an unrelated session clock (NXT-04).
    /// </summary>
    public void SetTransportTimeline(ITransportTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        MediaClock? clockToRetarget = null;
        lock (_gate)
        {
            if (_disposed) return;
            // A composition is one clock domain. The first transport group to drive it owns that domain until
            // the composition is rebuilt; a later concurrent group must not retarget every existing layer.
            // Repeated calls from successive clips in the SAME group carry the same stable timeline object.
            if (_master is not null)
                return;
            _transportTimeline = timeline;
            _timeline = null;
            _master = timeline;
            clockToRetarget = _slaveClock;
            foreach (var layer in _slots)
                layer.RawSlot.KeepPolicy = SlotKeepPolicy.MasterAligned;
        }

        clockToRetarget?.SetMaster(timeline);
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} now follows the transport timeline",
            CompositionName);
    }

    /// <summary>
    /// Releases the current clock master so the NEXT clip can re-master this composition. The pump keeps
    /// running, free-running on its own clock in the meantime (surface layers - e.g. a persistent
    /// visualizer - keep rendering). This is the escape hatch for the "one clock domain until rebuilt"
    /// contract: a PRESERVED composition (kept alive across a document reload) calls this once the outgoing
    /// clip's group is torn down, so the incoming clip's <see cref="SetTransportTimeline"/> takes effect
    /// instead of being ignored. Normal (non-preserved) compositions never call this - they are rebuilt.
    /// </summary>
    public void ResetClockMaster()
    {
        MediaClock? clockToClear;
        lock (_gate)
        {
            if (_disposed) return;
            _master = null;
            _transportTimeline = null;
            _timeline = null;
            clockToClear = _slaveClock;
        }

        clockToClear?.SetMaster(null); // free-run until the next clip masters it
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} clock master released (preserved across reload)",
            CompositionName);
    }

    public LayerSlot AddLayer(
        VideoFormat sourceFormat, VideoPlacementSpec placement,
        SlotKeepPolicy keepPolicy = SlotKeepPolicy.MasterAligned)
    {
        LayerSlot layer;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rawSlot = _mixer.AddSlot();
            // Master-clock compositions align decoded frames to the clock by PTS; without a master they
            // are latest-wins. Callers whose frames carry no meaningful PTS (subtitle overlays are
            // pump-driven and re-rendered in place at PresentationTime 0) must opt into Latest explicitly -
            // MasterAligned would freeze on the first frame, since every frame is equidistant from the clock.
            if (_master is not null)
                rawSlot.KeepPolicy = keepPolicy;
            layer = new LayerSlot(this, rawSlot, sourceFormat, placement, Interlocked.Increment(ref _nextLayerSequence));
            try
            {
                layer.ApplyPlacement();
            }
            catch
            {
                _mixer.RemoveSlot(rawSlot.Id);
                throw;
            }
            _slots.Add(layer);
            SortLayersLocked();
        }
        EnsurePumpStarted();
        return layer;
    }

    private void RemoveLayer(LayerSlot layer)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _slots.Remove(layer);
            _mixer.RemoveSlot(layer.RawSlot.Id);
            if (_slots.Count > 0)
                SortLayersLocked();
        }
    }

    /// <summary>Whether this composition's compositor can host GPU layer surfaces (NXT-10). When false,
    /// surface-capable sources must be consumed through their normal CPU frame path.</summary>
    public bool SupportsSurfaceLayers => _mixer.SupportsSurfaceLayers;

    /// <summary>
    /// Adds a GPU layer surface (NXT-10): <paramref name="surface"/> renders directly into the canvas on
    /// the compositor's GL thread, ON TOP of every frame layer (surfaces don't z-interleave with frame
    /// layers - v1 contract; they order among themselves by <see cref="VideoPlacementSpec.LayerIndex"/>).
    /// The placement's destination rect/fit/opacity resolve exactly like a frame layer's (the surface's
    /// nominal source size is the canvas). Integrated multi-output warp is bypassed while any surface is
    /// present (the chained per-lease mapping path still applies). Disposing the returned slot removes
    /// the layer AND disposes the surface (the runtime owns it - mirrors <see cref="LayerSlot"/> handing
    /// its slot back). Throws when <see cref="SupportsSurfaceLayers"/> is false.
    /// </summary>
    /// <param name="ownsSurface">When true (default) the returned slot disposes <paramref name="surface"/>
    /// on removal. Pass false to add the SAME surface into an ADDITIONAL placement (one visualizer render
    /// shown in several sections of the canvas): the compositor keys ConfigureGl by surface instance and
    /// renders it once per layer, so a single surface must be owned by exactly one slot to avoid a
    /// double dispose. See <c>ShowSessionVisualizerService</c> (#26 multi-placement).</param>
    public SurfaceLayerSlot AddSurfaceLayer(
        IVideoCompositorLayerSurface surface, VideoPlacementSpec placement, bool ownsSurface = true)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(placement);
        SurfaceLayerSlot layer;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rawSlot = _mixer.AddSurfaceSlot(surface);
            layer = new SurfaceLayerSlot(this, rawSlot, placement, ownsSurface);
            try
            {
                layer.ApplyPlacement();
            }
            catch
            {
                _mixer.RemoveSurfaceSlot(rawSlot);
                throw;
            }
            _surfaceLayers.Add(layer);
            SortSurfaceLayersLocked();
        }
        EnsurePumpStarted();
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} gained a GPU surface layer (z={LayerIndex})",
            CompositionName, placement.LayerIndex);
        return layer;
    }

    private void RemoveSurfaceLayer(SurfaceLayerSlot layer)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _surfaceLayers.Remove(layer);
            _mixer.RemoveSurfaceSlot(layer.RawSlot);
        }
    }

    private void SortSurfaceLayersLocked()
    {
        _surfaceLayers.Sort(static (a, b) =>
        {
            var byIndex = a.LayerIndex.CompareTo(b.LayerIndex);
            return byIndex != 0 ? byIndex : a.Sequence.CompareTo(b.Sequence);
        });
        var order = _surfaceLayers.Select(l => l.RawSlot).ToList();
        _mixer.SortSurfaceSlots((a, b) => order.IndexOf(a).CompareTo(order.IndexOf(b)));
    }

    /// <summary>
    /// Attaches a subtitle/overlay source as a full-canvas, top-z-order layer. Each frame the runtime renders the
    /// source at the owning clip's position, copies its (borrowed) overlay into a pooled, slot-owned frame, and pushes it
    /// like any other layer - so the mixer composites it uniformly (z-order, opacity, blend). The source should
    /// render at the canvas size. The returned lease removes the layer and disposes the source; dispose it when
    /// the owning clip stops.
    /// </summary>
    public IDisposable AttachSubtitleOverlay(
        IVideoOverlaySource source,
        Func<TimeSpan> positionProvider,
        int layerIndex = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(positionProvider);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var placement = new VideoPlacementSpec(CompositionName, layerIndex, Placement: "stretch");
        // Latest-wins: the subtitle feed is pump-driven - it renders the source at the current position each
        // tick and submits that frame. Its frames carry no per-frame PTS (re-rendered in place at PTS 0), so a
        // MasterAligned slot would freeze on the first frame (every frame equidistant from the clock). Latest
        // takes the newest submitted frame, which is exactly the one rendered for this position.
        var layer = AddLayer(_canvasFormat, placement, SlotKeepPolicy.Latest);
        var feed = new SubtitleLayerFeed(this, source, layer, positionProvider);
        lock (_gate)
        {
            _subtitleFeeds.Add(feed);
            RepublishSubtitleFeedsSnapshot();
        }
        return feed;
    }

    /// <summary>
    /// Attaches a subtitle source to the same authoritative transport timeline as video selection. Subtitle
    /// events use source time (so a trimmed file still selects events at its original media timestamps), while
    /// cue-local effects remain available from the contract's <see cref="TransportTimelineSnapshot.CueTime"/>.
    /// </summary>
    public IDisposable AttachSubtitleOverlay(
        IVideoOverlaySource source,
        ITransportTimeline timeline,
        int layerIndex = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return AttachSubtitleOverlay(source, () => timeline.GetSnapshot().SourceTime, layerIndex);
    }

    private void RemoveSubtitleFeed(SubtitleLayerFeed feed)
    {
        lock (_gate)
        {
            _subtitleFeeds.Remove(feed);
            RepublishSubtitleFeedsSnapshot();
        }
        RemoveLayer(feed.Layer);
    }

    /// <summary>Renders every subtitle source at its owning clip position. Pump-thread only.</summary>
    private void DriveSubtitleLayers()
    {
        var feeds = _subtitleFeedsSnapshot; // lock-free, allocation-free per-frame read (NXT-11)

        foreach (var feed in feeds)
            DriveSubtitleLayer(feed);
    }

    private void DriveSubtitleLayer(SubtitleLayerFeed feed)
    {
        VideoFrame? overlay;
        try
        {
            overlay = feed.RenderAtCurrentPosition();
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "ClipCompositionRuntime: subtitle render failed for {Composition}", CompositionName);
            feed.Layer.Opacity = 0f;
            return;
        }

        if (overlay is null)
        {
            feed.Layer.Opacity = 0f;
            return;
        }

        try
        {
            // The overlay frame is borrowed (the source reuses it) and slots take ownership of pushed frames, so
            // copy into a pooled, slot-owned frame. Pooled => no GC; the slot returns it to the pool on its swap.
            var owned = CopyToPooledBgra(overlay);
            try
            {
                feed.Layer.Output.Submit(owned);
            }
            catch
            {
                owned.Dispose();
                throw;
            }
            feed.Layer.Opacity = 1f;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "ClipCompositionRuntime: subtitle push failed for {Composition}", CompositionName);
            feed.Layer.Opacity = 0f;
        }
    }

    private static VideoFrame CopyToPooledBgra(VideoFrame source)
    {
        if (source.Format.PixelFormat != PixelFormat.Bgra32 || source.Planes.Length != 1 || source.Strides.Length != 1)
            throw new NotSupportedException(
                $"Subtitle overlays must be single-plane BGRA32, not {source.Format.PixelFormat} with {source.Planes.Length} planes.");
        var plane = source.Planes[0];
        var length = plane.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        plane.Span.CopyTo(buffer);
        var owned = buffer;
        return new VideoFrame(
            source.PresentationTime,
            source.Format,
            new ReadOnlyMemory<byte>(buffer, 0, length),
            source.Strides[0],
            metadata: new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied),
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)));
    }

    private sealed class SubtitleLayerFeed : IDisposable
    {
        private readonly ClipCompositionRuntime _owner;
        private readonly IVideoOverlaySource _source;
        private readonly Func<TimeSpan> _positionProvider;
        private readonly object _gate = new();
        private bool _disposed;

        public SubtitleLayerFeed(
            ClipCompositionRuntime owner,
            IVideoOverlaySource source,
            LayerSlot layer,
            Func<TimeSpan> positionProvider)
        {
            _owner = owner;
            _source = source;
            Layer = layer;
            _positionProvider = positionProvider;
        }

        public LayerSlot Layer { get; }

        public VideoFrame? RenderAtCurrentPosition()
        {
            lock (_gate)
                return _disposed ? null : _source.RenderAt(_positionProvider());
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            _owner.RemoveSubtitleFeed(this);
            _source.Dispose();
        }
    }

    private static ClipCompositionCompositor CreateDefaultCompositor(VideoFormat canvasFormat) =>
        new(new CpuVideoCompositor(canvasFormat), RequiresBgraLayerConversion: true, BackendName: "CPU");

    private void StartPumpLocked()
    {
        if (_slaveClock is not null) return;

        var audioInterval = TimeSpan.FromMilliseconds(50);
        _slaveClock = new MediaClock(audioInterval, _canvasPeriod);
        if (_master is not null)
            _slaveClock.SetMaster(_master);
        _slaveClock.VideoTick += OnSlaveVideoTick;
        _slaveClock.Start();
        Interlocked.Increment(ref _pumpStartCount);
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} pump started (videoTick={PeriodMs:0.00}ms, mastered={Mastered})",
            CompositionName,
            _canvasPeriod.TotalMilliseconds,
            _master is not null);
    }

    private void OnSlaveVideoTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (Interlocked.CompareExchange(ref _driverDisposeState, 2, 1) == 1)
        {
            DrainRetiredMappingStages();
            try { _disposeCompositorOnDriverThread?.Invoke(); }
            catch (Exception ex) { Trace.LogWarning(ex, "ClipCompositionRuntime.OnSlaveVideoTick: driver compositor dispose"); }
            return;
        }
        if (_disposed) return;
        DrainRetiredMappingStages();
        PumpOneFrame();
        CheckMasterDrift();
    }

    private void CheckMasterDrift()
    {
        var master = _master;
        if (master is null) return;

        TimeSpan masterPos;
        try { masterPos = master.ElapsedSinceStart; }
        catch { return; }

        if (_lastMasterPosition == default)
        {
            _lastMasterPosition = masterPos;
            _lastDriftCheckTicks = Stopwatch.GetTimestamp();
            return;
        }

        var wallElapsed = Stopwatch.GetElapsedTime(_lastDriftCheckTicks);
        var masterElapsed = masterPos - _lastMasterPosition;
        if (masterElapsed < TimeSpan.FromMilliseconds(50)) return;

        var diff = wallElapsed - masterElapsed;
        if (Math.Abs(diff.Ticks) > _canvasPeriod.Ticks * 2)
            Interlocked.Increment(ref _framesBehindMaster);

        _lastMasterPosition = masterPos;
        _lastDriftCheckTicks = Stopwatch.GetTimestamp();

        var behind = Volatile.Read(ref _framesBehindMaster);
        var since = behind - Volatile.Read(ref _lastBehindMasterReport);
        if (since < 30)
            return;

        Volatile.Write(ref _lastBehindMasterReport, behind);
        try
        {
            DriftWarning?.Invoke(this, new ClipCompositionDriftWarning(
                CompositionId,
                CompositionName,
                behind,
                wallElapsed - masterElapsed));
        }
        catch (Exception ex)
        {
            Trace.LogTrace(ex, "ClipCompositionRuntime.CheckMasterDrift: DriftWarning handler threw");
        }
    }

    private void PumpOneFrame()
    {
        var sw = Stopwatch.StartNew();
        TimeSpan? masterPts = null;
        if (_transportTimeline is { } transportTimeline)
        {
            try { masterPts = transportTimeline.GetSnapshot().SourceTime; }
            catch (Exception ex) { Trace.LogTrace(ex, "ClipCompositionRuntime.PumpOneFrame: transport timeline read"); }
        }
        else if (_timeline is not null)
        {
            try { masterPts = _timeline.CurrentPosition; }
            catch (Exception ex) { Trace.LogTrace(ex, "ClipCompositionRuntime.PumpOneFrame: timeline read"); }
        }
        else if (_master is not null)
        {
            try { masterPts = _master.ElapsedSinceStart; }
            catch (Exception ex) { Trace.LogTrace(ex, "ClipCompositionRuntime.PumpOneFrame: master read"); }
        }

        // Subtitle layers render at their owning clips' positions before either pump path
        // reads the mixer, so it composites uniformly with the video layers (z-order/opacity/blend).
        DriveSubtitleLayers();

        var snapshot = _acquiredSnapshot; // lock-free, allocation-free per-frame read (NXT-11)

        if (snapshot.Count == 0)
            return;

        if (TryPumpIntegratedMultiWarp(masterPts, snapshot, sw))
            return;

        var compositeStarted = Stopwatch.GetTimestamp();
        if (!_mixer.TryReadNextFrame(masterPts, out var frame))
            return;
        _compositeTiming.RecordSince(compositeStarted);
        Interlocked.Increment(ref _framesComposited);

        var compositionStage = _compositionMappingStage;
        if (compositionStage is not null)
        {
            try
            {
                var fxFrame = compositionStage.Composite(frame, _compositorFactory);
                frame.Dispose();
                frame = fxFrame;
            }
            catch (Exception ex)
            {
                Trace.LogWarning(
                    ex,
                    "ClipCompositionRuntime.Pump: composition mapping stage failed for {Composition}",
                    CompositionName);
            }
        }

        // Output-mapping stages run first, while the canvas frame is alive: each mapped output
        // composites its warp sections from the canvas (compositors never take frame ownership)
        // and gets its own output-sized frame. Unmapped outputs share the canvas via the fan-out
        // below. With the integrated GPU warp active, the mixer frame IS the warped output already
        // - the chained stage is skipped. See Doc/HaPlay-Output-Mapping-Plan.md.
        var integratedWarp = _integratedWarpActive;
        List<AcquiredOutput>? unmapped = null;
        foreach (var output in snapshot)
        {
            var stage = integratedWarp ? null : output.MappingStage;
            if (stage is null)
            {
                (unmapped ??= new List<AcquiredOutput>(snapshot.Count)).Add(output);
                continue;
            }

            VideoFrame mappedFrame;
            try
            {
                mappedFrame = stage.Composite(frame, _compositorFactory);
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "ClipCompositionRuntime.Pump: mapping stage failed for {Line}", output.DisplayName);
                continue;
            }

            SubmitToOutput(output, mappedFrame);
        }

        if (unmapped is null)
        {
            frame.Dispose();
            sw.Stop();
            RecordPumpTiming(sw.Elapsed, _canvasPeriod);
            return;
        }

        // Multi-output fan-out is zero-copy when the canvas is CPU-backed (the CpuVideoCompositor's
        // pool-rented buffer - the common case): every output gets a refcounted view over the same
        // pixels and the canvas returns to the pool once the last view is disposed. The per-output
        // deep copies this replaces were ~8 MB × (outputs−1) of memcpy per 1080p frame. Fallback to
        // cloning covers non-CPU backings (GL compositor) - TryCreateCpuFanOutViews leaves the frame
        // untouched when it declines.
        VideoFrame[]? views = null;
        if (unmapped.Count > 1)
            VideoFrame.TryCreateCpuFanOutViews(frame, unmapped.Count, frame.ColorTransferHint, out views);

        for (var i = 0; i < unmapped.Count; i++)
        {
            var output = unmapped[i];
            var isLast = i == unmapped.Count - 1;
            VideoFrame toSubmit;
            if (views is not null)
            {
                // The canvas frame's release moved into the views' shared countdown; `frame` itself
                // is now an inert husk (disposing it is a no-op), so no isLast special-casing.
                toSubmit = views[i];
            }
            else
            {
                try
                {
                    toSubmit = isLast ? frame : VideoFrameCpuClone.DuplicateCpuBacking(frame, frame.ColorTransferHint);
                }
                catch (Exception ex)
                {
                    Trace.LogTrace(ex, "ClipCompositionRuntime.Pump: clone failed for {Line}", output.DisplayName);
                    if (isLast) frame.Dispose();
                    continue;
                }
            }

            SubmitToOutput(output, toSubmit);
        }

        sw.Stop();
        RecordPumpTiming(sw.Elapsed, _canvasPeriod);
    }

    private bool TryPumpIntegratedMultiWarp(TimeSpan? masterPts, IReadOnlyList<AcquiredOutput> snapshot, Stopwatch sw)
    {
        if (_compositionMappingStage is not null
            || _integratedWarpActive
            || _compositor is not IWarpPassVideoCompositor
            || snapshot.Count < 2
            || _mixer.HasSurfaceSlots) // surfaces composite on the plain path (CompositeWithSurfaces)
            return false;

        var requests = new WarpOutputRequest[snapshot.Count];
        var hasMappedOutput = false;
        for (var i = 0; i < snapshot.Count; i++)
        {
            var stage = snapshot[i].MappingStage;
            if (stage is null)
            {
                requests[i] = new WarpOutputRequest(_canvasFormat, null);
                continue;
            }

            hasMappedOutput = true;
            requests[i] = new WarpOutputRequest(stage.OutputFormat, stage.WarpSections);
        }

        if (!hasMappedOutput)
            return false;

        IReadOnlyList<VideoFrame> frames;
        var compositeStarted = Stopwatch.GetTimestamp();
        try
        {
            if (!_mixer.TryReadNextFrames(masterPts, requests, out frames))
                return true;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(
                ex,
                "ClipCompositionRuntime.Pump: integrated multi-output warp failed for {Composition}; falling back to chained mapping",
                CompositionName);
            return false;
        }

        if (frames.Count != snapshot.Count)
        {
            DisposeFrames(frames);
            Trace.LogWarning(
                "ClipCompositionRuntime.Pump: integrated multi-output warp returned {FrameCount} frames for {OutputCount} outputs in {Composition}; falling back to chained mapping",
                frames.Count,
                snapshot.Count,
                CompositionName);
            return false;
        }

        _compositeTiming.RecordSince(compositeStarted);
        Interlocked.Increment(ref _framesComposited);
        for (var i = 0; i < snapshot.Count; i++)
            SubmitToOutput(snapshot[i], frames[i]);

        sw.Stop();
        RecordPumpTiming(sw.Elapsed, _canvasPeriod);
        return true;
    }

    private static void DisposeFrames(IReadOnlyList<VideoFrame> frames)
    {
        for (var i = 0; i < frames.Count; i++)
            frames[i].Dispose();
    }

    /// <summary>Configures the output on format change (idempotent per format) and submits;
    /// disposes the frame on submit failure.</summary>
    private void SubmitToOutput(AcquiredOutput output, VideoFrame toSubmit)
    {
        try
        {
            if (output.TrySubmit(toSubmit))
                Interlocked.Increment(ref _framesSubmitted);
            else
                toSubmit.Dispose();
        }
        catch (Exception ex)
        {
            Trace.LogTrace(ex, "ClipCompositionRuntime.Pump: Submit failed for {Line}", output.DisplayName);
            toSubmit.Dispose();
        }
    }

    private void RecordPumpTiming(TimeSpan elapsed, TimeSpan budget)
    {
        _pumpTiming.Record(elapsed);
        Volatile.Write(ref _lastPumpFrameTicks, elapsed.Ticks);
        UpdateMaxTicks(ref _maxPumpFrameTicks, elapsed.Ticks);

        if (elapsed <= budget)
            return;

        var overruns = Interlocked.Increment(ref _pumpOverruns);
        if (overruns == 1 || overruns % 120 == 0)
        {
            Trace.LogWarning(
                "ClipCompositionRuntime: composition {Composition} pump over budget ({ElapsedMs:0.00}ms > {BudgetMs:0.00}ms, layers={Layers}, slotOverflow={Overflow})",
                CompositionName,
                elapsed.TotalMilliseconds,
                budget.TotalMilliseconds,
                LayerCount,
                GetStats().SlotOverflowFrames);
        }
    }

    private static void UpdateMaxTicks(ref long target, long candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
                return;
        }
    }

    private void SortLayersLocked()
    {
        _slots.Sort(static (a, b) =>
        {
            var cmp = a.LayerIndex.CompareTo(b.LayerIndex);
            return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence);
        });

        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _slots.Count; i++)
            order[_slots[i].RawSlot.Id] = i;

        _mixer.SortSlots((a, b) =>
        {
            var ai = order.TryGetValue(a.Id, out var av) ? av : int.MaxValue;
            var bi = order.TryGetValue(b.Id, out var bv) ? bv : int.MaxValue;
            return ai.CompareTo(bi);
        });
    }

    internal void RaiseOutputPumpPressure(string outputId, string outputName, long droppedTotal, long droppedSinceLastReport)
    {
        try
        {
            PumpPressureWarning?.Invoke(this, new ClipCompositionPumpPressureWarning(
                CompositionId,
                CompositionName,
                outputId,
                outputName,
                droppedSinceLastReport,
                droppedTotal));
        }
        catch (Exception ex)
        {
            Trace.LogTrace(ex, "ClipCompositionRuntime: PumpPressureWarning handler threw");
        }
    }

    public void Dispose()
    {
        MediaClock? slaveClock;
        List<AcquiredOutput> acquiredToRetire;
        List<SubtitleLayerFeed> subtitleFeeds;
        List<SurfaceLayerSlot> surfaceLayers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            slaveClock = _slaveClock;
            acquiredToRetire = _acquired.ToList();
            subtitleFeeds = _subtitleFeeds.ToList();
            _subtitleFeeds.Clear();
            surfaceLayers = _surfaceLayers.ToList();
            _surfaceLayers.Clear();
            RepublishSubtitleFeedsSnapshot();

            // Retire mapping stages up front so the driver-thread dispose window below (and the
            // direct drain fallback at the end) can tear their compositors down.
            if (_compositionMappingStage is { } compositionStage)
            {
                _compositionMappingStage = null;
                _retiredMappingStages.Enqueue(compositionStage);
            }
        }

        foreach (var feed in subtitleFeeds)
            feed.Dispose();
        foreach (var surfaceLayer in surfaceLayers)
            surfaceLayer.Dispose();

        foreach (var acquired in acquiredToRetire)
        {
            if (acquired.Retire("ClipCompositionRuntime.Dispose") is { } stage)
                _retiredMappingStages.Enqueue(stage);
        }

        if (slaveClock is not null && _disposeCompositorOnDriverThread is not null)
        {
            Interlocked.Exchange(ref _driverDisposeState, 1);
            var deadline = Environment.TickCount64 + 250;
            while (Volatile.Read(ref _driverDisposeState) != 2 && Environment.TickCount64 < deadline)
                Thread.Sleep(1);
        }

        if (slaveClock is { } sc)
        {
            try { sc.VideoTick -= OnSlaveVideoTick; } catch { /* best effort */ }
            try { sc.Stop(); } catch { /* best effort */ }
            try { sc.Dispose(); } catch { /* best effort */ }
            lock (_gate)
                _slaveClock = null;
        }

        lock (_gate)
        {
            _slots.Clear();
            _acquired.Clear();
            RepublishAcquiredSnapshot();
        }

        // Best-effort fallback for stages the driver window didn't reach (pump never started, or
        // the dispose deadline lapsed) - mirrors the direct canvas-compositor dispose below.
        DrainRetiredMappingStages();

        MediaDiagnostics.SwallowDisposeErrors(_mixer.Dispose, "ClipCompositionRuntime.Dispose: mixer");
        MediaDiagnostics.SwallowDisposeErrors(_compositor.Dispose, "ClipCompositionRuntime.Dispose: compositor");
    }

    /// <summary>
    /// Per-output warp stage: composites the canvas frame's mapping sections into an output-sized
    /// frame on the pump thread. The mapping compositor is created lazily on the pump thread (GL
    /// context affinity) and shared across section-only updates via a boxed holder so live editor
    /// drags never churn GL contexts; it is disposed on the pump thread via the retired-stage queue.
    /// </summary>
    private sealed class OutputMappingStage
    {
        private sealed class CompositorBox
        {
            public IVideoCompositor? Compositor;
            public Action? DisposeOnDriverThread;
        }

        private readonly List<ResolvedMappingSection> _sections;
        private readonly WarpSection[] _warpSections;
        private readonly CompositorBox _box;
        private readonly List<CompositorLayer> _layerScratch = new();

        public OutputMappingStage(VideoFormat outputFormat, List<ResolvedMappingSection> sections)
            : this(outputFormat, sections, new CompositorBox())
        {
        }

        private OutputMappingStage(VideoFormat outputFormat, List<ResolvedMappingSection> sections, CompositorBox box)
        {
            OutputFormat = outputFormat;
            _sections = sections;
            _warpSections = CreateWarpSections(sections);
            _box = box;
        }

        public VideoFormat OutputFormat { get; }

        public WarpSection[] WarpSections => _warpSections;

        /// <summary>Section-only update at the same output size: the new stage shares the live
        /// compositor (boxed), so nothing needs disposal.</summary>
        public OutputMappingStage WithSections(List<ResolvedMappingSection> sections) =>
            new(OutputFormat, sections, _box);

        /// <summary>Sections in the shape the integrated GPU warp pass consumes.</summary>
        public WarpSection[] BuildWarpSections() => _warpSections;

        private static WarpSection[] CreateWarpSections(IReadOnlyList<ResolvedMappingSection> resolvedSections)
        {
            var warpSections = new WarpSection[resolvedSections.Count];
            for (var i = 0; i < resolvedSections.Count; i++)
                warpSections[i] = new WarpSection(
                    resolvedSections[i].SourceCrop,
                    resolvedSections[i].Transform,
                    resolvedSections[i].Opacity,
                    resolvedSections[i].Mesh);
            return warpSections;
        }

        /// <summary>True when any section carries a mesh warp - needs the GL warp pass; the CPU
        /// fallback renders such sections with their affine placement instead.</summary>
        private bool HasMeshSection()
        {
            foreach (var section in _sections)
            {
                if (section.Mesh is not null)
                    return true;
            }

            return false;
        }

        private bool _warpModeApplied;
        private bool _meshFallbackWarned;

        /// <summary>Pump thread only. The canvas frame is borrowed - the compositor never takes
        /// ownership, so the caller keeps disposing it.</summary>
        public VideoFrame Composite(VideoFrame canvas, Func<VideoFormat, ClipCompositionCompositor> compositorFactory)
        {
            var compositor = _box.Compositor;
            if (compositor is null)
            {
                var created = compositorFactory(OutputFormat);
                compositor = created.Compositor
                             ?? throw new InvalidOperationException("Compositor factory returned null compositor.");
                compositor.Configure(OutputFormat);
                _box.Compositor = compositor;
                _box.DisposeOnDriverThread = created.DisposeOnDriverThread;
            }

            if (compositor is IWarpPassVideoCompositor warpCapable)
            {
                // Warp mode (GL): composite the canvas as one identity layer, then the compositor's
                // integrated warp pass cuts/warps it into the output - the same pixel path as the
                // single-output integrated warp, and the only path that renders mesh sections.
                // Applied once per stage instance; section edits arrive as a new instance sharing
                // the boxed compositor (WithSections), re-applying here on its first frame.
                if (!_warpModeApplied)
                {
                    warpCapable.Configure(canvas.Format);
                    warpCapable.SetWarpPass(OutputFormat, BuildWarpSections());
                    _warpModeApplied = true;
                }

                _layerScratch.Clear();
                _layerScratch.Add(new CompositorLayer(canvas, LayerTransform2D.Identity, 1f, BlendMode.Source));
                return compositor.Composite(_layerScratch, canvas.PresentationTime);
            }

            if (!_meshFallbackWarned && HasMeshSection())
            {
                _meshFallbackWarned = true;
                Trace.LogWarning(
                    "Output mapping has mesh-warp sections but the compositor backend is CPU-only; rendering them with their affine placement (mesh warp requires the GL compositor).");
            }

            _layerScratch.Clear();
            foreach (var section in _sections)
            {
                _layerScratch.Add(new CompositorLayer(canvas, section.Transform, section.Opacity, BlendMode.SourceOver)
                {
                    SourceCrop = section.SourceCrop,
                });
            }

            return compositor.Composite(_layerScratch, canvas.PresentationTime);
        }

        /// <summary>Idempotent (the box is shared across section updates and emptied on first call).</summary>
        public void DisposeCompositor()
        {
            var driver = _box.DisposeOnDriverThread;
            _box.DisposeOnDriverThread = null;
            var compositor = _box.Compositor;
            _box.Compositor = null;

            if (driver is not null)
                MediaDiagnostics.SwallowDisposeErrors(driver, "ClipCompositionRuntime: mapping compositor driver dispose");
            if (compositor is not null)
                MediaDiagnostics.SwallowDisposeErrors(compositor.Dispose, "ClipCompositionRuntime: mapping compositor dispose");
        }
    }

    private sealed class AcquiredOutput
    {
        private readonly ClipCompositionOutputLease _lease;
        private readonly object _lifecycleGate = new();
        private EventHandler<VideoOutputPumpPressureEventArgs>? _pressureHandler;
        private long _lastReportedDrops;
        private long _nextReportTicks;
        private volatile OutputMappingStage? _mappingStage;
        private VideoFormat? _configuredFormat;
        private bool _retired;

        public AcquiredOutput(ClipCompositionOutputLease lease)
        {
            _lease = lease;
        }

        public string OutputId => _lease.OutputId;

        public string DisplayName => _lease.DisplayName;

        public IVideoOutput Output => _lease.Output;

        public Action? Release => _lease.Release;

        public bool DisposeOutputOnRuntimeDispose => _lease.DisposeOutputOnRuntimeDispose;

        /// <summary>Current mapping stage, or null when this output receives the raw canvas.
        /// Volatile snapshot - the pump reads it once per frame, the UI thread swaps it.</summary>
        public OutputMappingStage? MappingStage => _mappingStage;

        /// <summary>Swaps the mapping. When the output canvas size is unchanged the existing
        /// compositor carries over (no GL churn while dragging in the editor); otherwise the old
        /// stage is handed back via <paramref name="retired"/> for driver-thread disposal.</summary>
        public bool SetMapping(ClipOutputMappingSpec? mapping, VideoFormat canvasFormat, out OutputMappingStage? retired)
        {
            lock (_lifecycleGate)
            {
                retired = null;
                if (_retired)
                    return false;

                var current = _mappingStage;

                if (mapping is null)
                {
                    _mappingStage = null;
                    retired = current;
                    return true;
                }

                var outputFormat = OutputMappingResolver.ResolveOutputFormat(mapping, canvasFormat);
                var sections = OutputMappingResolver.Resolve(mapping, canvasFormat.Width, canvasFormat.Height);
                if (current is not null && current.OutputFormat == outputFormat)
                {
                    _mappingStage = current.WithSections(sections);
                    return true;
                }

                _mappingStage = new OutputMappingStage(outputFormat, sections);
                retired = current;
                return true;
            }
        }

        /// <summary>Configure-on-change: mapped and unmapped outputs see different formats, and a
        /// live mapping resize changes the format mid-run. Configure is idempotent downstream.</summary>
        private void EnsureConfigured(VideoFormat format)
        {
            if (_configuredFormat == format)
                return;
            Output.Configure(format);
            _configuredFormat = format;
        }

        public bool TrySubmit(VideoFrame frame)
        {
            lock (_lifecycleGate)
            {
                if (_retired)
                    return false;

                EnsureConfigured(frame.Format);
                Output.Submit(frame);
                return true;
            }
        }

        public void SubscribePumpPressure(ClipCompositionRuntime owner)
        {
            if (Output is not VideoOutputPump pump)
                return;

            _pressureHandler = (_, args) =>
            {
                var nowTicks = Environment.TickCount64;
                if (nowTicks < _nextReportTicks) return;
                var newDrops = args.DroppedFramesTotal - _lastReportedDrops;
                if (newDrops <= 0) return;
                _lastReportedDrops = args.DroppedFramesTotal;
                _nextReportTicks = nowTicks + 5000;
                owner.RaiseOutputPumpPressure(OutputId, DisplayName, args.DroppedFramesTotal, newDrops);
            };
            pump.PumpPressure += _pressureHandler;
        }

        public OutputMappingStage? Retire(string operation)
        {
            lock (_lifecycleGate)
            {
                if (_retired)
                    return null;

                _retired = true;
                var retired = _mappingStage;
                _mappingStage = null;
                UnsubscribePumpPressureCore();

                if (DisposeOutputOnRuntimeDispose && Output is IDisposable disposable)
                    MediaDiagnostics.SwallowDisposeErrors(disposable.Dispose, $"{operation}: output dispose");
                if (Release is not null)
                    MediaDiagnostics.SwallowDisposeErrors(Release, $"{operation}: output release");

                return retired;
            }
        }

        private void UnsubscribePumpPressureCore()
        {
            if (_pressureHandler is null || Output is not VideoOutputPump pump)
                return;
            pump.PumpPressure -= _pressureHandler;
            _pressureHandler = null;
        }
    }

    /// <summary>
    /// The placement surface a clip's composition layer exposes regardless of HOW it renders - a decoded
    /// frame slot (<see cref="LayerSlot"/>) or a GPU layer surface (<see cref="SurfaceLayerSlot"/>, NXT-10).
    /// The session's fade rides, live placement edits, and teardown all go through this contract, so a
    /// surface-backed clip behaves exactly like a frame-backed one for transport purposes.
    /// </summary>
    public interface IPlacedClipLayer : IDisposable
    {
        int LayerIndex { get; }
        float Opacity { get; set; }
        void UpdatePlacement(VideoPlacementSpec placement);
    }

    /// <summary>
    /// One GPU layer surface placed on this composition (NXT-10). Mirrors <see cref="LayerSlot"/>'s
    /// placement semantics (dest rect/fit/rotation/opacity resolve identically; the surface's nominal
    /// source size is the canvas). Disposing removes the layer and disposes the SURFACE - the runtime
    /// owns surface lifetime once placed.
    /// </summary>
    public sealed class SurfaceLayerSlot : IPlacedClipLayer
    {
        private readonly ClipCompositionRuntime _owner;
        private readonly bool _ownsSurface;
        internal VideoCompositorSource.SurfaceSlot RawSlot { get; }
        private VideoPlacementSpec _placement;
        private int _disposed;

        internal SurfaceLayerSlot(
            ClipCompositionRuntime owner,
            VideoCompositorSource.SurfaceSlot slot,
            VideoPlacementSpec placement,
            bool ownsSurface = true)
        {
            _owner = owner;
            RawSlot = slot;
            _placement = placement;
            _ownsSurface = ownsSurface;
            Sequence = Interlocked.Increment(ref owner._nextLayerSequence);
        }

        public IVideoCompositorLayerSurface Surface => RawSlot.Surface;

        public int LayerIndex => _placement.LayerIndex;

        public long Sequence { get; }

        public float Opacity
        {
            get => RawSlot.Opacity;
            set => RawSlot.Opacity = value;
        }

        public void UpdatePlacement(VideoPlacementSpec placement)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ArgumentNullException.ThrowIfNull(placement);
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                var resort = placement.LayerIndex != _placement.LayerIndex;
                _placement = placement;
                ApplyPlacement();
                if (resort)
                    _owner.SortSurfaceLayersLocked();
            }
        }

        /// <summary>Resolves the placement to the surface's canvas transform - the same
        /// <see cref="PlacementResolver"/> math a frame layer uses, with the canvas as the source size
        /// (surfaces render canvas-resolution content; a full-canvas stretch is the identity).</summary>
        internal void ApplyPlacement()
        {
            var destRect = new RectNormalized(
                (float)_placement.DestX,
                (float)_placement.DestY,
                (float)(_placement.DestX + _placement.DestWidth),
                (float)(_placement.DestY + _placement.DestHeight));
            var (transform, _) = PlacementResolver.Resolve(
                destRect,
                LayerSlot.MapFit(_placement.Placement),
                0f, 0f, 0f, 0f,
                _owner._canvasFormat,
                _owner._canvasFormat);

            if (_placement.RotationDegrees != 0)
            {
                var rad = (float)(_placement.RotationDegrees * Math.PI / 180.0);
                var cx = (float)((_placement.DestX + _placement.DestWidth * 0.5) * _owner._canvasFormat.Width);
                var cy = (float)((_placement.DestY + _placement.DestHeight * 0.5) * _owner._canvasFormat.Height);
                transform = LayerTransform2D.Compose(
                    LayerTransform2D.Translate(cx, cy),
                    LayerTransform2D.Compose(
                        LayerTransform2D.Rotate(rad),
                        LayerTransform2D.Compose(LayerTransform2D.Translate(-cx, -cy), transform)));
            }

            RawSlot.Transform = transform;
            RawSlot.Opacity = Math.Clamp((float)_placement.Opacity, 0f, 1f);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.RemoveSurfaceLayer(this);
            // A non-owning slot shares its surface with the owning slot (one surface, several placements);
            // only the owner disposes it, so the shared surface is not torn down while still rendered.
            if (_ownsSurface)
                Surface.Dispose();
        }
    }

    public sealed class LayerSlot : IDisposable, IPlacedClipLayer
    {
        private readonly ClipCompositionRuntime _owner;
        internal VideoCompositorSource.Slot RawSlot { get; }
        private readonly VideoFormat _source;
        private VideoPlacementSpec _placement;
        private int _disposed;

        internal LayerSlot(
            ClipCompositionRuntime owner,
            VideoCompositorSource.Slot slot,
            VideoFormat source,
            VideoPlacementSpec placement,
            long sequence)
        {
            _owner = owner;
            RawSlot = slot;
            _source = source;
            _placement = placement;
            Sequence = sequence;
        }

        public IVideoOutput Output => RawSlot.Output;

        public int LayerIndex => _placement.LayerIndex;

        public float Opacity
        {
            get => RawSlot.Opacity;
            set => RawSlot.Opacity = Math.Clamp(value, 0f, 1f);
        }

        public long Sequence { get; }

        public void UpdatePlacement(VideoPlacementSpec placement)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ArgumentNullException.ThrowIfNull(placement);

            var resort = false;
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
                resort = placement.LayerIndex != _placement.LayerIndex;
                _placement = placement;
                ApplyPlacement();
                if (resort)
                    _owner.SortLayersLocked();
            }
        }

        public void ApplyPlacement()
        {
            var destRect = new RectNormalized(
                (float)_placement.DestX,
                (float)_placement.DestY,
                (float)(_placement.DestX + _placement.DestWidth),
                (float)(_placement.DestY + _placement.DestHeight));
            if (_placement.VideoFx is { } videoFx)
            {
                ApplyMappedPlacement(destRect, videoFx);
                return;
            }

            var (transform, crop) = PlacementResolver.Resolve(
                destRect,
                MapFit(_placement.Placement),
                (float)_placement.CropLeft,
                (float)_placement.CropTop,
                (float)_placement.CropRight,
                (float)_placement.CropBottom,
                _source,
                _owner._canvasFormat);

            // Per-layer rotation: spin the already-placed image about its destination-rect centre (in canvas
            // pixels). The compositor applies the full affine, so this works on both the GL and CPU backends.
            if (_placement.RotationDegrees != 0)
            {
                var rad = (float)(_placement.RotationDegrees * Math.PI / 180.0);
                var cx = (float)((_placement.DestX + _placement.DestWidth * 0.5) * _owner._canvasFormat.Width);
                var cy = (float)((_placement.DestY + _placement.DestHeight * 0.5) * _owner._canvasFormat.Height);
                transform = LayerTransform2D.Compose(
                    LayerTransform2D.Translate(cx, cy),
                    LayerTransform2D.Compose(
                        LayerTransform2D.Rotate(rad),
                        LayerTransform2D.Compose(LayerTransform2D.Translate(-cx, -cy), transform)));
            }

            RawSlot.MappingSections = null;
            RawSlot.Transform = transform;
            RawSlot.SourceCrop = crop;
            RawSlot.Opacity = Math.Clamp((float)_placement.Opacity, 0f, 1f);
            RawSlot.BlendMode = BlendMode.SourceOver;
        }

        private void ApplyMappedPlacement(RectNormalized destRect, ClipOutputMappingSpec videoFx)
        {
            var effectFormat = OutputMappingResolver.ResolveOutputFormat(videoFx, _source);
            var (effectTransform, _) = PlacementResolver.Resolve(
                destRect,
                MapFit(_placement.Placement),
                0f,
                0f,
                0f,
                0f,
                effectFormat,
                _owner._canvasFormat);

            effectTransform = ApplyPlacementRotation(effectTransform);

            var sourceBounds = new RectNormalized(
                Math.Clamp((float)_placement.CropLeft, 0f, 0.99f),
                Math.Clamp((float)_placement.CropTop, 0f, 0.99f),
                1f - Math.Clamp((float)_placement.CropRight, 0f, 0.99f),
                1f - Math.Clamp((float)_placement.CropBottom, 0f, 0.99f)).Clamped();

            var resolved = OutputMappingResolver.Resolve(
                videoFx,
                _source.Width,
                _source.Height,
                sourceBounds);

            var sections = new WarpSection[resolved.Count];
            for (var i = 0; i < resolved.Count; i++)
            {
                var section = resolved[i];
                var transform = LayerTransform2D.Compose(effectTransform, section.Transform);
                sections[i] = new WarpSection(
                    section.SourceCrop,
                    transform,
                    section.Opacity,
                    section.Mesh is null ? null : TransformMesh(section.Mesh, effectTransform));
            }

            RawSlot.MappingSections = sections;
            RawSlot.Transform = LayerTransform2D.Identity;
            RawSlot.SourceCrop = RectNormalized.Full;
            RawSlot.Opacity = Math.Clamp((float)_placement.Opacity, 0f, 1f);
            RawSlot.BlendMode = BlendMode.SourceOver;
        }

        private LayerTransform2D ApplyPlacementRotation(LayerTransform2D transform)
        {
            if (_placement.RotationDegrees == 0)
                return transform;

            var rad = (float)(_placement.RotationDegrees * Math.PI / 180.0);
            var cx = (float)((_placement.DestX + _placement.DestWidth * 0.5) * _owner._canvasFormat.Width);
            var cy = (float)((_placement.DestY + _placement.DestHeight * 0.5) * _owner._canvasFormat.Height);
            return LayerTransform2D.Compose(
                LayerTransform2D.Translate(cx, cy),
                LayerTransform2D.Compose(
                    LayerTransform2D.Rotate(rad),
                    LayerTransform2D.Compose(LayerTransform2D.Translate(-cx, -cy), transform)));
        }

        private static WarpMesh TransformMesh(WarpMesh mesh, LayerTransform2D transform)
        {
            var points = new System.Numerics.Vector2[mesh.Points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var p = mesh.Points[i];
                var (x, y) = transform.Apply(p.X, p.Y);
                points[i] = new System.Numerics.Vector2(x, y);
            }

            return new WarpMesh(mesh.Columns, mesh.Rows, points);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.RemoveLayer(this);
        }

        internal static PlacementFit MapFit(string? placement) => placement?.ToLowerInvariant() switch
        {
            "letterbox" or "contain" or "center" => PlacementFit.Contain,
            "stretch" => PlacementFit.Stretch,
            "fillwidth" => PlacementFit.FillWidth,
            "fillheight" => PlacementFit.FillHeight,
            _ => PlacementFit.Cover,
        };
    }
}

public readonly record struct ClipCompositionRuntimeStats(
    string CompositionId,
    long FramesComposited,
    long FramesSubmitted,
    long PumpOverruns,
    long SlotOverflowFrames,
    TimeSpan LastPumpFrameTime,
    TimeSpan MaxPumpFrameTime,
    long FramesBehindMaster,
    bool ClockMastered,
    int LayerCount = 0,
    TimingSnapshot PumpTiming = default,
    TimingSnapshot CompositeTiming = default,
    TimeSpan CanvasPeriod = default);

public readonly record struct ClipCompositionDriftWarning(
    string CompositionId,
    string CompositionName,
    long FramesBehindMaster,
    TimeSpan LagFromMaster);

public readonly record struct ClipCompositionPumpPressureWarning(
    string CompositionId,
    string CompositionName,
    string OutputId,
    string OutputName,
    long DroppedSinceLastReport,
    long DroppedTotal);
