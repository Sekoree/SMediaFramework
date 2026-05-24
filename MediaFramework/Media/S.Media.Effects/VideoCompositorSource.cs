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
            _slots.Sort(comparison);
    }

    /// <summary>Removes a slot and disposes any frame it was holding.</summary>
    public bool RemoveSlot(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Slot? toDispose = null;
        lock (_slotsGate)
        {
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

    /// <param name="masterAlignmentTime">When set, slots with
    /// <see cref="Slot.KeepPolicy"/> = <see cref="SlotKeepPolicy.MasterAligned"/> pick the
    /// frame whose PTS is closest to this position.</param>
    public bool TryReadNextFrame(TimeSpan? masterAlignmentTime, out VideoFrame frame)
    {
        if (_disposed)
        {
            frame = null!;
            return false;
        }

        // Snapshot slots and lease each held frame for the duration of this composite.
        Slot[] snapshot;
        lock (_slotsGate)
            snapshot = _slots.ToArray();

        List<CompositorLayer>? layers = null;
        List<Slot.SlotFrameLease>? leases = null;
        try
        {
            foreach (var slot in snapshot)
            {
                var lease = slot.KeepPolicy == SlotKeepPolicy.MasterAligned && masterAlignmentTime is { } masterPts
                    ? slot.AcquireMasterAligned(masterPts, _ptsStep)
                    : slot.AcquireLatest();
                if (lease is null) continue;
                leases ??= [];
                leases.Add(lease);
                var f = lease.Frame;
                layers ??= [];
                layers.Add(new CompositorLayer(f, slot.Transform, slot.Opacity, slot.BlendMode));
            }

            var pts = _nextPts;
            _nextPts += _ptsStep;
            frame = _compositor.Composite(layers ?? (IReadOnlyList<CompositorLayer>)Array.Empty<CompositorLayer>(), pts);
            Interlocked.Increment(ref _compositesEmitted);
            return true;
        }
        finally
        {
            if (leases is not null)
            {
                foreach (var lease in leases)
                    lease.Dispose();
            }
        }
    }

    public void Dispose()
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
        private long _overflowFrames;
        private int _activeReaders;
        private float _opacity = 1f;
        private LayerTransform2D _transform = LayerTransform2D.Identity;
        private BlendMode _blendMode = BlendMode.SourceOver;
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

        /// <summary>Which submitted frame is exposed at composite time. Default
        /// <see cref="SlotKeepPolicy.Latest"/>.</summary>
        public SlotKeepPolicy KeepPolicy { get; set; } = SlotKeepPolicy.Latest;

        /// <summary>Frames replaced before the compositor could read them.</summary>
        public long OverflowFrames => Volatile.Read(ref _overflowFrames);

        internal void SubmitFromOutput(VideoFrame frame)
        {
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
            if (closed)
                throw new ObjectDisposedException(nameof(Slot));
        }

        internal SlotFrameLease? AcquireLatest()
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
            return new SlotFrameLease(this, frame);
        }

        /// <summary>Picks the held frame whose PTS is closest to <paramref name="masterPts"/> without
        /// selecting a frame that is more than one canvas period in the future.</summary>
        internal SlotFrameLease? AcquireMasterAligned(TimeSpan masterPts, TimeSpan canvasPeriod)
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
            return new SlotFrameLease(this, frame);
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
                }
            }
            pendingToDispose?.Dispose();
            currentToDispose?.Dispose();
        }

        private void ReleaseLease()
        {
            VideoFrame? toDispose = null;
            lock (_gate)
            {
                if (_activeReaders > 0)
                    _activeReaders--;
                if (_closed && _activeReaders == 0)
                {
                    toDispose = _current;
                    _current = null;
                }
            }
            toDispose?.Dispose();
        }

        internal sealed class SlotFrameLease : IDisposable
        {
            private Slot? _owner;

            public SlotFrameLease(Slot owner, VideoFrame frame)
            {
                _owner = owner;
                Frame = frame;
            }

            public VideoFrame Frame { get; }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseLease();
            }
        }

        private sealed class SlotOutput(Slot owner, IReadOnlyList<PixelFormat> accepted) : IVideoOutput
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

            private static bool ContainsFormat(IReadOnlyList<PixelFormat> list, PixelFormat pf)
            {
                for (var i = 0; i < list.Count; i++)
                    if (list[i] == pf) return true;
                return false;
            }
        }
    }
}
