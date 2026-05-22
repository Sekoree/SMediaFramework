namespace S.Media.Effects;

/// <summary>
/// Combines N video inputs into one output stream via an <see cref="IVideoCompositor"/>.
/// Each input is an <see cref="IVideoOutput"/> slot the upstream router targets; the single output
/// is an <see cref="IVideoSource"/> a downstream player or router pulls from.
/// </summary>
/// <remarks>
/// <para>
/// Same source/output-duality pattern as <see cref="Audio.AudioBus"/>, but **per slot**: each slot
/// holds only the most-recent submitted frame (replace-on-submit). When the downstream consumer
/// calls <see cref="TryReadNextFrame"/>, the output snapshots every slot's current frame, builds a
/// <see cref="CompositorLayer"/> list (slot-insertion order = back-to-front), calls
/// <see cref="IVideoCompositor.Composite"/>, and returns the composed frame.
/// </para>
/// <para>
/// <strong>Latest-wins</strong> semantics: if a slot receives a new frame before the previous one
/// has been composited, the old frame is disposed and <see cref="Slot.OverflowFrames"/> increments.
/// This matches <see cref="VideoOutputPump"/>'s drop-oldest behavior under pressure but at the slot
/// level rather than a queue.
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

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        if (_disposed)
        {
            frame = null!;
            return false;
        }

        // Snapshot slots + take ownership of each slot's current frame.
        Slot[] snapshot;
        lock (_slotsGate)
            snapshot = _slots.ToArray();

        List<CompositorLayer>? layers = null;
        // Frames we took ownership of from slots; must be disposed after Composite returns.
        List<VideoFrame>? consumedFrames = null;
        try
        {
            foreach (var slot in snapshot)
            {
                var f = slot.TakeLatest();
                if (f is null) continue;
                consumedFrames ??= [];
                consumedFrames.Add(f);
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
            if (consumedFrames is not null)
            {
                foreach (var f in consumedFrames)
                    f.Dispose();
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
        private VideoFrame? _latest;
        private long _overflowFrames;
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

        /// <summary>Frames replaced before the compositor could read them.</summary>
        public long OverflowFrames => Volatile.Read(ref _overflowFrames);

        internal void SubmitFromOutput(VideoFrame frame)
        {
            VideoFrame? toDispose;
            lock (_gate)
            {
                if (_closed)
                {
                    frame.Dispose();
                    throw new ObjectDisposedException(nameof(Slot));
                }
                toDispose = _latest;
                _latest = frame;
            }
            if (toDispose is not null)
            {
                Interlocked.Increment(ref _overflowFrames);
                toDispose.Dispose();
            }
        }

        internal VideoFrame? TakeLatest()
        {
            lock (_gate)
            {
                var f = _latest;
                _latest = null;
                return f;
            }
        }

        internal void Close()
        {
            VideoFrame? toDispose;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                toDispose = _latest;
                _latest = null;
            }
            toDispose?.Dispose();
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
