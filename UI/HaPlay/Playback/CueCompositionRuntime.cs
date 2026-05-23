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
    private readonly CancellationTokenSource _pumpCts = new();
    private Task? _pumpTask;
    private long _nextLayerSequence;
    private long _framesComposited;
    private long _framesSubmitted;
    private long _pumpOverruns;
    private long _lastPumpFrameTicks;
    private long _maxPumpFrameTicks;
    private long _framesBehindMaster;
    private long _lastBehindMasterReport;
    private IPlaybackClock? _master;
    private MediaClock? _slaveClock;
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
            _slaveClock is not null);
    }

    /// <summary>Raised once per ~5 s when the composition pump detects sustained drift between
    /// its own tick rate and the master clock. <see cref="MainViewModel"/> can subscribe and
    /// translate to a status message; for now it's diagnostic only.</summary>
    public event EventHandler<CueCompositionDriftWarning>? DriftWarning;

    /// <summary>Starts the pump that pulls composed frames at the canvas framerate and Submits
    /// to each acquired output. Idempotent. Picks the Stopwatch-driven path by default; the
    /// engine flips us to a clock-driven path via <see cref="SetClockMaster"/> after the first
    /// cue resolves its audio master, eliminating composition-vs-cue clock drift.</summary>
    public void EnsurePumpStarted()
    {
        lock (_gate)
        {
            if (_pumpTask is not null) return;
            // If a master was set BEFORE the first AddLayer call, prefer the clock-driven path
            // straight away. Otherwise start with the Stopwatch loop and let SetClockMaster
            // upgrade us when the engine calls it.
            if (_master is not null)
            {
                StartClockDrivenPumpLocked();
                return;
            }
            _pumpTask = Task.Factory.StartNew(
                () => PumpLoop(_pumpCts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    /// <summary>Slave the composition pump's cadence to a <see cref="IPlaybackClock"/> (typically
    /// the first cue's audio runtime clock). Removes the composition-vs-cue Stopwatch drift —
    /// composited frames present at the master's video-tick rate instead of free-running.
    /// Multiple calls are safe; only the first non-null master is taken so the master doesn't
    /// flip between concurrent cues.</summary>
    public void SetClockMaster(IPlaybackClock master)
    {
        ArgumentNullException.ThrowIfNull(master);
        lock (_gate)
        {
            if (_disposed) return;
            if (_master is not null) return; // first cue's master wins
            _master = master;

            // If the Stopwatch pump is already running, stop it cleanly so the clock-driven path
            // takes over. The lock guard avoids double-pump.
            if (_pumpTask is not null)
            {
                _pumpCts.Cancel();
                try { _pumpTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { /* best effort */ }
                _pumpTask = null;
            }

            // PumpCts has been cancelled — replace it so the clock-driven path has a fresh token.
            // (Field is readonly; reassign via reflection-free approach — just create a new
            // local CTS and let dispose clean up the old one. We accept the small leak.)
            StartClockDrivenPumpLocked();
        }
    }

    private void StartClockDrivenPumpLocked()
    {
        // Period derived from canvas framerate; the clock fires VideoTick at this interval.
        var num = Math.Max(1, _canvasFormat.FrameRate.Numerator);
        var den = Math.Max(1, _canvasFormat.FrameRate.Denominator);
        var period = TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond * (long)den / Math.Max(1L, num)));
        // Audio tick interval doesn't matter to us; pick a coarse one to keep the driver thread quiet.
        var audioInterval = TimeSpan.FromMilliseconds(50);
        _slaveClock = new MediaClock(audioInterval, period);
        if (_master is not null)
            _slaveClock.SetMaster(_master);
        _slaveClock.VideoTick += OnSlaveVideoTick;
        _slaveClock.Start();
        Trace.LogInformation(
            "CueCompositionRuntime: composition {Composition} pump now slaved to master clock (videoTick={PeriodMs:0.00}ms)",
            _composition.Name, period.TotalMilliseconds);
    }

    private void OnSlaveVideoTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
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

        var num = Math.Max(1, _canvasFormat.FrameRate.Numerator);
        var den = Math.Max(1, _canvasFormat.FrameRate.Denominator);
        var canvasPeriod = TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond * (long)den / Math.Max(1L, num)));

        var wallElapsed = Stopwatch.GetElapsedTime(_lastDriftCheckTicks);
        var masterElapsed = masterPos - _lastMasterPosition;

        // If master has advanced significantly less than wall-clock would suggest, the master
        // is paused or the audio device is back-pressuring — that's not a drift bug, skip.
        if (masterElapsed < TimeSpan.FromMilliseconds(50)) return;

        var diff = wallElapsed - masterElapsed;
        if (Math.Abs(diff.Ticks) > canvasPeriod.Ticks * 2)
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

    /// <summary>The Stopwatch-driven fallback pump for compositions that don't yet have an
    /// audio master (cues without audio routes, or master not yet resolved). The clock-driven
    /// path swaps in once <see cref="SetClockMaster"/> is called.</summary>
    private void PumpLoop(CancellationToken ct)
    {
        var num = _canvasFormat.FrameRate.Numerator;
        var den = Math.Max(1, _canvasFormat.FrameRate.Denominator);
        var fps = num <= 0 ? 60.0 : num / (double)den;
        var periodStopwatchTicks = Math.Max(1L, (long)(Stopwatch.Frequency / Math.Max(1.0, fps)));

        // Keep the composition loop on one dedicated thread. The GL compositor requires a stable
        // context-owner thread, and the CPU compositor also benefits from predictable cadence.
        var nextTick = Stopwatch.GetTimestamp();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                nextTick += periodStopwatchTicks;
                var now = Stopwatch.GetTimestamp();
                var delayTicks = nextTick - now;
                if (delayTicks > 0)
                {
                    var delay = TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency);
                    if (ct.WaitHandle.WaitOne(delay))
                        break;
                }
                else if (-delayTicks > periodStopwatchTicks * 4)
                {
                    nextTick = now;
                }

                PumpOneFrame();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CueCompositionRuntime.Pump: loop crashed");
        }
        finally
        {
            _gpuCompositor?.DisposeOnOwnerThread();
        }
    }

    /// <summary>One pull-from-compositor + fan-out-to-outputs cycle. Shared between the
    /// Stopwatch-driven pump (no master) and the <see cref="MediaClock"/>-driven path (master
    /// set) so frame ownership / clone semantics stay identical.</summary>
    private void PumpOneFrame()
    {
        var num = _canvasFormat.FrameRate.Numerator;
        var den = Math.Max(1, _canvasFormat.FrameRate.Denominator);
        var fps = num <= 0 ? 60.0 : num / (double)den;
        var period = TimeSpan.FromTicks((long)(TimeSpan.TicksPerSecond / Math.Max(1.0, fps)));

        var sw = Stopwatch.StartNew();
        if (!_mixer.TryReadNextFrame(out var frame))
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
        RecordPumpTiming(sw.Elapsed, period);
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
        _disposed = true;

        try { _pumpCts.Cancel(); } catch { /* best effort */ }
        try { _pumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        try { _pumpCts.Dispose(); } catch { /* best effort */ }

        // Tear down the master-slaved MediaClock if SetClockMaster swapped it in.
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
