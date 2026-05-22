namespace S.Media.Core.Audio;

/// <summary>
/// Sub-mix bus: implements both <see cref="IAudioOutput"/> (routes can target it) and
/// <see cref="IAudioSource"/> (routes can pull from it). Lets a consumer build mixer-style chains
/// like "drum group → comp/limiter → master out" without writing custom plumbing.
/// </summary>
/// <remarks>
/// <para>
/// Wiring pattern: register the bus on the router as <strong>both</strong> a output (via
/// <see cref="AudioRouter.AddOutput"/>) and a source (via <see cref="AudioRouter.AddSource"/>).
/// Per chunk, the router pumps mixed audio into the bus (output side) and reads it back on the
/// source side. The bus introduces approximately one router chunk of latency through the feedback
/// path — the SPSC ring decouples the per-output pump thread (Submit) from the run loop thread
/// (ReadInto) without an internal lock.
/// </para>
/// <para>
/// Ring sizing defaults to ≈80 ms of buffered audio so brief scheduling drift between the pump
/// and run-loop threads doesn't underflow. Override via <see cref="AudioBus(AudioFormat, TimeSpan?)"/>
/// for tighter / looser tolerances. Overruns (Submit faster than ReadInto) are counted in
/// <see cref="OverflowFloats"/> and Submit drops the excess. Underruns return the available
/// fragment plus a counted-in-<see cref="UnderflowFloats"/> short read.
/// </para>
/// <para>
/// <see cref="IsExhausted"/> is always <c>false</c> — the bus is a passive node. Hosts that need
/// natural completion semantics manage that at a higher level (e.g. remove the bus's routes when
/// upstream sources exhaust).
/// </para>
/// </remarks>
public sealed class AudioBus : IAudioOutput, IAudioOutputChannelCapabilities, IAudioSource
{
    private readonly AudioFormat _format;
    private readonly int _channels;
    private readonly float[] _ring;
    private readonly int _ringMask;
    private long _writeIndex;
    private long _readIndex;
    private long _overflowFloats;
    private long _underflowFloats;

    /// <param name="format">Sample rate + channel count. Must match every route's endpoint.</param>
    /// <param name="maxBufferedDuration">Approximate ring capacity expressed as time (default ≈80 ms).</param>
    public AudioBus(AudioFormat format, TimeSpan? maxBufferedDuration = null)
    {
        format.Validate(nameof(format));
        var requested = maxBufferedDuration ?? TimeSpan.FromMilliseconds(80);
        if (requested <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedDuration), "must be > 0");

        var floatsPerSec = (long)format.SampleRate * format.Channels;
        var requestedFloats = (long)Math.Ceiling(requested.TotalSeconds * floatsPerSec);
        var capFloats = (int)Math.Max(1024L, Math.Min(requestedFloats, int.MaxValue / 2));
        var pow2 = 1;
        while (pow2 < capFloats) pow2 <<= 1;

        _format = format;
        _channels = format.Channels;
        _ring = new float[pow2];
        _ringMask = pow2 - 1;
    }

    public AudioFormat Format => _format;
    public AudioOutputChannelCapabilities ChannelCapabilities => AudioOutputChannelCapabilities.Fixed(_channels);
    public bool IsExhausted => false;

    /// <summary>Total floats dropped by Submit because the ring was full (router buffer too small or downstream stuck).</summary>
    public long OverflowFloats => Volatile.Read(ref _overflowFloats);

    /// <summary>Total floats reported as silence by ReadInto because the ring underflowed before a Submit arrived.</summary>
    public long UnderflowFloats => Volatile.Read(ref _underflowFloats);

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        if ((packedSamples.Length % _channels) != 0)
            throw new ArgumentException(
                $"packedSamples.Length {packedSamples.Length} is not a multiple of channel count {_channels}",
                nameof(packedSamples));
        if (packedSamples.Length == 0) return;

        var write = Volatile.Read(ref _writeIndex);
        var read = Volatile.Read(ref _readIndex);
        var freeFloats = _ring.Length - (int)(write - read);
        var toWrite = Math.Min(packedSamples.Length, Math.Max(0, freeFloats));

        if (toWrite > 0)
        {
            var startIdx = (int)(write & _ringMask);
            var firstChunk = Math.Min(toWrite, _ring.Length - startIdx);
            packedSamples[..firstChunk].CopyTo(_ring.AsSpan(startIdx));
            if (firstChunk < toWrite)
                packedSamples.Slice(firstChunk, toWrite - firstChunk).CopyTo(_ring.AsSpan(0));
            Volatile.Write(ref _writeIndex, write + toWrite);
        }

        var dropped = packedSamples.Length - toWrite;
        if (dropped > 0)
            Interlocked.Add(ref _overflowFloats, dropped);
    }

    public int ReadInto(Span<float> destination)
    {
        if ((destination.Length % _channels) != 0)
            throw new ArgumentException(
                $"destination.Length {destination.Length} is not a multiple of channel count {_channels}",
                nameof(destination));
        if (destination.Length == 0) return 0;

        var write = Volatile.Read(ref _writeIndex);
        var read = Volatile.Read(ref _readIndex);
        var available = (int)(write - read);
        var toRead = Math.Min(destination.Length, available);

        if (toRead > 0)
        {
            var startIdx = (int)(read & _ringMask);
            var firstChunk = Math.Min(toRead, _ring.Length - startIdx);
            _ring.AsSpan(startIdx, firstChunk).CopyTo(destination);
            if (firstChunk < toRead)
                _ring.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
            Volatile.Write(ref _readIndex, read + toRead);
        }

        var shortfall = destination.Length - toRead;
        if (shortfall > 0)
            Interlocked.Add(ref _underflowFloats, shortfall);
        return toRead;
    }
}
