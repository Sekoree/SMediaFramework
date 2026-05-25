using S.Media.Core.Audio;

namespace HaPlay.Playback;

/// <summary>
/// Decorates an <see cref="IAudioOutput"/> with lightweight peak metering.
/// Measures the peak sample magnitude on every <see cref="Submit"/> call and
/// exposes it as a thread-safe dB value that the UI can poll.
/// </summary>
internal sealed class MeteringAudioOutput : IAudioOutput
{
    private readonly IAudioOutput _inner;
    private float _peakLinear;

    public MeteringAudioOutput(IAudioOutput inner)
    {
        _inner = inner;
    }

    public AudioFormat Format => _inner.Format;

    /// <summary>Peak level in dB since the last call to <see cref="ReadAndResetPeakDb"/>.
    /// Returns negative infinity when silent.</summary>
    public double ReadAndResetPeakDb()
    {
        var peak = Interlocked.Exchange(ref _peakLinear, 0f);
        return peak > 0 ? 20.0 * Math.Log10(peak) : double.NegativeInfinity;
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        float localPeak = 0;
        for (var i = 0; i < packedSamples.Length; i++)
        {
            var abs = Math.Abs(packedSamples[i]);
            if (abs > localPeak) localPeak = abs;
        }

        float prev;
        do
        {
            prev = Volatile.Read(ref _peakLinear);
            if (localPeak <= prev) break;
        } while (Interlocked.CompareExchange(ref _peakLinear, localPeak, prev) != prev);

        _inner.Submit(packedSamples);
    }
}
