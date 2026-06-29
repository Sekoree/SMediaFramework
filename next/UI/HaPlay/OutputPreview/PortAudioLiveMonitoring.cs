using S.Media.Audio.PortAudio;

namespace HaPlay.OutputPreview;

/// <summary>PortAudio stream sizing for live monitoring (low end-to-end latency).</summary>
internal static class PortAudioLiveMonitoring
{
    /// <summary>~250 ms ring at 48 kHz — enough for live jitter without the old 1 s path.</summary>
    public static int RingCapacityFrames(int sampleRate) =>
        Math.Clamp(sampleRate / 4, 4096, 16384);

    /// <summary>
    /// Router pacing target (~65 ms at 48 kHz, at least three 480-frame chunks).
    /// Lower values caused audible dropouts while the DAC clock stayed in sync.
    /// </summary>
    public static int TargetQueueSamples(int sampleRate, int chunkSamples = 480) =>
        Math.Max(Math.Max(sampleRate / 15, chunkSamples * 3), 1920);

    public static void ApplyTo(PortAudioOutput output, int sampleRate, int chunkSamples = 480)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.TargetQueueSamples = TargetQueueSamples(sampleRate, chunkSamples);
    }
}
