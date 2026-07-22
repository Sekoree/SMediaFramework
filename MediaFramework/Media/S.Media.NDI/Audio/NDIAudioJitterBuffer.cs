using S.Media.Core.Audio;

namespace S.Media.NDI.Audio;

/// <summary>
/// Shared live-NDI audio jitter buffer. The producer may drop old data while the consumer is reading,
/// so cursor movement is delegated to <see cref="FrameAlignedFloatRing"/>'s CAS-guarded implementation.
/// </summary>
internal sealed class NDIAudioJitterBuffer
{
    private readonly FrameAlignedFloatRing _ring;
    private int _primed;

    public NDIAudioJitterBuffer(AudioFormat format, int capacityFrames, int minBufferedFrames)
    {
        Format = format;
        Channels = format.Channels;
        _ring = new FrameAlignedFloatRing(
            Channels,
            checked((long)capacityFrames * Channels),
            minimumFloats: Channels);
        MinBufferedFloats = checked(minBufferedFrames * Channels);
    }

    public AudioFormat Format { get; }

    public int Channels { get; }

    public int MinBufferedFloats { get; }

    public int AvailableFrames => _ring.BufferedFrames;

    public int CapacityFloats => _ring.CapacityFloats;

    public int ReadInto(Span<float> destination)
    {
        if (destination.Length % Channels != 0)
        {
            throw new ArgumentException(
                $"destination length {destination.Length} is not a multiple of channel count {Channels}",
                nameof(destination));
        }

        var primed = Volatile.Read(ref _primed) != 0;
        var toRead = NDIAudioReceiver.ComputeReadCount(
            destination.Length,
            _ring.BufferedFloats,
            MinBufferedFloats,
            ref primed);
        if (toRead == 0)
        {
            Volatile.Write(ref _primed, primed ? 1 : 0);
            return 0;
        }

        var read = _ring.Read(destination[..toRead]);
        // A concurrent overflow/rebase can win the read-cursor CAS after samples were copied. The shared
        // ring then clears that copy and returns zero; force a fresh jitter-buffer prime in that case.
        Volatile.Write(ref _primed, read == 0 ? 0 : primed ? 1 : 0);
        return read;
    }

    /// <summary>Queues newest samples and returns the number of floats discarded from either end.</summary>
    public int Enqueue(ReadOnlySpan<float> source)
    {
        if (source.Length % Channels != 0)
        {
            throw new ArgumentException(
                $"source length {source.Length} is not a multiple of channel count {Channels}",
                nameof(source));
        }

        var dropped = 0;
        var capacity = _ring.CapacityFloats;
        if (source.Length > capacity)
        {
            dropped += source.Length - capacity;
            source = source[^capacity..];
        }

        // Reserve room by advancing the read cursor with CAS. The old NDI-local rings assigned this
        // cursor directly while the consumer also assigned it, allowing a losing consumer to move it
        // backwards and replay overwritten samples.
        var droppedOld = _ring.DropOldestKeepingFloats(capacity - source.Length);
        if (droppedOld > 0)
        {
            dropped += droppedOld;
            Volatile.Write(ref _primed, 0);
        }

        var written = _ring.Write(source);
        dropped += source.Length - written;
        return dropped;
    }

    /// <summary>Drops old audio so at most <paramref name="keepFloats"/> remain.</summary>
    public int RebaseToLatest(int keepFloats)
    {
        var dropped = _ring.DropOldestKeepingFloats(keepFloats);
        if (dropped > 0)
        {
            Volatile.Write(
                ref _primed,
                _ring.BufferedFloats >= MinBufferedFloats ? 1 : 0);
        }

        return dropped;
    }
}
