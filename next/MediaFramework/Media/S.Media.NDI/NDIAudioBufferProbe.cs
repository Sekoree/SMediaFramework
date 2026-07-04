using System.Diagnostics;
using S.Media.NDI.Audio;

namespace S.Media.NDI;

/// <summary>
/// The three audio jitter-buffer presets a network probe derives for an NDI source. <see cref="Lowest"/> is the
/// measured floor (the smallest reserve that didn't starve the receiver); <see cref="Balanced"/> and
/// <see cref="Safe"/> add progressively more headroom for jitter. Smaller = lower latency (audio sits closer to
/// the live video) at higher underrun risk. <see cref="HasAudio"/> is false when the source carried no audio to
/// probe (the preset values then fall back to the NDI default reserve and should be ignored).
/// </summary>
public readonly record struct NDIAudioBufferPresets(TimeSpan Lowest, TimeSpan Balanced, TimeSpan Safe)
{
    public bool HasAudio { get; init; }
}

/// <summary>
/// Probes the lowest glitch-free audio jitter buffer for an NDI source on the current network and returns
/// latency/safety presets — for a UI to offer "lowest / balanced / safe", or to pass <see cref="NDIAudioBufferPresets.Lowest"/>
/// straight to <see cref="NDIModule"/>. The buffer is the dominant tunable latency between a live source's audio
/// and its low-latency video, so shrinking it is the A/V-sync lever (audio comes forward to meet the video).
/// </summary>
/// <remarks>
/// Glitches are measured source-side: the receiver's audio is pumped at the consumption rate and chunks the ring
/// couldn't fully supply are counted (a short read = a silence gap the router would pad = an audible dropout).
/// No output device is opened; video is drained on a side thread so the receiver runs under the realistic A/V
/// load of the shared connection. The call blocks for roughly (warmup + measure) × tested-sizes — run it on a
/// background thread. <paramref name="onStep"/> reports each tested size as it completes (for progress UI), and
/// <paramref name="cancellationToken"/> stops it between sizes.
/// </remarks>
public static class NDIAudioBufferProbe
{
    // Ramp from a safe reserve down toward a near-zero one; the first size that starves fixes the floor at the
    // last good size above it. Local-network senders often reach single-digit milliseconds glitch-free.
    private static readonly int[] CandidatesMs =
        [60, 45, 35, 28, 22, 18, 15, 12, 10, 8, 6, 5, 4, 3, 2, 1];

    public static NDIAudioBufferPresets Probe(
        NDIDiscoveredSource source,
        TimeSpan? warmup = null,
        TimeSpan? measure = null,
        Action<TimeSpan, int>? onStep = null,
        CancellationToken cancellationToken = default)
    {
        var warm = warmup ?? TimeSpan.FromSeconds(1.2);
        var meas = measure ?? TimeSpan.FromSeconds(2.5);

        TimeSpan? floor = null;
        foreach (var ms in CandidatesMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var underruns = MeasureUnderruns(source, ms, warm, meas, cancellationToken);
            if (underruns < 0) // no audio on the source — presets are meaningless
            {
                var d = NDIAudioReceiver.DefaultMinBufferedDuration;
                return new NDIAudioBufferPresets(d, d, d) { HasAudio = false };
            }

            onStep?.Invoke(TimeSpan.FromMilliseconds(ms), underruns);
            if (underruns != 0)
                break; // first size that glitches; the floor is the last good size above it
            floor = TimeSpan.FromMilliseconds(ms);
        }

        // If even the largest candidate glitched, keep that largest as the floor (network too jittery to go low).
        var f = floor ?? TimeSpan.FromMilliseconds(CandidatesMs[0]);
        return new NDIAudioBufferPresets(
            Lowest: f,
            Balanced: RoundMs(f * 2.0),
            Safe: RoundMs(f * 4.0)) { HasAudio = true };
    }

    private static TimeSpan RoundMs(TimeSpan t) => TimeSpan.FromMilliseconds(Math.Round(t.TotalMilliseconds));

    // Open the receiver at one buffer size; return starved audio chunks over the measure window (or -1 for no
    // audio). Warms up first so the ring is primed before counting.
    private static int MeasureUnderruns(
        NDIDiscoveredSource source, int bufferMs, TimeSpan warmup, TimeSpan measure, CancellationToken ct)
    {
        using var ndi = NDISource.Open(source, new NDISourceOptions
        {
            ReceiveVideo = true,
            ReceiveAudio = true,
            AudioMinBufferedDuration = TimeSpan.FromMilliseconds(bufferMs),
        });
        if (!ndi.WaitForStreams(TimeSpan.FromSeconds(3)) || !ndi.TryGetAudioFormat(out var af))
            return -1;

        // Drain video on a side thread so the receiver runs under the realistic A/V load of the shared connection.
        var draining = true;
        var videoThread = new Thread(() =>
        {
            while (Volatile.Read(ref draining))
            {
                if (ndi.Video.TryReadNextFrame(out var frame))
                    frame.Dispose();
                else
                    Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "ndi-probe-video-drain" };
        videoThread.Start();

        try
        {
            PumpAudio(ndi, af.SampleRate, af.Channels, warmup, count: false, ct); // prime + settle
            return PumpAudio(ndi, af.SampleRate, af.Channels, measure, count: true, ct);
        }
        finally
        {
            Volatile.Write(ref draining, false);
            videoThread.Join(TimeSpan.FromSeconds(1));
        }
    }

    // Consume audio at the wall-clock sample rate, counting chunks the ring couldn't fully supply. The consumed
    // cursor advances by the full chunk even on a short read (the missing samples ARE the gap), so pacing stays
    // locked to the audio clock and this measures real ring starvation rather than pacing skew.
    private static int PumpAudio(
        NDISource ndi, int rate, int channels, TimeSpan duration, bool count, CancellationToken ct)
    {
        var chunkSamples = Math.Max(1, rate / 100); // ~10 ms chunks
        var chunk = new float[chunkSamples * channels];
        var underruns = 0;
        var sw = Stopwatch.StartNew();
        long consumed = 0;
        while (sw.Elapsed < duration)
        {
            ct.ThrowIfCancellationRequested();
            var due = (long)(sw.Elapsed.TotalSeconds * rate);
            while (consumed < due)
            {
                var n = ndi.Audio.ReadInto(chunk);
                if (count && n < chunk.Length)
                    underruns++;
                consumed += chunkSamples;
            }

            Thread.Sleep(2);
        }

        return underruns;
    }
}
