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

    /// <summary>
    /// Regression: <see cref="PortAudioOutput.ElapsedSinceStart"/> must advance between PortAudio
    /// output callbacks, not only when <see cref="PortAudioOutput.PlayedSamples"/> bumps each buffer
    /// (~10–25 ms at 48 kHz), or video playhead gating can sit near ~47 Hz on 60 fps content.
    /// </summary>
    [Fact]
    public void ElapsedSinceStart_GranularBetweenCallbacks_Integration()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat, ringCapacityFrames: 8192);
        output.Start();
        var frames = Math.Max(output.Format.SampleRate / 6, 4096);
        var samples = new float[frames * output.Format.Channels];
        output.Submit(new AudioFrame(TimeSpan.Zero, output.Format, frames, samples));

        Thread.Sleep(100);

        var ticks = new List<long>(48);
        for (var i = 0; i < 48; i++)
        {
            ticks.Add(output.ElapsedSinceStart.Ticks);
            Thread.Sleep(2);
        }

        var distinct = ticks.Distinct().Count();
        Assert.True(
            distinct >= 8,
            $"expected granular ElapsedSinceStart (distinct={distinct}); played={output.PlayedSamples} streamActive={output.StreamActive}");

        output.Stop();
    }

    [Fact]
    public void PrefillFrom_WithoutStart_ReachesTarget()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat, ringCapacityFrames: 4096);
        var source = new TestFrameSource(StereoFormat, totalFrames: 10_000);
        const int target = 512;
        output.PrefillFrom(source, TimeSpan.FromSeconds(2), chunkSamples: 480, null, target);
        Assert.True(output.QueuedSamples >= target);
        Assert.True(output.QueuedSamples <= output.CapacitySamples);
    }

    [Fact]
    public void PrefillFrom_MirrorReceivesSamePackedFloatsAsRing()
    {
        if (!HasOutputDevice()) return;

        using var output = new PortAudioOutput(StereoFormat, ringCapacityFrames: 4096);
        var mirror = new FloatCountingOutput(StereoFormat);
        var source = new TestFrameSource(StereoFormat, totalFrames: 10_000);
        const int target = 400;
        output.PrefillFrom(source, TimeSpan.FromSeconds(2), chunkSamples: 480, mirror, target);
        var floatsInRing = output.QueuedSamples * StereoFormat.Channels;
        Assert.True(output.QueuedSamples >= target);
        Assert.True(mirror.SubmittedFloats >= floatsInRing);
        if (output.DroppedSamples == 0)
            Assert.Equal(floatsInRing, mirror.SubmittedFloats);
    }

    [Fact]
    public void TryPrefillPrimaryPortAudio_UsesPrimaryPortAudioOutput()
    {
        if (!HasOutputDevice()) return;

        using var pa = new PortAudioOutput(StereoFormat, ringCapacityFrames: 4096);
        using var player = new AudioPlayer(StereoFormat.SampleRate, chunkSamples: 480);
        player.AddOutput(pa);
        var source = new TestFrameSource(StereoFormat, totalFrames: 8_000);
        Assert.True(player.TryPrefillPrimaryPortAudio(source, TimeSpan.FromSeconds(2), null, 450));
        Assert.True(pa.QueuedSamples >= 450);
    }

    private sealed class TestFrameSource : IAudioSource
    {
        private readonly int _totalFrames;
        private int _emittedFrames;

        public TestFrameSource(AudioFormat format, int totalFrames)
        {
            Format = format;
            _totalFrames = totalFrames;
        }

        public AudioFormat Format { get; }
        public bool IsExhausted => _emittedFrames >= _totalFrames;

        public int ReadInto(Span<float> destination)
        {
            if (_emittedFrames >= _totalFrames)
                return 0;
            var maxFrames = destination.Length / Format.Channels;
            var nFrames = Math.Min(maxFrames, _totalFrames - _emittedFrames);
            var nFloats = nFrames * Format.Channels;
            destination[..nFloats].Fill(0.01f);
            _emittedFrames += nFrames;
            return nFloats;
        }
    }

    private sealed class FloatCountingOutput : IAudioOutput
    {
        public FloatCountingOutput(AudioFormat format) => Format = format;
        public AudioFormat Format { get; }
        public int SubmittedFloats { get; private set; }

        public void Submit(ReadOnlySpan<float> packedSamples) => SubmittedFloats += packedSamples.Length;
    }
}
