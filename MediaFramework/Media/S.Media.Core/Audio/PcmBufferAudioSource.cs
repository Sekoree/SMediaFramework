namespace S.Media.Core.Audio;

/// <summary>One-shot in-memory interleaved PCM source (used when loading <see cref="AudioClip"/>).</summary>
internal sealed class PcmBufferAudioSource : IAudioSource
{
    private readonly float[] _interleaved;
    private readonly int _samplesPerChannel;
    private int _cursorFrames;

    public PcmBufferAudioSource(AudioFormat format, float[] interleaved, int samplesPerChannel)
    {
        Format = format;
        _interleaved = interleaved;
        _samplesPerChannel = samplesPerChannel;
    }

    public AudioFormat Format { get; }

    public bool IsExhausted => _cursorFrames >= _samplesPerChannel;

    public int ReadInto(Span<float> dst)
    {
        if (IsExhausted)
            return 0;

        var channels = Format.Channels;
        var framesRequested = dst.Length / channels;
        var framesAvailable = _samplesPerChannel - _cursorFrames;
        var frames = Math.Min(framesRequested, framesAvailable);
        if (frames == 0)
            return 0;

        var srcOffset = _cursorFrames * channels;
        var byteCount = frames * channels;
        _interleaved.AsSpan(srcOffset, byteCount).CopyTo(dst[..byteCount]);
        _cursorFrames += frames;
        return byteCount;
    }
}
