using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Effects;

namespace S.Media.Playback;

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
    bool DisposeOutputOnRuntimeDispose = false);

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
    private readonly List<LayerSlot> _slots = [];
    private readonly TimeSpan _canvasPeriod;
    private long _nextLayerSequence;
    private long _framesComposited;
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
    private MediaClock? _slaveClock;
    private int _driverDisposeState;
    private bool _outputsConfigured;
    private bool _disposed;

    public ClipCompositionRuntime(
        ClipCompositionDefinition definition,
        IReadOnlyList<ClipCompositionOutputLease> outputs,
        Func<VideoFormat, ClipCompositionCompositor>? compositorFactory = null)
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

        var compositor = (compositorFactory ?? CreateDefaultCompositor)(_canvasFormat);
        _compositor = compositor.Compositor ?? throw new InvalidOperationException("Compositor factory returned null compositor.");
        RequiresBgraLayerConversion = compositor.RequiresBgraLayerConversion;
        CompositorBackendName = string.IsNullOrWhiteSpace(compositor.BackendName) ? "Unknown" : compositor.BackendName;
        _disposeCompositorOnDriverThread = compositor.DisposeOnDriverThread;
        _mixer = new VideoCompositorSource(_canvasFormat, _compositor, disposeCompositorOnDispose: false);

        foreach (var output in outputs)
        {
            if (output.Output is null)
                continue;
            var acquired = new AcquiredOutput(output);
            _acquired.Add(acquired);
            acquired.SubscribePumpPressure(this);
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

    public long PumpStartCount => Volatile.Read(ref _pumpStartCount);

    public event EventHandler<ClipCompositionDriftWarning>? DriftWarning;

    public event EventHandler<ClipCompositionPumpPressureWarning>? PumpPressureWarning;

    public ClipCompositionRuntimeStats GetStats()
    {
        long slotOverflow = 0;
        lock (_gate)
        {
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
            _master is not null);
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

    public void SetClockMaster(IPlaybackClock master)
    {
        ArgumentNullException.ThrowIfNull(master);
        MediaClock? clockToRetarget = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_master is not null) return;
            _master = master;
            foreach (var layer in _slots)
                layer.RawSlot.KeepPolicy = SlotKeepPolicy.MasterAligned;
            clockToRetarget = _slaveClock;
        }

        clockToRetarget?.SetMaster(master);
        Trace.LogInformation(
            "ClipCompositionRuntime: composition {Composition} pump now slaved to master clock",
            CompositionName);
    }

    public LayerSlot AddLayer(VideoFormat sourceFormat, VideoPlacementSpec placement)
    {
        LayerSlot layer;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rawSlot = _mixer.AddSlot();
            if (_master is not null)
                rawSlot.KeepPolicy = SlotKeepPolicy.MasterAligned;
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
            try { _disposeCompositorOnDriverThread?.Invoke(); }
            catch (Exception ex) { Trace.LogWarning(ex, "ClipCompositionRuntime.OnSlaveVideoTick: driver compositor dispose"); }
            return;
        }
        if (_disposed) return;
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
        if (_master is not null)
        {
            try { masterPts = _master.ElapsedSinceStart; }
            catch (Exception ex) { Trace.LogTrace(ex, "ClipCompositionRuntime.PumpOneFrame: master read"); }
        }

        if (!_mixer.TryReadNextFrame(masterPts, out var frame))
            return;
        Interlocked.Increment(ref _framesComposited);

        List<AcquiredOutput> snapshot;
        lock (_gate) snapshot = _acquired.ToList();

        if (snapshot.Count == 0)
        {
            frame.Dispose();
            return;
        }

        if (!_outputsConfigured)
        {
            foreach (var output in snapshot)
            {
                try { output.Output.Configure(frame.Format); }
                catch (Exception ex)
                {
                    Trace.LogWarning(
                        ex,
                        "ClipCompositionRuntime.Pump: initial Configure failed for {Line}",
                        output.DisplayName);
                }
            }
            _outputsConfigured = true;
        }

        // Multi-output fan-out is zero-copy when the canvas is CPU-backed (the CpuVideoCompositor's
        // pool-rented buffer — the common case): every output gets a refcounted view over the same
        // pixels and the canvas returns to the pool once the last view is disposed. The per-output
        // deep copies this replaces were ~8 MB × (outputs−1) of memcpy per 1080p frame. Fallback to
        // cloning covers non-CPU backings (GL compositor) — TryCreateCpuFanOutViews leaves the frame
        // untouched when it declines.
        VideoFrame[]? views = null;
        if (snapshot.Count > 1)
            VideoFrame.TryCreateCpuFanOutViews(frame, snapshot.Count, frame.ColorTransferHint, out views);

        for (var i = 0; i < snapshot.Count; i++)
        {
            var output = snapshot[i];
            var isLast = i == snapshot.Count - 1;
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

            try
            {
                output.Output.Submit(toSubmit);
                Interlocked.Increment(ref _framesSubmitted);
            }
            catch (Exception ex)
            {
                Trace.LogTrace(ex, "ClipCompositionRuntime.Pump: Submit failed for {Line}", output.DisplayName);
                toSubmit.Dispose();
            }
        }

        sw.Stop();
        RecordPumpTiming(sw.Elapsed, _canvasPeriod);
    }

    private void RecordPumpTiming(TimeSpan elapsed, TimeSpan budget)
    {
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            slaveClock = _slaveClock;
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
            foreach (var acquired in _acquired)
            {
                acquired.UnsubscribePumpPressure();
                if (acquired.DisposeOutputOnRuntimeDispose && acquired.Output is IDisposable disposable)
                    MediaDiagnostics.SwallowDisposeErrors(disposable.Dispose, "ClipCompositionRuntime.Dispose: output dispose");
                if (acquired.Release is not null)
                    MediaDiagnostics.SwallowDisposeErrors(acquired.Release, "ClipCompositionRuntime.Dispose: output release");
            }
            _acquired.Clear();
        }

        MediaDiagnostics.SwallowDisposeErrors(_mixer.Dispose, "ClipCompositionRuntime.Dispose: mixer");
        MediaDiagnostics.SwallowDisposeErrors(_compositor.Dispose, "ClipCompositionRuntime.Dispose: compositor");
    }

    private sealed class AcquiredOutput
    {
        private readonly ClipCompositionOutputLease _lease;
        private EventHandler<VideoOutputPumpPressureEventArgs>? _pressureHandler;
        private long _lastReportedDrops;
        private long _nextReportTicks;

        public AcquiredOutput(ClipCompositionOutputLease lease)
        {
            _lease = lease;
        }

        public string OutputId => _lease.OutputId;

        public string DisplayName => _lease.DisplayName;

        public IVideoOutput Output => _lease.Output;

        public Action? Release => _lease.Release;

        public bool DisposeOutputOnRuntimeDispose => _lease.DisposeOutputOnRuntimeDispose;

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

        public void UnsubscribePumpPressure()
        {
            if (_pressureHandler is null || Output is not VideoOutputPump pump)
                return;
            pump.PumpPressure -= _pressureHandler;
            _pressureHandler = null;
        }
    }

    public sealed class LayerSlot : IDisposable
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
            var (transform, crop) = PlacementResolver.Resolve(
                destRect,
                MapFit(_placement.Placement),
                (float)_placement.CropLeft,
                (float)_placement.CropTop,
                (float)_placement.CropRight,
                (float)_placement.CropBottom,
                _source,
                _owner._canvasFormat);
            RawSlot.Transform = transform;
            RawSlot.SourceCrop = crop;
            RawSlot.Opacity = Math.Clamp((float)_placement.Opacity, 0f, 1f);
            RawSlot.BlendMode = BlendMode.SourceOver;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.RemoveLayer(this);
        }

        private static PlacementFit MapFit(string? placement) => placement?.ToLowerInvariant() switch
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
    bool ClockMastered);

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
