using S.Media.Core.Audio;
using S.Media.Core.Buses;
using S.Media.Core.Video;
using S.Media.Routing;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>Phase 4 effect buses: audio bus chain, output inserts, video decorator ownership, metadata hub.</summary>
public sealed class BusEffectTests
{
    private sealed class ScaleEffect(float factor) : IAudioBusEffect
    {
        public int ConfigureCalls;
        public bool Disposed;

        public void Configure(AudioFormat format) => ConfigureCalls++;

        public void Process(Span<float> interleaved, long samplePosition)
        {
            for (var i = 0; i < interleaved.Length; i++)
                interleaved[i] *= factor;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void AudioEffectBus_EmptyChain_IsBitExactPassthrough()
    {
        var bus = new AudioEffectBus(new AudioFormat(48_000, 2));
        var input = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        bus.Submit(input);

        var output = new float[4];
        Assert.Equal(4, bus.ReadInto(output));
        Assert.Equal(input, output);
    }

    [Fact]
    public void AudioEffectBus_ChainAppliesInOrder_AndSwapDisposesRemoved()
    {
        var bus = new AudioEffectBus(new AudioFormat(48_000, 2));
        var half = new ScaleEffect(0.5f);
        var doubled = new ScaleEffect(2f);
        bus.SetEffects([half, doubled]);
        Assert.Equal(1, half.ConfigureCalls);

        bus.Submit(new float[] { 0.8f, 0.8f });
        var output = new float[2];
        bus.ReadInto(output);
        Assert.Equal(0.8f, output[0], 3); // 0.8 × 0.5 × 2

        bus.SetEffects([doubled]);
        // Review M5: a removed effect must NOT be disposed at swap time (the read thread may still be
        // inside it) - it retires on the read thread's next pass.
        Assert.False(half.Disposed);

        bus.Submit(new float[] { 0.25f, 0.25f });
        bus.ReadInto(output);
        Assert.True(half.Disposed);  // retired at the top of the read pass
        Assert.False(doubled.Disposed);
        Assert.Equal(0.5f, output[0], 3);

        bus.Dispose();
        Assert.True(doubled.Disposed);
    }

    [Fact]
    public void AudioEffectOutput_ProcessesCopy_NeverMutatingTheSharedChunk()
    {
        var sink = new CapturingAudioOutput(new AudioFormat(48_000, 2));
        using var insert = AudioEffectOutput.Wrap(sink, [new ScaleEffect(0.5f)]);

        var shared = new float[] { 1f, 1f, 1f, 1f };
        insert.Submit(shared);

        Assert.Equal([0.5f, 0.5f, 0.5f, 0.5f], sink.Last);
        Assert.Equal([1f, 1f, 1f, 1f], shared); // the router's shared chunk is untouched
    }

    [Fact]
    public void GainAudioEffect_RampsToTargetWithoutOvershoot()
    {
        var gain = new GainAudioEffect();
        gain.Configure(new AudioFormat(48_000, 1));
        gain.GainDb = -6.0206; // 0.5 linear

        var samples = new float[48_000];
        Array.Fill(samples, 1f);
        gain.Process(samples, 0);

        Assert.Equal(0.5f, samples[^1], 3); // settled at target by the end of the chunk
        Assert.True(samples[0] > 0.5f);     // ramped, not stepped
        for (var i = 1; i < samples.Length; i++)
            Assert.True(samples[i] <= samples[i - 1] + 1e-6f, "gain-down ramp must be monotonic");
    }

    private sealed class ClockedStatsAudioOutput(AudioFormat format)
        : IAudioOutput, IClockedOutput, IPlaybackClock, IAudioOutputPlaybackStats, IDisposable
    {
        public bool Disposed;
        public AudioFormat Format { get; } = format;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => true;
        public TimeSpan ElapsedSinceStart => TimeSpan.FromSeconds(1);
        public bool IsAdvancing => true;
        public long PlayedSamples => 42;
        public long UnderrunSamples => 0;
        public long DroppedSamples => 0;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void AudioEffectOutput_Wrap_PreservesClockPlaybackAndStatsCapabilities()
    {
        // Review H3: erasing IClockedOutput made the router stop slaving to the hardware clock the
        // moment an effect was inserted (wall-clock pacing + drift on the main output).
        var hw = new ClockedStatsAudioOutput(new AudioFormat(48_000, 2));
        using var wrapped = AudioEffectOutput.Wrap(hw, [new ScaleEffect(0.5f)]);

        var clocked = Assert.IsAssignableFrom<IClockedOutput>(wrapped);
        Assert.True(clocked.WaitForCapacity(480, CancellationToken.None));
        var playback = Assert.IsAssignableFrom<IPlaybackClock>(wrapped);
        Assert.True(playback.IsAdvancing);
        var stats = Assert.IsAssignableFrom<IAudioOutputPlaybackStats>(wrapped);
        Assert.Equal(42, stats.PlayedSamples);

        // A plain inner must NOT gain fake capabilities.
        var plain = AudioEffectOutput.Wrap(new CapturingAudioOutput(new AudioFormat(48_000, 2)), []);
        Assert.False(plain is IClockedOutput);
        Assert.False(plain is IPlaybackClock);
        Assert.False(plain is IAudioOutputPlaybackStats);
        plain.Dispose();
    }

    [Fact]
    public void AudioEffectOutput_DisposeInner_ControlsTerminalOwnership()
    {
        // Review H4: session-owned terminal → the wrapper chain is disposal-transparent; borrowed
        // carrier → dispose stops at the wrapper (the host releases the carrier itself).
        var owned = new ClockedStatsAudioOutput(new AudioFormat(48_000, 2));
        AudioEffectOutput.Wrap(owned, [new ScaleEffect(1f)], disposeInner: true).Dispose();
        Assert.True(owned.Disposed);

        var borrowed = new ClockedStatsAudioOutput(new AudioFormat(48_000, 2));
        var effect = new ScaleEffect(1f);
        AudioEffectOutput.Wrap(borrowed, [effect], disposeInner: false).Dispose();
        Assert.False(borrowed.Disposed);
        Assert.True(effect.Disposed); // the wrapper ALWAYS retires its own effects
    }

    [Fact]
    public void AudioEffectOutput_SwapRetiresOnNextSubmit_NotAtSwapTime()
    {
        var sink = new CapturingAudioOutput(new AudioFormat(48_000, 2));
        using var insert = AudioEffectOutput.Wrap(sink, [new ScaleEffect(0.5f)]);
        var removed = (ScaleEffect)insert.Effects[0];

        insert.SetEffects([]);
        Assert.False(removed.Disposed); // M5: never disposed under a potentially-running Process

        insert.Submit(new float[] { 1f, 1f });
        Assert.True(removed.Disposed); // retired at the top of the processing thread's next pass
    }

    [Fact]
    public void GainAudioEffect_RampAdvancesPerFrame_SameGainOnAllChannels()
    {
        // Review M5 sub-item: the ramp used to advance per interleaved FLOAT - channels× too fast, and
        // channels within one frame received different gains.
        var gain = new GainAudioEffect();
        gain.Configure(new AudioFormat(48_000, 2));
        gain.GainDb = float.NegativeInfinity; // target 0 → maximal ramp distance

        var stereo = new float[960]; // 480 frames = exactly the 10 ms ramp span at 48 kHz
        Array.Fill(stereo, 1f);
        gain.Process(stereo, 0);

        for (var f = 0; f < stereo.Length; f += 2)
            Assert.Equal(stereo[f], stereo[f + 1]); // L == R within every frame
        Assert.True(stereo[0] > 0.9f, "first frame should have barely ramped");
        Assert.Equal(0f, stereo[^1], 3); // reached target at the END of the 10 ms span, not halfway

        // NaN config must be rejected, not turned into NaN audio.
        gain.GainDb = double.NaN;
        var probe = new float[] { 1f, 1f };
        gain.Process(probe, 0);
        Assert.False(float.IsNaN(probe[0]));
    }

    [Fact]
    public void VideoEffectBusOutput_SwapRetiresOnNextSubmit()
    {
        var sink = new CapturingVideoOutput();
        var effect = new CountingVideoEffect();
        using var insert = new VideoEffectBusOutput(sink, [effect]);
        insert.Configure(new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1)));

        insert.SetEffects([]);
        Assert.False(effect.Disposed); // M5

        insert.Submit(MakeFrame());
        Assert.True(effect.Disposed);
        sink.Last?.Dispose();
    }

    private sealed class CapturingAudioOutput(AudioFormat format) : IAudioOutput
    {
        public float[] Last = [];
        public AudioFormat Format { get; } = format;
        public void Submit(ReadOnlySpan<float> packedSamples) => Last = packedSamples.ToArray();
    }

    private sealed class CountingVideoEffect : IVideoBusEffect
    {
        public int Processed;
        public bool Disposed;

        public void Configure(VideoFormat format) { }

        public VideoFrame Process(VideoFrame frame, TimeSpan presentationTime)
        {
            Processed++;
            return frame;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class CapturingVideoOutput : IVideoOutput
    {
        public VideoFrame? Last;
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            Last?.Dispose();
            Last = frame;
        }
    }

    private static VideoFrame MakeFrame() => new(
        TimeSpan.Zero,
        new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1)),
        [new byte[16]],
        [8]);

    [Fact]
    public void VideoEffectBusOutput_RunsChain_AndForwardsExactlyOneOwnedFrame()
    {
        var sink = new CapturingVideoOutput();
        var effect = new CountingVideoEffect();
        using var insert = new VideoEffectBusOutput(sink, [effect]);

        insert.Configure(new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1)));
        var frame = MakeFrame();
        insert.Submit(frame);

        Assert.Equal(1, effect.Processed);
        Assert.Same(frame, sink.Last); // pass-through effect: the same frame reaches the inner output

        insert.Dispose();
        Assert.True(effect.Disposed);
        sink.Last?.Dispose();
    }

    [Fact]
    public void BusMetadataHub_DeliversCurrentItemToLateSubscribers_AndIsolatesThrowingSinks()
    {
        var hub = new BusMetadataHub();
        hub.Publish(new MediaItemMetadata("Song A", "Artist"));

        var received = new List<string?>();
        var throwing = new ThrowingSink();
        var listener = new DelegateSink(m => received.Add(m.Title));
        hub.Attach(throwing);
        hub.Attach(listener);

        Assert.Equal(["Song A"], received); // late subscriber got the current item immediately

        hub.Publish(new MediaItemMetadata("Song B"));
        Assert.Equal(["Song A", "Song B"], received); // the throwing sink didn't break delivery
        Assert.True(throwing.Calls >= 2);
        Assert.Equal("Song B", hub.CurrentItem?.Title);

        hub.Publish(new FrameStatsMetadata(0xFF808080, 0xFFFF0000, 0.5, TimeSpan.Zero));
        Assert.Equal(0xFFFF0000u, listener.LastStats.DominantArgb);
    }

    private sealed class ThrowingSink : IBusMetadataSink
    {
        public int Calls;
        public void OnItemMetadata(MediaItemMetadata metadata) { Calls++; throw new InvalidOperationException("boom"); }
        public void OnFrameStats(in FrameStatsMetadata stats) => throw new InvalidOperationException("boom");
    }

    private sealed class DelegateSink(Action<MediaItemMetadata> onItem) : IBusMetadataSink
    {
        public FrameStatsMetadata LastStats;
        public void OnItemMetadata(MediaItemMetadata metadata) => onItem(metadata);
        public void OnFrameStats(in FrameStatsMetadata stats) => LastStats = stats;
    }
}
