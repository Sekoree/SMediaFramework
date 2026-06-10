using S.Media.Core.Audio;

namespace HaPlay.Playback;

internal sealed class PausableAudioSource : IAudioSource, ICooperativeAudioReadInterrupt, IDisposable
{
    private readonly IAudioSource _inner;
    private readonly bool _disposeInner;
    private int _paused;
    private bool _disposed;

    public PausableAudioSource(IAudioSource inner, bool disposeInner = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _disposeInner = disposeInner;
    }

    public AudioFormat Format => _inner.Format;

    public bool IsPaused
    {
        get => Volatile.Read(ref _paused) != 0;
        set => Volatile.Write(ref _paused, value ? 1 : 0);
    }

    // A disposed source is "fully done" (exhausted), not "stalled live" — the router treats exhausted
    // sources as removable; reporting !exhausted after dispose would make it look stuck instead.
    public bool IsExhausted => _disposed || (!IsPaused && _inner.IsExhausted);

    // Forward cooperative interruption so AudioRouter.Pause/Dispose can abort a blocking decoder read
    // through the cue path (the demux AudioTrack implements this); without the forward, cue stops wait
    // out the full read.
    public void RequestYieldBetweenReads() => (_inner as ICooperativeAudioReadInterrupt)?.RequestYieldBetweenReads();

    public void ClearYieldRequest() => (_inner as ICooperativeAudioReadInterrupt)?.ClearYieldRequest();

    public bool TryReadNextFrame(out AudioFrame frame)
    {
        if (_disposed || IsPaused)
        {
            frame = default;
            return false;
        }
        return _inner.TryReadNextFrame(out frame);
    }

    public int ReadInto(Span<float> destination)
    {
        if (_disposed || IsPaused)
            return 0;
        return _inner.ReadInto(destination);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_disposeInner && _inner is IDisposable d)
            d.Dispose();
    }
}

internal sealed class AudioSourceFanout : IDisposable
{
    private readonly IAudioSource _inner;
    private readonly object _gate = new();
    private readonly List<Branch> _branches = new();
    private readonly int _maxBufferedFloats;
    private readonly float[] _pullBuffer;
    private float[] _buffer = [];
    private long _baseFloatIndex;
    private int _count;
    private bool _disposed;

    public AudioSourceFanout(IAudioSource inner, int maxBufferedSeconds = 5)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        var fmt = inner.Format;
        fmt.Validate(nameof(inner));
        var seconds = Math.Clamp(maxBufferedSeconds, 1, 60);
        _maxBufferedFloats = checked(fmt.SampleRate * fmt.Channels * seconds);
        _pullBuffer = new float[Math.Max(fmt.Channels * 1024, fmt.Channels)];
    }

    public IAudioSource CreateBranch()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var branch = new Branch(this, _baseFloatIndex);
            _branches.Add(branch);
            return branch;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _branches.Clear();
            _buffer = [];
            _count = 0;
            _baseFloatIndex = 0;
        }
    }

    private int ReadBranch(Branch branch, Span<float> destination)
    {
        if (destination.Length == 0)
            return 0;

        lock (_gate)
        {
            if (_disposed || branch.Disposed)
                return 0;

            if (branch.Position < _baseFloatIndex)
                branch.Position = _baseFloatIndex;

            EnsureAvailableLocked(branch.Position + destination.Length);
            if (branch.Position < _baseFloatIndex)
                branch.Position = _baseFloatIndex;

            var writtenUntil = _baseFloatIndex + _count;
            var available = (int)Math.Min(destination.Length, writtenUntil - branch.Position);
            if (available <= 0)
                return 0;

            var offset = checked((int)(branch.Position - _baseFloatIndex));
            _buffer.AsSpan(offset, available).CopyTo(destination);
            branch.Position += available;
            TrimLocked();
            return available;
        }
    }

    private bool IsBranchExhausted(Branch branch)
    {
        // A disposed fanout/branch is "fully done", not "stalled live" — see PausableAudioSource.IsExhausted.
        lock (_gate)
            return _disposed || branch.Disposed || (_inner.IsExhausted && branch.Position >= _baseFloatIndex + _count);
    }

    private void RemoveBranch(Branch branch)
    {
        lock (_gate)
        {
            branch.Disposed = true;
            _branches.Remove(branch);
            TrimLocked();
        }
    }

    private void EnsureAvailableLocked(long targetExclusive)
    {
        while (!_inner.IsExhausted && _baseFloatIndex + _count < targetExclusive)
        {
            var read = _inner.ReadInto(_pullBuffer);
            if (read <= 0)
                break;
            AppendLocked(_pullBuffer.AsSpan(0, read));
        }
    }

    private void AppendLocked(ReadOnlySpan<float> samples)
    {
        EnsureCapacity(_count + samples.Length);
        samples.CopyTo(_buffer.AsSpan(_count));
        _count += samples.Length;
        TrimLocked();
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
            return;

        var next = Math.Max(required, Math.Max(1024, _buffer.Length * 2));
        var replacement = new float[next];
        if (_count > 0)
            Array.Copy(_buffer, 0, replacement, 0, _count);
        _buffer = replacement;
    }

    private void TrimLocked()
    {
        if (_count == 0)
            return;

        var minPosition = _branches.Count == 0
            ? _baseFloatIndex + _count
            : _branches.Min(static b => b.Position);
        var removable = (int)Math.Clamp(minPosition - _baseFloatIndex, 0, _count);

        if (_count - removable > _maxBufferedFloats)
            removable = _count - _maxBufferedFloats;

        if (removable <= 0)
            return;

        var remaining = _count - removable;
        if (remaining > 0)
            Array.Copy(_buffer, removable, _buffer, 0, remaining);
        _count = remaining;
        _baseFloatIndex += removable;

        foreach (var branch in _branches)
        {
            if (branch.Position < _baseFloatIndex)
                branch.Position = _baseFloatIndex;
        }
    }

    private sealed class Branch : IAudioSource, ICooperativeAudioReadInterrupt, IDisposable
    {
        private readonly AudioSourceFanout _owner;

        public Branch(AudioSourceFanout owner, long position)
        {
            _owner = owner;
            Position = position;
        }

        public long Position { get; set; }

        public bool Disposed { get; set; }

        public AudioFormat Format => _owner._inner.Format;

        public bool IsExhausted => _owner.IsBranchExhausted(this);

        // Forwarded to the shared inner source: a branch's router may be tearing down while another
        // branch's router is mid-pull under the fanout gate — yielding the inner read releases both.
        // ClearYieldRequest is also shared, so a clear from a resuming branch can race a concurrent
        // teardown's yield; the teardown re-requests on its next read attempt, so the window is benign.
        public void RequestYieldBetweenReads() => (_owner._inner as ICooperativeAudioReadInterrupt)?.RequestYieldBetweenReads();

        public void ClearYieldRequest() => (_owner._inner as ICooperativeAudioReadInterrupt)?.ClearYieldRequest();

        public int ReadInto(Span<float> destination) => _owner.ReadBranch(this, destination);

        public void Dispose() => _owner.RemoveBranch(this);
    }
}
