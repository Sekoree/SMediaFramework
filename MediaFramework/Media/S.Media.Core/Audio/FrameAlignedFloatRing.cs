namespace S.Media.Core.Audio;

/// <summary>
/// Single-producer / single-consumer float ring that only ever moves WHOLE interleaved frames
/// (multiples of the channel count). Backing storage is a power-of-two array indexed via mask, but the
/// usable capacity is rounded DOWN to a frame multiple: a power of two is not divisible by 3, 5, 6 or 7,
/// and truncating a write to the raw free float count could otherwise split a frame in half - every
/// sample after that lands one slot early and rotates the channel order until the ring fully drains
/// (the P1-1 multichannel corruption class). All ring implementations in the framework share this type
/// so the alignment guarantee is tested once.
/// </summary>
/// <remarks>
/// <para>Thread model: one writer thread calls <see cref="Write"/>; one reader thread calls
/// <see cref="Read"/>. <see cref="Clear"/> and <see cref="DropOldestKeepingFloats"/> may be called from
/// any thread: the read cursor is CAS-guarded, so a reader that loses the race against a concurrent
/// flush zeroes what it copied and reports an empty read instead of returning discarded samples.</para>
/// <para>Counters (overflow/underrun/drop logging) stay in the owner - the ring only reports how many
/// floats each call actually moved.</para>
/// </remarks>
public sealed class FrameAlignedFloatRing
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private readonly int _channels;
    private readonly int _capacityFloats;

    private long _writeIndex;
    private long _readIndex;

    /// <param name="channels">Interleaved channel count; every transfer is a multiple of this.</param>
    /// <param name="requestedFloats">Requested capacity in floats; rounded up to a power of two for
    /// masking, then down to a frame multiple for the usable capacity.</param>
    /// <param name="minimumFloats">Lower bound applied before rounding (default 1024).</param>
    public FrameAlignedFloatRing(int channels, long requestedFloats, int minimumFloats = 1024)
    {
        if (channels < 1)
            throw new ArgumentOutOfRangeException(nameof(channels), "must be >= 1");
        if (minimumFloats < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumFloats), "must be >= 1");

        var capFloats = (int)Math.Max(Math.Max(minimumFloats, channels), Math.Min(requestedFloats, int.MaxValue / 2));
        var pow2 = 1;
        while (pow2 < capFloats) pow2 <<= 1;

        _channels = channels;
        _buffer = new float[pow2];
        _mask = pow2 - 1;
        _capacityFloats = pow2 - pow2 % channels;
    }

    public int Channels => _channels;

    /// <summary>Usable capacity in floats - always a whole-frame multiple, possibly smaller than the
    /// backing power-of-two array.</summary>
    public int CapacityFloats => _capacityFloats;

    public int CapacityFrames => _capacityFloats / _channels;

    /// <summary>Floats currently buffered between the producer and consumer (a frame multiple).</summary>
    public int BufferedFloats =>
        Math.Max(0, (int)(Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex)));

    public int BufferedFrames => BufferedFloats / _channels;

    /// <summary>
    /// Copies as many WHOLE FRAMES from <paramref name="source"/> as fit and returns the float count
    /// actually written. The remainder (also whole frames) was dropped - the caller decides whether
    /// that is overflow accounting, a log, or an error.
    /// </summary>
    public int Write(ReadOnlySpan<float> source)
    {
        ValidateFrameMultiple(source.Length, nameof(source));
        if (source.IsEmpty)
            return 0;

        var write = Volatile.Read(ref _writeIndex);
        var read = Volatile.Read(ref _readIndex);
        var free = _capacityFloats - (int)(write - read);
        var toWrite = Math.Min(source.Length, Math.Max(0, free));
        toWrite -= toWrite % _channels;
        if (toWrite <= 0)
            return 0;

        var startIdx = (int)(write & _mask);
        var firstChunk = Math.Min(toWrite, _buffer.Length - startIdx);
        source[..firstChunk].CopyTo(_buffer.AsSpan(startIdx));
        if (firstChunk < toWrite)
            source.Slice(firstChunk, toWrite - firstChunk).CopyTo(_buffer.AsSpan(0));
        Volatile.Write(ref _writeIndex, write + toWrite);
        return toWrite;
    }

    /// <summary>
    /// Copies up to <paramref name="destination"/>.Length buffered floats (whole frames) and returns
    /// the count actually read. A concurrent <see cref="Clear"/>/<see cref="DropOldestKeepingFloats"/>
    /// that wins the cursor race zeroes the copied region and reports 0 - never samples the flusher
    /// explicitly discarded. The tail beyond the returned count is left untouched (owners decide
    /// whether a short read means silence fill or a counted underrun).
    /// </summary>
    public int Read(Span<float> destination)
    {
        ValidateFrameMultiple(destination.Length, nameof(destination));
        if (destination.IsEmpty)
            return 0;

        var write = Volatile.Read(ref _writeIndex);
        var read = Volatile.Read(ref _readIndex);
        var available = (int)(write - read);
        var toRead = Math.Min(destination.Length, available);
        toRead -= toRead % _channels;
        if (toRead <= 0)
            return 0;

        var startIdx = (int)(read & _mask);
        var firstChunk = Math.Min(toRead, _buffer.Length - startIdx);
        _buffer.AsSpan(startIdx, firstChunk).CopyTo(destination);
        if (firstChunk < toRead)
            _buffer.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
        if (Interlocked.CompareExchange(ref _readIndex, read + toRead, read) != read)
        {
            destination[..toRead].Clear();
            return 0;
        }

        return toRead;
    }

    /// <summary>Discards everything currently buffered. Safe against a concurrent reader - see
    /// <see cref="Read"/>'s cursor race note.</summary>
    public void Clear()
    {
        var write = Volatile.Read(ref _writeIndex);
        Volatile.Write(ref _readIndex, write);
    }

    /// <summary>
    /// Drops the OLDEST buffered floats so at most <paramref name="keepFloats"/> (rounded down to a
    /// frame multiple) remain - the live-capture "rebase to latest" operation. Returns the float count
    /// dropped (0 when already within the limit).
    /// </summary>
    public int DropOldestKeepingFloats(int keepFloats)
    {
        if (keepFloats < 0)
            throw new ArgumentOutOfRangeException(nameof(keepFloats), "must be >= 0");
        keepFloats -= keepFloats % _channels;

        while (true)
        {
            var write = Volatile.Read(ref _writeIndex);
            var read = Volatile.Read(ref _readIndex);
            var buffered = (int)(write - read);
            if (buffered <= keepFloats)
                return 0;
            var target = write - keepFloats;
            if (Interlocked.CompareExchange(ref _readIndex, target, read) == read)
                return (int)(target - read);
        }
    }

    private void ValidateFrameMultiple(int length, string paramName)
    {
        if (length % _channels != 0)
            throw new ArgumentException(
                $"length {length} is not a multiple of channel count {_channels}", paramName);
    }
}
