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
/// path - the SPSC ring decouples the per-output pump thread (Submit) from the run loop thread
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
/// <see cref="IsExhausted"/> is always <c>false</c> - the bus is a passive node. Hosts that need
/// natural completion semantics manage that at a higher level (e.g. remove the bus's routes when
/// upstream sources exhaust).
/// </para>
/// </remarks>
public sealed class AudioBus : IAudioOutput, IAudioOutputChannelCapabilities, IAudioSource, IFlushableOutput
{
    private readonly AudioFormat _format;
    private readonly FrameAlignedFloatRing _ring;
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

        _format = format;
        _ring = new FrameAlignedFloatRing(format.Channels, requestedFloats);
    }

    public AudioFormat Format => _format;
    public AudioOutputChannelCapabilities ChannelCapabilities => AudioOutputChannelCapabilities.Fixed(_format.Channels);
    public bool IsExhausted => false;

    /// <summary>Approximate samples-per-channel currently buffered between the producer and consumer.</summary>
    public int BufferedSamples => _ring.BufferedFrames;

    /// <summary>Maximum samples-per-channel the ring can hold.</summary>
    public int CapacitySamples => _ring.CapacityFrames;

    /// <summary>Total floats dropped by Submit because the ring was full (router buffer too small or downstream stuck).</summary>
    public long OverflowFloats => Volatile.Read(ref _overflowFloats);

    /// <summary>Total floats reported as silence by ReadInto because the ring underflowed before a Submit arrived.</summary>
    public long UnderflowFloats => Volatile.Read(ref _underflowFloats);

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        if (packedSamples.Length == 0) return;

        var written = _ring.Write(packedSamples);
        var dropped = packedSamples.Length - written;
        if (dropped > 0)
            Interlocked.Add(ref _overflowFloats, dropped);
    }

    public int ReadInto(Span<float> destination)
    {
        if (destination.Length == 0) return 0;

        var read = _ring.Read(destination);
        var shortfall = destination.Length - read;
        if (shortfall > 0)
            Interlocked.Add(ref _underflowFloats, shortfall);
        return read;
    }

    /// <summary>
    /// Discards samples queued by this producer without affecting any downstream hardware output.
    /// This makes a bus safe as one client of a shared physical output: pausing one client cannot
    /// flush audio submitted by the other clients.
    /// </summary>
    public void Flush() => _ring.Clear();
}
