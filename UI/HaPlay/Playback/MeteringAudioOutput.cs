using S.Media.Core.Audio;
using S.Media.Time;

namespace HaPlay.Playback;

/// <summary>
/// Decorates an <see cref="IAudioOutput"/> with lightweight peak metering.
/// Measures the peak sample magnitude on every <see cref="Submit"/> call and
/// exposes it as a thread-safe dB value that the UI can poll.
/// Disposal-transparent: disposing the wrapper disposes the inner output (when it is disposable), so a
/// session-owned lease output can be wrapped without changing who releases the device.
/// </summary>
internal class MeteringAudioOutput : IAudioOutput, IAudioOutputChannelCapabilities, IFlushableOutput, IDisposable
{
    protected readonly IAudioOutput Inner;
    private float _peakLinear;

    public MeteringAudioOutput(IAudioOutput inner)
    {
        Inner = inner;
    }

    public static MeteringAudioOutput Wrap(IAudioOutput inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (inner is IClockedOutput clocked)
        {
            return inner is IPlaybackClock playbackClock
                ? new ClockedPlaybackMeteringAudioOutput(inner, clocked, playbackClock)
                : new ClockedMeteringAudioOutput(inner, clocked);
        }

        return new MeteringAudioOutput(inner);
    }

    public AudioFormat Format => Inner.Format;

    public AudioOutputChannelCapabilities ChannelCapabilities =>
        Inner is IAudioOutputChannelCapabilities caps
            ? caps.ChannelCapabilities
            : AudioOutputChannelCapabilities.Fixed(Format.Channels);

    public void Flush()
    {
        if (Inner is IFlushableOutput flushable)
            flushable.Flush();
    }

    public void Dispose() => (Inner as IDisposable)?.Dispose();

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

        Inner.Submit(packedSamples);
    }

    private class ClockedMeteringAudioOutput(
        IAudioOutput inner,
        IClockedOutput clocked) : MeteringAudioOutput(inner), IClockedOutput
    {
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) =>
            clocked.WaitForCapacity(chunkSamples, token);
    }

    private sealed class ClockedPlaybackMeteringAudioOutput(
        IAudioOutput inner,
        IClockedOutput clocked,
        IPlaybackClock playbackClock) : ClockedMeteringAudioOutput(inner, clocked), IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => playbackClock.ElapsedSinceStart;

        public bool IsAdvancing => playbackClock.IsAdvancing;
    }
}
