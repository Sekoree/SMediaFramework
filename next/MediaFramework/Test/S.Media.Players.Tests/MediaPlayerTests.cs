using Xunit;

namespace S.Media.Players.Tests;

public sealed class MediaPlayerTests
{
    [Fact]
    public void OpenAudio_WiresDecodedSourceToOutput()
    {
        var source = new ToneSource(sampleRate: 48_000, channels: 6, chunks: 8);
        var backend = new CollectingBackend();
        var registry = MediaRegistry.Build(b => b.AddDecoder(new FixedDecoderProvider(source)));

        using var player = MediaPlayer.OpenAudio(registry, backend, "file:///tone.wav");
        player.Play();

        Assert.True(SpinWait.SpinUntil(() => backend.Output.NonZeroSamples > 0, TimeSpan.FromSeconds(2)),
            "MediaPlayer opened a source and output but did not route non-zero audio between them.");
    }

    private sealed class FixedDecoderProvider(IAudioSource source) : IMediaDecoderProvider
    {
        public string Name => "fixed";
        public double Probe(string uri, MediaKind kind) => kind == MediaKind.Audio ? 1.0 : 0.0;
        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => throw new NotSupportedException();
        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => source;
    }

    private sealed class ToneSource(int sampleRate, int channels, int chunks) : IAudioSource
    {
        private int _remainingChunks = chunks;

        public AudioFormat Format { get; } = new(sampleRate, channels);
        public bool IsExhausted => Volatile.Read(ref _remainingChunks) <= 0;

        public int ReadInto(Span<float> destination)
        {
            if (Interlocked.Decrement(ref _remainingChunks) < 0)
                return 0;

            for (var i = 0; i < destination.Length; i++)
                destination[i] = ((i % channels) + 1) / 16f;
            return destination.Length;
        }
    }

    private sealed class CollectingBackend : IAudioBackend
    {
        public CollectingOutput Output { get; } = new(sampleRate: 48_000, channels: 2);
        public string Name => "collecting";

        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
        [
            new("default", "Default", MaxChannels: 2, DefaultSampleRate: 48_000, IsDefault: true),
        ];

        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];

        public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
        {
            Assert.Equal("default", deviceId);
            Assert.Equal(Output.Format, format);
            return Output;
        }

        public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }

    private sealed class CollectingOutput(int sampleRate, int channels) : IAudioOutput, IClockedOutput, IPlaybackClock
    {
        private long _submittedSamples;
        private long _nonZeroSamples;

        public AudioFormat Format { get; } = new(sampleRate, channels);
        public long NonZeroSamples => Volatile.Read(ref _nonZeroSamples);
        public TimeSpan ElapsedSinceStart => TimeSpan.FromSeconds(Volatile.Read(ref _submittedSamples) / (double)Format.Channels / Format.SampleRate);
        public bool IsAdvancing => true;

        public void Submit(ReadOnlySpan<float> samples)
        {
            foreach (var sample in samples)
            {
                if (sample != 0f)
                    Interlocked.Increment(ref _nonZeroSamples);
            }

            Interlocked.Add(ref _submittedSamples, samples.Length);
        }

        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
    }
}
