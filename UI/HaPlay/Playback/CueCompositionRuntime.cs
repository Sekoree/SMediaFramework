using HaPlay.Models;
using HaPlay.ViewModels;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Effects;
using S.Media.SDL3;

namespace HaPlay.Playback;

/// <summary>
/// One per active <see cref="CueComposition"/>. Owns a <see cref="VideoCompositorSource"/> (the
/// mixer), the physical <see cref="IVideoOutput"/>s acquired from <c>OutputManagementView</c> for
/// every <see cref="CueVideoOutputBinding"/> targeting this composition, and a pump task that
/// pulls composed frames from the compositor at the composition's framerate and Submits them to
/// each acquired output.
/// </summary>
/// <remarks>
/// Cues feed their video into this runtime by calling <see cref="AddLayer"/> with the cue's
/// source <see cref="VideoFormat"/> and the placement's <see cref="CueVideoPlacement"/>. The
/// returned <see cref="LayerSlot"/> exposes an <see cref="IVideoOutput"/> the cue's
/// <c>MediaPlayer.VideoRouter</c> uses as its downstream — frames flow
/// decoder → MediaPlayer.VideoRouter → slot.Output → compositor → physical outputs.
/// </remarks>
internal sealed class CueCompositionRuntime : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.CueCompositionRuntime");

    private readonly OutputManagementViewModel _outputs;
    private readonly CueComposition _composition;
    private readonly VideoFormat _canvasFormat;
    private readonly IVideoCompositor _compositor;
    private readonly SDL3GLVideoCompositor? _gpuCompositor;
    private readonly VideoCompositorSource _mixer;
    private readonly bool _requiresBgraLayerConversion;
    private readonly string _compositorBackendName;
    private readonly object _gate = new();
    private readonly List<AcquiredOutput> _acquired = new();
    private readonly List<LayerSlot> _slots = new();
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
    private IPlaybackClock? _master;
    private MediaClock? _slaveClock;
    // 0 = no request, 1 = dispose requested (Dispose() is waiting), 2 = driver thread ran GL dispose.
    // Used to hop the GL compositor's teardown back onto its owner thread before we stop the clock.
    private int _driverDisposeState;
    private bool _disposed;

    public CueCompositionRuntime(
        CueComposition composition,
        IReadOnlyList<OutputLineViewModel> targetLines,
        OutputManagementViewModel outputs)
    {
        _composition = composition;
        _outputs = outputs;

        var den = Math.Max(1, composition.FrameRateDen);
        var num = Math.Max(1, composition.FrameRateNum);
        var rate = new Rational(num, den);
        _canvasFormat = new VideoFormat(
            Math.Max(16, composition.Width),
            Math.Max(16, composition.Height),
            PixelFormat.Bgra32,
            rate);
        _canvasPeriod = TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond * (long)den / Math.Max(1L, (long)num)));

        (_compositor, _gpuCompositor, _requiresBgraLayerConversion, _compositorBackendName) =
            CreateCompositor(_canvasFormat, composition);
        _mixer = new VideoCompositorSource(_canvasFormat, _compositor, disposeCompositorOnDispose: false);

        // Acquire each physical output bound to this composition. Failures are logged but don't
        // abort — the operator just sees fewer outputs lit up.
        foreach (var line in targetLines)
        {
            try
            {
                if (line.Definition is Models.LocalVideoOutputDefinition)
                {
                    var output = outputs.TryAcquireLocalVideoOutputForPlayback(line);
                    if (output is not null)
                        _acquired.Add(new AcquiredOutput(line, output, AcquiredKind.Local));
                }
                else if (line.Definition is Models.NDIOutputDefinition nd
                         && nd.StreamMode != Models.NDIOutputStreamMode.AudioOnly)
                {
                    var ndi = outputs.TryAcquireNDICarrierForPlayback(line, needsVideo: true, needsAudio: false);
                    if (ndi is not null)
                    {
                        // NDIVideoSender.Submit is blocking (network send + format pack +
                        // per-frame minimum-submit-spacing pacing). Letting my pump call it
                        // inline would stall composition for every other output in the same
                        // tick AND back-pressure NDI's pace until frames pile up unsubmitted —
                        // the "NDI monitor doesn't even update" symptom. The framework's NDI
                        // path always wraps the sender in a VideoOutputPump (background queue,
                        // drop-oldest) for exactly this reason.
                        IVideoOutput sink = ndi.Video;
                        var name = $"cuecomp-ndi-{nd.Id:N}";
                        sink = WrapWithNDILockIfNeeded(sink, nd, name);
                        var pump = new S.Media.Core.Video.VideoOutputPump(
                            sink,
                            maxQueuedFrames: 8,
                            name: name,
                            log: null,
                            disposeInnerOnDispose: false);
                        // Phase 5.7.3 — surface receiver-can't-keep-up to the operator. The
                        // pump fires per-drop; we throttle and forward to the host.
                        var capturedLine = line;
                        long lastReportedDrops = 0;
                        var nextReportTicks = 0L;
                        pump.PumpPressure += (_, args) =>
                        {
                            var nowTicks = Environment.TickCount64;
                            if (nowTicks < nextReportTicks) return;
                            var newDrops = args.DroppedFramesTotal - lastReportedDrops;
                            if (newDrops <= 0) return;
                            lastReportedDrops = args.DroppedFramesTotal;
                            nextReportTicks = nowTicks + 5000;
                            try
                            {
                                PumpPressureWarning?.Invoke(this, new CueCompositionPumpPressureWarning(
                                    _composition.Id,
                                    _composition.Name,
                                    capturedLine.Definition.Id,
                                    capturedLine.Definition.DisplayName,
                                    newDrops,
                                    args.DroppedFramesTotal));
                            }
                            catch (Exception ex)
                            {
                                Trace.LogTrace(ex, "CueCompositionRuntime: PumpPressureWarning handler threw");
                            }
                        };
                        _acquired.Add(new AcquiredOutput(line, pump, AcquiredKind.Ndi));
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "CueCompositionRuntime: failed to acquire output {Line}", line.Definition.DisplayName);
            }
        }
    }

    public Guid CompositionId => _composition.Id;

    public VideoFormat CanvasFormat => _canvasFormat;

    public bool RequiresBgraLayerConversion => _requiresBgraLayerConversion;

    public string CompositorBackendName => _compositorBackendName;

    public int LayerCount
    {
        get { lock (_gate) return _slots.Count; }
    }

    public CueCompositionRuntimeStats GetStats()
    {
        long slotOverflow = 0;
        lock (_gate)
        {
            foreach (var slot in _slots)
                slotOverflow += slot.RawSlot.OverflowFrames;
        }

        return new CueCompositionRuntimeStats(
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

    /// <summary>Number of times the pump has been started. Used by tests to detect double-start
    /// regressions on the master-clock path; never &gt; 1 in correct operation.</summary>
    internal long PumpStartCount => Volatile.Read(ref _pumpStartCount);

    /// <summary>Raised once per ~5 s when the composition pump detects sustained drift between
    /// its own tick rate and the master clock. <see cref="MainViewModel"/> can subscribe and
    /// translate to a status message; for now it's diagnostic only.</summary>
    public event EventHandler<CueCompositionDriftWarning>? DriftWarning;

    /// <summary>Raised when an NDI output pump drops frames because the receiver/network can't
    /// keep up (Phase 5.7.3). Throttled per-output to once every ~5 s — the underlying pump
    /// fires on every drop, which would flood the UI in a real overload.</summary>
    public event EventHandler<CueCompositionPumpPressureWarning>? PumpPressureWarning;

    /// <summary>Starts the pump that pulls composed frames at the canvas framerate and Submits
    /// to each acquired output. Idempotent — the runtime uses exactly one <see cref="MediaClock"/>
    /// driver thread for its lifetime so the GL compositor's context owner thread never changes
    /// (Phase 5.4 fix). When a master is later supplied via <see cref="SetClockMaster"/> the same
    /// clock's master is swapped in place; we never spawn a second driver.</summary>
    public void EnsurePumpStarted()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_slaveClock is not null) return;
            StartPumpLocked();
        }
    }

    /// <summary>Slave the composition pump's cadence to a <see cref="IPlaybackClock"/> (typically
    /// the first cue's audio runtime clock). The pump's underlying <see cref="MediaClock"/> is
    /// created on first <see cref="EnsurePumpStarted"/> with no master (canvas-period free-run);
    /// this call swaps its master in place. The driver thread is preserved, so the GL compositor's
    /// context owner thread doesn't change. Multiple calls are safe; only the first non-null master
    /// is taken so the master doesn't flip between concurrent cues.</summary>
    public void SetClockMaster(IPlaybackClock master)
    {
        ArgumentNullException.ThrowIfNull(master);
        MediaClock? clockToRetarget = null;
        lock (_gate)
        {
            if (_disposed) return;
            if (_master is not null) return; // first cue's master wins
            _master = master;
            foreach (var layer in _slots)
                layer.RawSlot.KeepPolicy = SlotKeepPolicy.MasterAligned;
            clockToRetarget = _slaveClock;
        }

        // Done outside the lock — MediaClock.SetMaster is safe to call concurrently with VideoTick
        // (it locks internally) and keeping the runtime lock here would risk an inversion with the
        // driver thread when it raises OnSlaveVideoTick.
        clockToRetarget?.SetMaster(master);
        Trace.LogInformation(
            "CueCompositionRuntime: composition {Composition} pump now slaved to master clock",
            _composition.Name);
    }

    private void StartPumpLocked()
    {
        if (_slaveClock is not null) return;

        // Audio tick interval doesn't matter to us; pick a coarse one to keep the driver thread quiet.
        var audioInterval = TimeSpan.FromMilliseconds(50);
        _slaveClock = new MediaClock(audioInterval, _canvasPeriod);
        if (_master is not null)
            _slaveClock.SetMaster(_master);
        _slaveClock.VideoTick += OnSlaveVideoTick;
        _slaveClock.Start();
        Interlocked.Increment(ref _pumpStartCount);
        Trace.LogInformation(
            "CueCompositionRuntime: composition {Composition} pump started (videoTick={PeriodMs:0.00}ms, mastered={Mastered})",
            _composition.Name, _canvasPeriod.TotalMilliseconds, _master is not null);
    }

    private void OnSlaveVideoTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        // Dispose() asks the driver thread to free GL resources on its own thread before we stop
        // the clock — SDL3GLVideoCompositor.DisposeOnOwnerThread is a no-op off-thread.
        if (Interlocked.CompareExchange(ref _driverDisposeState, 2, 1) == 1)
        {
            try { _gpuCompositor?.DisposeOnOwnerThread(); }
            catch (Exception ex) { Trace.LogWarning(ex, "CueCompositionRuntime.OnSlaveVideoTick: driver GL dispose"); }
            return;
        }
        if (_disposed) return;
        PumpOneFrame();
        CheckMasterDrift();
    }

    private long _lastDriftCheckTicks;
    private TimeSpan _lastMasterPosition;

    /// <summary>Sample the master's reported position vs. expected; count frames-behind and emit
    /// a drift warning when sustained lag accumulates. Cheap — just two TimeSpan reads per tick
    /// plus a Stopwatch comparison every ~5 s.</summary>
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

        // If master has advanced significantly less than wall-clock would suggest, the master
        // is paused or the audio device is back-pressuring — that's not a drift bug, skip.
        if (masterElapsed < TimeSpan.FromMilliseconds(50)) return;

        var diff = wallElapsed - masterElapsed;
        if (Math.Abs(diff.Ticks) > _canvasPeriod.Ticks * 2)
            Interlocked.Increment(ref _framesBehindMaster);

        _lastMasterPosition = masterPos;
        _lastDriftCheckTicks = Stopwatch.GetTimestamp();

        // Throttled warning — emit at most once per ~5 s on sustained drift.
        var behind = Volatile.Read(ref _framesBehindMaster);
        var since = behind - Volatile.Read(ref _lastBehindMasterReport);
        if (since >= 30)
        {
            Volatile.Write(ref _lastBehindMasterReport, behind);
            try
            {
                DriftWarning?.Invoke(this, new CueCompositionDriftWarning(
                    CompositionId,
                    _composition.Name,
                    behind,
                    wallElapsed - masterElapsed));
            }
            catch (Exception ex)
            {
                Trace.LogTrace(ex, "CueCompositionRuntime.CheckMasterDrift: DriftWarning handler threw");
            }
        }
    }

    /// <summary>Reserves a layer slot for a cue's video. The cue routes its decoder's frames into
    /// <see cref="LayerSlot.Output"/>; the compositor reads them on the next composed frame.</summary>
    public LayerSlot AddLayer(VideoFormat sourceFormat, CueVideoPlacement placement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rawSlot = _mixer.AddSlot();
        if (_master is not null)
            rawSlot.KeepPolicy = SlotKeepPolicy.MasterAligned;
        var layer = new LayerSlot(this, rawSlot, sourceFormat, placement, Interlocked.Increment(ref _nextLayerSequence));
        layer.ApplyPlacement();
        lock (_gate)
        {
            _slots.Add(layer);
            SortLayersLocked();
        }
        EnsurePumpStarted();
        return layer;
    }

    internal void RemoveLayer(LayerSlot layer)
    {
        bool empty;
        lock (_gate)
        {
            _slots.Remove(layer);
            _mixer.RemoveSlot(layer.RawSlot.Id);
            empty = _slots.Count == 0;
            if (!empty)
                SortLayersLocked();
        }
        // Caller (engine) decides whether to dispose the runtime when LayerCount hits zero.
        _ = empty;
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

    private bool _outputsConfigured;

    /// <summary>One pull-from-compositor + fan-out-to-outputs cycle. Always called from the
    /// single <see cref="MediaClock"/> driver thread that owns this runtime's GL context.</summary>
    private void PumpOneFrame()
    {
        var sw = Stopwatch.StartNew();
        TimeSpan? masterPts = null;
        if (_master is not null)
        {
            try { masterPts = _master.ElapsedSinceStart; }
            catch (Exception ex) { Trace.LogTrace(ex, "CueCompositionRuntime.PumpOneFrame: master read"); }
        }

        if (!_mixer.TryReadNextFrame(masterPts, out var frame))
            return;
        Interlocked.Increment(ref _framesComposited);

        List<AcquiredOutput> snapshot;
        lock (_gate) snapshot = _acquired.ToList();

        if (snapshot.Count == 0)
        {
            // No outputs yet — drop the frame so the buffer pool can recycle.
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
                    Trace.LogWarning(ex, "CueCompositionRuntime.Pump: initial Configure failed for {Line}",
                        output.Line.Definition.DisplayName);
                }
            }
            _outputsConfigured = true;
        }

        // IVideoOutput.Submit transfers ownership of the frame to the output (the output is
        // responsible for Dispose). Hand the original to the LAST output and clone for the rest.
        for (var i = 0; i < snapshot.Count; i++)
        {
            var output = snapshot[i];
            var isLast = i == snapshot.Count - 1;
            VideoFrame toSubmit;
            try
            {
                toSubmit = isLast ? frame : VideoFrameCpuClone.DuplicateCpuBacking(frame, frame.ColorTransferHint);
            }
            catch (Exception ex)
            {
                Trace.LogTrace(ex, "CueCompositionRuntime.Pump: clone failed for {Line}",
                    output.Line.Definition.DisplayName);
                if (isLast) frame.Dispose();
                continue;
            }

            try
            {
                output.Output.Submit(toSubmit);
                Interlocked.Increment(ref _framesSubmitted);
            }
            catch (Exception ex)
            {
                Trace.LogTrace(ex, "CueCompositionRuntime.Pump: Submit failed for {Line}",
                    output.Line.Definition.DisplayName);
                // Submit didn't take ownership; we still own the frame and must Dispose it.
                toSubmit.Dispose();
            }
        }

        sw.Stop();
        RecordPumpTiming(sw.Elapsed, _canvasPeriod);
    }

    private static (IVideoCompositor Compositor, SDL3GLVideoCompositor? Gpu, bool RequiresBgraLayerConversion, string BackendName)
        CreateCompositor(VideoFormat canvasFormat, CueComposition composition)
    {
        var requested = Environment.GetEnvironmentVariable("HAPLAY_CUE_COMPOSITOR");
        if (string.Equals(requested, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            Trace.LogInformation("CueCompositionRuntime: composition {Composition} using CPU compositor (env override)",
                composition.Name);
            return (new CpuVideoCompositor(canvasFormat), null, true, "CPU");
        }

        if (SDL3GLVideoCompositor.TryProbe(out var glError))
        {
            var gpu = new SDL3GLVideoCompositor(canvasFormat);
            Trace.LogInformation("CueCompositionRuntime: composition {Composition} using OpenGL compositor", composition.Name);
            return (gpu, gpu, false, "OpenGL");
        }

        var explicitGpu =
            string.Equals(requested, "gl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requested, "gpu", StringComparison.OrdinalIgnoreCase);
        if (explicitGpu)
        {
            Trace.LogWarning("CueCompositionRuntime: OpenGL compositor requested for {Composition} but unavailable: {Error}; falling back to CPU",
                composition.Name,
                glError);
        }
        else
        {
            Trace.LogInformation("CueCompositionRuntime: OpenGL compositor unavailable for {Composition}: {Error}; using CPU compositor",
                composition.Name,
                glError);
        }

        return (new CpuVideoCompositor(canvasFormat), null, true, "CPU");
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
                "CueCompositionRuntime: composition {Composition} pump over budget ({ElapsedMs:0.00}ms > {BudgetMs:0.00}ms, layers={Layers}, slotOverflow={Overflow})",
                _composition.Name,
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

    public void Dispose()
    {
        if (_disposed) return;

        // If the pump ever started, the GL compositor's owner thread is the MediaClock driver.
        // Ask that thread to dispose GL resources on its own thread before we stop the clock —
        // SDL3GLVideoCompositor.Dispose() called from any other thread is a no-op for the GL
        // teardown path. Skip when no GPU compositor exists or the pump never ran (uninitialized).
        if (_slaveClock is not null && _gpuCompositor is not null)
        {
            Interlocked.Exchange(ref _driverDisposeState, 1);
            var deadline = Environment.TickCount64 + 250;
            while (Volatile.Read(ref _driverDisposeState) != 2 && Environment.TickCount64 < deadline)
                Thread.Sleep(1);
        }

        _disposed = true;

        // Tear down the driver clock. MediaClock.Stop joins the driver thread, so once it returns
        // no more VideoTick callbacks are in flight.
        if (_slaveClock is { } sc)
        {
            try { sc.VideoTick -= OnSlaveVideoTick; } catch { /* best effort */ }
            try { sc.Stop(); } catch { /* best effort */ }
            try { sc.Dispose(); } catch { /* best effort */ }
            _slaveClock = null;
        }

        lock (_gate)
        {
            _slots.Clear();
            foreach (var acquired in _acquired)
            {
                // For NDI we wrapped the carrier in VideoOutputPump (and possibly
                // LockedFormatVideoOutput) — dispose the wrapper to stop its background thread
                // *before* releasing the carrier back to OutputManagement (else the pump may try
                // to Submit into a sender we've just handed back).
                if (acquired.Kind == AcquiredKind.Ndi && acquired.Output is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception ex) { Trace.LogWarning(ex, "CueCompositionRuntime.Dispose: NDI wrapper dispose"); }
                }

                try
                {
                    if (acquired.Kind == AcquiredKind.Local)
                        _outputs.ReleaseLocalVideoOutputForPlayback(acquired.Line);
                    else if (acquired.Kind == AcquiredKind.Ndi)
                        _outputs.ReleaseNDICarrierForPlayback(acquired.Line, releaseVideo: true, releaseAudio: false);
                }
                catch (Exception ex)
                {
                    Trace.LogWarning(ex, "CueCompositionRuntime.Dispose: release failed for {Line}",
                        acquired.Line.Definition.DisplayName);
                }
            }
            _acquired.Clear();
        }

        try { _mixer.Dispose(); }
        catch (Exception ex) { Trace.LogWarning(ex, "CueCompositionRuntime.Dispose: mixer dispose"); }
        try { _compositor.Dispose(); }
        catch (Exception ex) { Trace.LogWarning(ex, "CueCompositionRuntime.Dispose: compositor dispose"); }
    }

    private enum AcquiredKind { Local, Ndi }

    private sealed record AcquiredOutput(OutputLineViewModel Line, IVideoOutput Output, AcquiredKind Kind);

    /// <summary>Mirrors <c>HaPlayPlaybackSession.WrapWithNDILockIfNeeded</c>. When the carrier
    /// definition pins a pixel format or resolution, wrap the sender so frames get letterboxed /
    /// re-packed to those constraints before send.</summary>
    private static IVideoOutput WrapWithNDILockIfNeeded(IVideoOutput ndiSender, Models.NDIOutputDefinition nd, string name)
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

    public readonly record struct CueCompositionRuntimeStats(
        Guid CompositionId,
        long FramesComposited,
        long FramesSubmitted,
        long PumpOverruns,
        long SlotOverflowFrames,
        TimeSpan LastPumpFrameTime,
        TimeSpan MaxPumpFrameTime,
        long FramesBehindMaster,
        bool ClockMastered);

    /// <summary>Cue-facing handle: <see cref="Output"/> is what the cue's <c>MediaPlayer.VideoRouter</c>
    /// adds as a downstream output. Dispose to detach the cue's layer from the composition.</summary>
    internal sealed class LayerSlot : IDisposable
    {
        private readonly CueCompositionRuntime _owner;
        internal VideoCompositorSource.Slot RawSlot { get; }
        private readonly VideoFormat _source;
        private CueVideoPlacement _placement;

        public LayerSlot(CueCompositionRuntime owner, VideoCompositorSource.Slot slot, VideoFormat source, CueVideoPlacement placement, long sequence)
        {
            _owner = owner;
            RawSlot = slot;
            _source = source;
            _placement = placement;
            Sequence = sequence;
        }

        public IVideoOutput Output => RawSlot.Output;
        public int LayerIndex => _placement.LayerIndex;
        public long Sequence { get; }

        public void ApplyPlacement()
        {
            RawSlot.Transform = LayerConfigResolver.ToTransform(BuildConfig(_placement), _source, _owner._canvasFormat);
            RawSlot.Opacity = Math.Clamp((float)_placement.Opacity, 0f, 1f);
            RawSlot.BlendMode = BlendMode.SourceOver;
        }

        public void Dispose()
        {
            _owner.RemoveLayer(this);
        }

        private static LayerConfig BuildConfig(CueVideoPlacement p) =>
            new(MapPosition(p.Position), Scale: 1f, Opacity: (float)Math.Clamp(p.Opacity, 0.0, 1.0));

        private static LayerPosition MapPosition(CueLayerPosition pos) => pos switch
        {
            CueLayerPosition.Cover => LayerPosition.Cover,
            CueLayerPosition.Letterbox => LayerPosition.Center,
            CueLayerPosition.Center => LayerPosition.Center,
            CueLayerPosition.FillWidth => LayerPosition.Cover,
            CueLayerPosition.FillHeight => LayerPosition.Cover,
            _ => LayerPosition.Center,
        };
    }
}

/// <summary>Per-composition drift sample. Emitted at most every ~5 s on sustained drift.</summary>
internal readonly record struct CueCompositionDriftWarning(
    Guid CompositionId,
    string CompositionName,
    long FramesBehindMaster,
    TimeSpan LagFromMaster);

/// <summary>NDI output pump dropped frames — receiver/network can't keep up. Throttled to one
/// report per ~5 s per output, with <see cref="DroppedSinceLastReport"/> being the count over
/// the throttle window. (Phase 5.7.3.)</summary>
internal readonly record struct CueCompositionPumpPressureWarning(
    Guid CompositionId,
    string CompositionName,
    Guid OutputLineId,
    string OutputLineName,
    long DroppedSinceLastReport,
    long DroppedTotal);
