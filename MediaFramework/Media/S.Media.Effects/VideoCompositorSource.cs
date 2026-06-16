using S.Media.Core.Video;

namespace S.Media.Effects;

/// <summary>
/// Combines N video inputs into one output stream via an <see cref="IVideoCompositor"/>.
/// Each input is an <see cref="IVideoOutput"/> slot the upstream router targets; the single output
/// is an <see cref="IVideoSource"/> a downstream player or router pulls from.
/// </summary>
/// <remarks>
/// <para>
/// Same source/output-duality pattern as <see cref="Audio.AudioBus"/>, but **per slot**: each slot
/// holds the most-recent frame that has been promoted into the composition and keeps reusing it
/// until a newer submitted frame replaces it. When the downstream consumer calls
/// <see cref="TryReadNextFrame"/>, the output snapshots every slot's current frame, builds a
/// <see cref="CompositorLayer"/> list (slot order = back-to-front), calls
/// <see cref="IVideoCompositor.Composite"/>, and returns the composed frame.
/// </para>
/// <para>
/// <strong>Latest-wins</strong> semantics: if a slot receives multiple new frames before the next
/// composite, only the newest pending frame is promoted; superseded pending frames are disposed and
/// <see cref="Slot.OverflowFrames"/> increments. This matches <see cref="VideoOutputPump"/>'s
/// drop-oldest behavior under pressure but at the slot level rather than a queue.
/// </para>
/// <para>
/// <strong>Per-slot mutable state</strong>: <see cref="Slot.Opacity"/>, <see cref="Slot.Transform"/>,
/// and <see cref="Slot.BlendMode"/> are read each Composite call so a higher-level animator can
/// drive transitions by setting them from a timeline (see
/// <see cref="LayerOpacityTween"/>).
/// </para>
/// <para>
/// <strong>Threading</strong>: <see cref="IVideoOutput.Submit"/> on a slot may run on any thread
/// (typically the upstream player's clock thread). <see cref="TryReadNextFrame"/> runs on the
/// downstream consumer's thread. For a <c>GlVideoCompositor</c>, the downstream consumer's thread
/// must be the GL context owner.
/// </para>
/// </remarks>
public sealed class VideoCompositorSource : IVideoSource, IDisposable
{
    private readonly IVideoCompositor _compositor;
    private readonly bool _disposeCompositorOnDispose;
    private readonly VideoFormat _output;
    private readonly PixelFormat[] _native;
    private readonly TimeSpan _ptsStep;
    private readonly Lock _slotsGate = new();
    private readonly List<Slot> _slots = [];
    // Reusable scratch for the single-consumer read path (serialized by _readGate) so a steady-state
    // composite allocates nothing: the slot snapshot, the per-slot composite layers, and the slots to
    // release once Composite has read them.
    private readonly Lock _readGate = new();
    private readonly List<Slot> _snapshotScratch = [];
    private readonly List<CompositorLayer> _layerScratch = [];
    private readonly List<Slot> _acquiredScratch = [];
    private TimeSpan _nextPts = TimeSpan.Zero;
    private long _compositesEmitted;
    private bool _disposed;

    /// <param name="output">Output format. Pixel format must be one the compositor accepts on its **output** — both shipping compositors output BGRA32.</param>
    /// <param name="compositor">The compositor that does the actual blending. Lifecycle: owned by the output when <paramref name="disposeCompositorOnDispose"/> is <c>true</c>.</param>
    /// <param name="disposeCompositorOnDispose">When <c>true</c> (default), <see cref="Dispose"/> also disposes the compositor.</param>
    public VideoCompositorSource(VideoFormat output, IVideoCompositor compositor, bool disposeCompositorOnDispose = true)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        if (compositor.OutputFormat != output)
            compositor.Configure(output);

        _compositor = compositor;
        _disposeCompositorOnDispose = disposeCompositorOnDispose;
        _output = output;
        _native = [output.PixelFormat];
        _ptsStep = DerivePeriod(output.FrameRate);
    }

    public VideoFormat Format => _output;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;
    public bool IsExhausted => _disposed;

    /// <summary>Cumulative number of composite frames emitted from <see cref="TryReadNextFrame"/>.</summary>
    public long CompositesEmitted => Volatile.Read(ref _compositesEmitted);

    /// <summary>Snapshot of all slots in insertion order (back-to-front for the compositor).</summary>
    public IReadOnlyList<Slot> Slots
    {
        get
        {
            lock (_slotsGate)
                return _slots.ToArray();
        }
    }

    /// <summary>
    /// Adds an input slot. The returned <see cref="Slot"/> exposes the <see cref="IVideoOutput"/>
    /// upstream code should target (<see cref="Slot.Output"/>) plus mutable per-slot
    /// <see cref="Slot.Opacity"/> / <see cref="Slot.Transform"/> / <see cref="Slot.BlendMode"/>.
    /// </summary>
    /// <param name="id">Optional stable id for diagnostics; defaults to <c>slot_1</c>, <c>slot_2</c>, …</param>
    /// <param name="acceptedFormats">Override the slot's <see cref="IVideoOutput.AcceptedPixelFormats"/>. Defaults to the compositor's <see cref="IVideoCompositor.AcceptedLayerPixelFormats"/>.</param>
    public Slot AddSlot(string? id = null, IReadOnlyList<PixelFormat>? acceptedFormats = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_slotsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var slotId = id ?? $"slot_{_slots.Count + 1}";
            foreach (var s in _slots)
            {
                if (s.Id == slotId)
                    throw new ArgumentException($"slot id '{slotId}' is already registered", nameof(id));
            }
            var slot = new Slot(slotId, acceptedFormats ?? _compositor.AcceptedLayerPixelFormats);
            _slots.Add(slot);
            return slot;
        }
    }

    /// <summary>
    /// Reorders slots in-place. The list order is the compositor's back-to-front draw order.
    /// </summary>
    public void SortSlots(Comparison<Slot> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_slotsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _slots.Sort(comparison);
        }
    }

    /// <summary>Removes a slot and disposes any frame it was holding.</summary>
    public bool RemoveSlot(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Slot? toDispose = null;
        lock (_slotsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Id != id) continue;
                toDispose = _slots[i];
                _slots.RemoveAt(i);
                break;
            }
        }

        if (toDispose is null) return false;
        toDispose.Close();
        return true;
    }

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != _output.PixelFormat)
            throw new InvalidOperationException(
                $"VideoCompositorSource only delivers {_output.PixelFormat}; consumer requested {format}.");
    }

    public bool TryReadNextFrame(out VideoFrame frame) =>
        TryReadNextFrame(masterAlignmentTime: null, out frame);

    public bool TryReadNextFrames(
        TimeSpan? masterAlignmentTime,
        IReadOnlyList<WarpOutputRequest> outputs,
        out IReadOnlyList<VideoFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (_compositor is not IWarpPassVideoCompositor warpCompositor)
            throw new InvalidOperationException("The configured compositor does not support multi-output warp composition.");

        // Single-consumer read (class contract): serialize so the reused scratch below is exclusive.
        lock (_readGate)
        {
            if (_disposed)
            {
                frames = Array.Empty<VideoFrame>();
                return false;
            }

            // Snapshot slot refs under _slotsGate (brief — keeps AddSlot/RemoveSlot from contending
            // with the whole composite), then acquire each slot's held frame outside it. AddRange from
            // an ICollection copies without per-element/enumerator alloc once the scratch is warm.
            _snapshotScratch.Clear();
            lock (_slotsGate)
                _snapshotScratch.AddRange(_slots);

            _layerScratch.Clear();
            _acquiredScratch.Clear();
            try
            {
                foreach (var slot in _snapshotScratch)
                {
                    // Acquire holds a read-ref on the slot's frame (the slot won't dispose it until we
                    // ReleaseFrame below). Track the slot, not a per-call lease object, to release it.
                    var f = slot.KeepPolicy == SlotKeepPolicy.MasterAligned && masterAlignmentTime is { } masterPts
                        ? slot.AcquireMasterAlignedFrame(masterPts, _ptsStep)
                        : slot.AcquireLatestFrame();
                    if (f is null) continue;
                    _acquiredScratch.Add(slot);
                    _layerScratch.Add(new CompositorLayer(f, slot.Transform, slot.Opacity, slot.BlendMode)
                    {
                        SourceCrop = slot.SourceCrop,
                    });
                }

                // When the caller drives composition from a master timeline (the declarative
                // VideoCompositor), stamp the composite with that master time so downstream players
                // align to a real clock rather than a synthetic free-running counter. For read-paced
                // scaler/preset paths (OutputPresetVideoSource), propagate the background layer's
                // source PTS so shared-demux seeks stay aligned with audio.
                TimeSpan pts;
                if (masterAlignmentTime is { } master)
                {
                    pts = master;
                }
                else if (_layerScratch.Count > 0)
                {
                    pts = _layerScratch[0].Frame.PresentationTime;
                    _nextPts = pts + _ptsStep;
                }
                else
                {
                    pts = _nextPts;
                    _nextPts += _ptsStep;
                }

                frames = warpCompositor.CompositeMulti(_layerScratch, outputs, pts);
                Interlocked.Increment(ref _compositesEmitted);
                return true;
            }
            finally
            {
                foreach (var slot in _acquiredScratch)
                    slot.ReleaseFrame();
                _acquiredScratch.Clear();
                _layerScratch.Clear();
                _snapshotScratch.Clear();
            }
        }
    }

    /// <param name="masterAlignmentTime">When set, slots with
    /// <see cref="Slot.KeepPolicy"/> = <see cref="SlotKeepPolicy.MasterAligned"/> pick the
    /// frame whose PTS is closest to this position.</param>
    public bool TryReadNextFrame(TimeSpan? masterAlignmentTime, out VideoFrame frame)
    {
        // Single-consumer read (class contract): serialize so the reused scratch below is exclusive.
        lock (_readGate)
        {
            if (_disposed)
            {
                frame = null!;
                return false;
            }

            // Snapshot slot refs under _slotsGate (brief — keeps AddSlot/RemoveSlot from contending
            // with the whole composite), then acquire each slot's held frame outside it. AddRange from
            // an ICollection copies without per-element/enumerator alloc once the scratch is warm.
            _snapshotScratch.Clear();
            lock (_slotsGate)
                _snapshotScratch.AddRange(_slots);

            _layerScratch.Clear();
            _acquiredScratch.Clear();
            try
            {
                foreach (var slot in _snapshotScratch)
                {
                    // Acquire holds a read-ref on the slot's frame (the slot won't dispose it until we
                    // ReleaseFrame below). Track the slot, not a per-call lease object, to release it.
                    var f = slot.KeepPolicy == SlotKeepPolicy.MasterAligned && masterAlignmentTime is { } masterPts
                        ? slot.AcquireMasterAlignedFrame(masterPts, _ptsStep)
                        : slot.AcquireLatestFrame();
                    if (f is null) continue;
                    _acquiredScratch.Add(slot);
                    _layerScratch.Add(new CompositorLayer(f, slot.Transform, slot.Opacity, slot.BlendMode)
                    {
                        SourceCrop = slot.SourceCrop,
                    });
                }

                // When the caller drives composition from a master timeline (the declarative
                // VideoCompositor), stamp the composite with that master time so downstream players
                // align to a real clock rather than a synthetic free-running counter. For read-paced
                // scaler/preset paths (OutputPresetVideoSource), propagate the background layer's
                // source PTS so shared-demux seeks stay aligned with audio.
                TimeSpan pts;
                if (masterAlignmentTime is { } master)
                {
                    pts = master;
                }
                else if (_layerScratch.Count > 0)
                {
                    pts = _layerScratch[0].Frame.PresentationTime;
                    _nextPts = pts + _ptsStep;
                }
                else
                {
                    pts = _nextPts;
                    _nextPts += _ptsStep;
                }

                frame = _compositor.Composite(_layerScratch, pts);
                Interlocked.Increment(ref _compositesEmitted);
                return true;
            }
            finally
            {
                foreach (var slot in _acquiredScratch)
                    slot.ReleaseFrame();
                _acquiredScratch.Clear();
                _layerScratch.Clear();
                _snapshotScratch.Clear();
            }
        }
    }

    public void Dispose()
    {
        lock (_readGate)
        {
            if (_disposed) return;
            _disposed = true;
            Slot[] toClose;
            lock (_slotsGate)
            {
                toClose = _slots.ToArray();
                _slots.Clear();
            }
            foreach (var s in toClose)
                s.Close();
            if (_disposeCompositorOnDispose)
                _compositor.Dispose();
        }
    }

    private static TimeSpan DerivePeriod(Rational frameRate)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            return TimeSpan.FromMilliseconds(33);
        return TimeSpan.FromSeconds((double)frameRate.Denominator / frameRate.Numerator);
    }

    /// <summary>One input slot — combines an <see cref="IVideoOutput"/> target with mutable composite parameters.</summary>
    public sealed class Slot
    {
        private readonly Lock _gate = new();
        private readonly SlotOutput _sink;
        private VideoFrame? _current;
        private VideoFrame? _pending;
        private VideoFrame? _abandonedCurrent;
        private long _overflowFrames;
        private int _activeReaders;
        private float _opacity = 1f;
        private LayerTransform2D _transform = LayerTransform2D.Identity;
        private BlendMode _blendMode = BlendMode.SourceOver;
        private RectNormalized _sourceCrop = RectNormalized.Full;
        private bool _closed;

        internal Slot(string id, IReadOnlyList<PixelFormat> accepted)
        {
            Id = id;
            _sink = new SlotOutput(this, accepted);
        }

        /// <summary>Stable id for diagnostics.</summary>
        public string Id { get; }

        /// <summary>The <see cref="IVideoOutput"/> upstream code targets.</summary>
        public IVideoOutput Output => _sink;

        /// <summary>Per-layer alpha multiplier in [0, 1]. Animator-friendly; read on every Composite.</summary>
        public float Opacity
        {
            get { lock (_gate) return _opacity; }
            set { lock (_gate) _opacity = value; }
        }

        /// <summary>Source-to-destination affine. Animator-friendly; read on every Composite.</summary>
        public LayerTransform2D Transform
        {
            get { lock (_gate) return _transform; }
            set { lock (_gate) _transform = value; }
        }

        /// <summary>Blend mode applied when this slot's frame is drawn.</summary>
        public BlendMode BlendMode
        {
            get { lock (_gate) return _blendMode; }
            set { lock (_gate) _blendMode = value; }
        }

        /// <summary>Normalized source crop applied before compositing. Animator-friendly; read on every Composite.</summary>
        public RectNormalized SourceCrop
        {
            get { lock (_gate) return _sourceCrop; }
            set { lock (_gate) _sourceCrop = value; }
        }

        /// <summary>Which submitted frame is exposed at composite time. Default
        /// <see cref="SlotKeepPolicy.Latest"/>.</summary>
        public SlotKeepPolicy KeepPolicy { get; set; } = SlotKeepPolicy.Latest;

        /// <summary>Frames replaced before the compositor could read them.</summary>
        public long OverflowFrames => Volatile.Read(ref _overflowFrames);

        internal void SubmitFromOutput(VideoFrame frame)
        {
            // A closed slot drops silently (frame disposed, ownership honored): a player tick is
            // routinely in flight while a cue's slots are torn down, and throwing here only
            // produced per-tick error spam upstream — no submitter can react to it anyway.
            VideoFrame? toDispose;
            var closed = false;
            lock (_gate)
            {
                if (_closed)
                {
                    closed = true;
                    toDispose = frame;
                }
                else
                {
                    toDispose = _pending;
                    _pending = frame;
                }
            }
            if (toDispose is not null)
            {
                if (!closed)
                    Interlocked.Increment(ref _overflowFrames);
                toDispose.Dispose();
            }
        }

        /// <summary>Returns the slot's latest held frame and registers an active read-ref on it (the
        /// slot won't dispose it until <see cref="ReleaseFrame"/>). Returns null (no ref taken) when the
        /// slot is closed or empty.</summary>
        internal VideoFrame? AcquireLatestFrame()
        {
            VideoFrame? toDispose = null;
            VideoFrame? frame;
            lock (_gate)
            {
                if (_closed)
                    return null;

                if (_pending is not null && _activeReaders == 0)
                {
                    toDispose = _current;
                    _current = _pending;
                    _pending = null;
                }

                frame = _current;
                if (frame is null)
                    return null;
                _activeReaders++;
            }

            toDispose?.Dispose();
            return frame;
        }

        /// <summary>Picks the held frame whose PTS is closest to <paramref name="masterPts"/> without
        /// selecting a frame that is more than one canvas period in the future. Registers an active
        /// read-ref (release via <see cref="ReleaseFrame"/>) when it returns non-null.</summary>
        internal VideoFrame? AcquireMasterAlignedFrame(TimeSpan masterPts, TimeSpan canvasPeriod)
        {
            VideoFrame? toDispose = null;
            VideoFrame? frame;
            lock (_gate)
            {
                if (_closed)
                    return null;

                frame = ChooseMasterAlignedFrame(masterPts, canvasPeriod, ref toDispose);
                if (frame is null)
                    return null;
                _activeReaders++;
            }

            toDispose?.Dispose();
            return frame;
        }

        internal void AbandonQueuedFrames()
        {
            VideoFrame? pendingToDispose;
            VideoFrame? currentToDispose = null;
            lock (_gate)
            {
                pendingToDispose = _pending;
                _pending = null;
                if (_activeReaders == 0)
                {
                    currentToDispose = _current;
                    _current = null;
                }
                else if (_current is not null)
                {
                    _abandonedCurrent = _current;
                    _current = null;
                }
            }

            pendingToDispose?.Dispose();
            currentToDispose?.Dispose();
        }

        internal bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_pending is null && _activeReaders == 0)
                        return true;
                }

                if (Environment.TickCount64 >= deadline)
                    return false;

                Thread.Sleep(1);
            }
        }

        private VideoFrame? ChooseMasterAlignedFrame(TimeSpan masterPts, TimeSpan canvasPeriod, ref VideoFrame? toDispose)
        {
            var maxFuture = masterPts + canvasPeriod;
            VideoFrame? best = null;
            var bestDistance = long.MaxValue;

            void Consider(VideoFrame? candidate)
            {
                if (candidate is null) return;
                if (candidate.PresentationTime > maxFuture) return;
                var distance = Math.Abs((candidate.PresentationTime - masterPts).Ticks);
                if (distance >= bestDistance) return;
                bestDistance = distance;
                best = candidate;
            }

            Consider(_current);
            Consider(_pending);

            if (best is null)
                best = _current;

            if (best is null)
                return null;

            if (!ReferenceEquals(best, _current) && ReferenceEquals(best, _pending))
            {
                if (_activeReaders == 0)
                {
                    // Safe to promote: no other lease holds _current, so we can dispose it and
                    // hand the lease a reference that is no longer reachable from SubmitFromOutput.
                    toDispose = _current;
                    _current = _pending;
                    _pending = null;
                }
                else
                {
                    // A concurrent reader still holds _current. Returning _pending would be
                    // unsafe — a subsequent SubmitFromOutput would dispose _pending while the
                    // new lease is still using it. Fall back to _current (PTS-wise <= _pending,
                    // so older but never too-future). If _current is null we skip this tick.
                    best = _current;
                    if (best is null)
                        return null;
                }
            }

            return best;
        }

        internal void Close()
        {
            VideoFrame? currentToDispose = null;
            VideoFrame? abandonedToDispose = null;
            VideoFrame? pendingToDispose;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                pendingToDispose = _pending;
                _pending = null;
                if (_activeReaders == 0)
                {
                    currentToDispose = _current;
                    _current = null;
                    abandonedToDispose = _abandonedCurrent;
                    _abandonedCurrent = null;
                }
            }
            pendingToDispose?.Dispose();
            currentToDispose?.Dispose();
            abandonedToDispose?.Dispose();
        }

        /// <summary>Releases one read-ref taken by <see cref="AcquireLatestFrame"/> /
        /// <see cref="AcquireMasterAlignedFrame"/>. Must be called exactly once per non-null acquire.</summary>
        internal void ReleaseFrame()
        {
            VideoFrame? toDispose = null;
            VideoFrame? abandonedToDispose = null;
            lock (_gate)
            {
                if (_activeReaders > 0)
                    _activeReaders--;
                if (_activeReaders == 0)
                {
                    abandonedToDispose = _abandonedCurrent;
                    _abandonedCurrent = null;
                    if (_closed)
                    {
                        toDispose = _current;
                        _current = null;
                    }
                }
            }
            toDispose?.Dispose();
            abandonedToDispose?.Dispose();
        }

        private sealed class SlotOutput(Slot owner, IReadOnlyList<PixelFormat> accepted) : IVideoOutput, IVideoOutputQueueControl
        {
            private VideoFormat _format;
            public VideoFormat Format => _format;
            public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = accepted;

            public void Configure(VideoFormat format)
            {
                var ap = AcceptedPixelFormats;
                if (ap.Count > 0 && !ContainsFormat(ap, format.PixelFormat))
                    throw new ArgumentException(
                        $"slot '{owner.Id}' does not accept pixel format {format.PixelFormat}; " +
                        $"accepted: {string.Join(", ", ap)}",
                        nameof(format));
                _format = format;
            }

            public void Submit(VideoFrame frame)
            {
                ArgumentNullException.ThrowIfNull(frame);
                owner.SubmitFromOutput(frame);
            }

            public void AbandonQueuedFrames() => owner.AbandonQueuedFrames();

            public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default) =>
                owner.WaitForIdle(timeout, cancellationToken);

            private static bool ContainsFormat(IReadOnlyList<PixelFormat> list, PixelFormat pf)
            {
                for (var i = 0; i < list.Count; i++)
                    if (list[i] == pf) return true;
                return false;
            }
        }
    }
}
