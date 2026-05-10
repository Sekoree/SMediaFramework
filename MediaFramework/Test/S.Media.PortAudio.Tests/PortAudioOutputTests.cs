using S.Media.Core.Audio;
using S.Media.PortAudio;
using Xunit;

namespace S.Media.PortAudio.Tests;

public class PortAudioOutputTests
{
    private static readonly AudioFormat StereoFormat = new(48000, 2);

    private static bool HasOutputDevice()
    {
        PortAudioRuntime.Acquire();
        try { return PortAudioRuntime.DefaultOutputDevice >= 0; }
        finally { PortAudioRuntime.Release(); }
    }

    [Fact]
    public void Construct_WithoutStart_DoesNotOpenStream()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat);
        Assert.False(output.IsRunning);
        Assert.Equal(StereoFormat, output.Format);
        Assert.Equal(0, output.QueuedSamples);
    }

    [Fact]
    public void Submit_FormatMismatch_Throws()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat);
        var wrongFormat = new AudioFormat(44100, 2);
        var frame = new AudioFrame(TimeSpan.Zero, wrongFormat, 100, new float[200]);

        Assert.Throws<ArgumentException>(() => output.Submit(frame));
    }

    [Fact]
    public void Submit_AddsToQueuedSamples()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat, ringCapacityFrames: 1024);
        var samples = new float[100 * 2]; // 100 frames stereo
        var frame = new AudioFrame(TimeSpan.Zero, StereoFormat, 100, samples);

        output.Submit(frame);

        Assert.Equal(100, output.QueuedSamples);
    }

    [Fact]
    public void Submit_BeyondCapacity_DropsExcess()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat, ringCapacityFrames: 64);
        var capacity = output.CapacitySamples;
        var oversized = new float[(capacity + 200) * 2];
        var frame = new AudioFrame(TimeSpan.Zero, StereoFormat, capacity + 200, oversized);

        output.Submit(frame);

        Assert.Equal(capacity, output.QueuedSamples);
        Assert.Equal(200L * 2, output.DroppedSamples);
    }

    [Fact]
    public void RingBuffer_Wraparound_PreservesData()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat, ringCapacityFrames: 64);
        var capFloats = output.CapacitySamples * 2;

        // Fill 75% of the ring with a known pattern, then drain it,
        // then write more so the next write straddles the buffer end.
        var first = new float[capFloats * 3 / 4];
        for (var i = 0; i < first.Length; i++) first[i] = i + 1;
        output.Submit(new AudioFrame(TimeSpan.Zero, StereoFormat, first.Length / 2, first));

        var drainBuffer = new float[first.Length];
        Assert.Equal(first.Length, output.TryDrainForTest(drainBuffer));
        Assert.Equal(first, drainBuffer);

        // Now the read index is advanced; write a chunk that wraps around.
        var second = new float[capFloats / 2];
        for (var i = 0; i < second.Length; i++) second[i] = -(i + 1);
        output.Submit(new AudioFrame(TimeSpan.Zero, StereoFormat, second.Length / 2, second));

        var drainAgain = new float[second.Length];
        Assert.Equal(second.Length, output.TryDrainForTest(drainAgain));
        Assert.Equal(second, drainAgain);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        if (!HasOutputDevice()) return;

        var output = new PortAudioOutput(StereoFormat);
        output.Dispose();
        output.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            output.Submit(new AudioFrame(TimeSpan.Zero, StereoFormat, 1, new float[2])));
    }

    [Fact]
    public void Start_Stop_LifecycleClean()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat);
        output.Start();
        Assert.True(output.IsRunning);

        // Submit ~50 ms of silence so the callback can fire without underruns.
        var samples = new float[output.Format.SampleRate / 20 * output.Format.Channels];
        output.Submit(new AudioFrame(TimeSpan.Zero, output.Format, samples.Length / output.Format.Channels, samples));
        Thread.Sleep(80);

        output.Stop();
        Assert.False(output.IsRunning);
        // Fresh start works after stop.
        output.Start();
        Assert.True(output.IsRunning);
        output.Stop();
    }
}
