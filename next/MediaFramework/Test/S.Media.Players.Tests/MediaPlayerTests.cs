using S.Media.Core;
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

    [Fact]
    public void TryOpen_ForwardsOpenOptionsToRegistry()
    {
        var provider = new CapturingDecoderProvider();
        var registry = MediaRegistry.Build(b => b.AddDecoder(provider));
        var options = MediaPlayerOpenOptions.Default with
        {
            TryHardwareAcceleration = false,
            RetainDmabufForGl = true,
            RetainD3D11SharedHandleForGl = true,
            Win32Nv12SharedHandleOnlyExport = true,
            AudioPacketQueueDepth = 12,
            VideoPacketQueueDepth = 34,
            FileReadBufferBytes = 1024 * 1024,
            StreamIsSeekable = true,
            SpoolStreamToDisk = true,
            AudioStreamIndex = 2,
            VideoStreamIndex = 3,
        };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            Assert.NotNull(provider.VideoOptions);
            Assert.False(provider.VideoOptions.TryHardwareAcceleration);
            Assert.True(provider.VideoOptions.RetainDmabufForGl);
            Assert.True(provider.VideoOptions.RetainD3D11SharedHandleForGl);
            Assert.True(provider.VideoOptions.Win32Nv12SharedHandleOnlyExport);
            Assert.Equal(12, provider.VideoOptions.AudioPacketQueueDepth);
            Assert.Equal(34, provider.VideoOptions.VideoPacketQueueDepth);
            Assert.Equal(1024 * 1024, provider.VideoOptions.FileReadBufferBytes);
            Assert.True(provider.VideoOptions.StreamIsSeekable);
            Assert.True(provider.VideoOptions.SpoolToDisk);
            Assert.Equal(2, provider.VideoOptions.AudioStreamIndex);
            Assert.Equal(3, provider.VideoOptions.VideoStreamIndex);

            Assert.NotNull(provider.AudioOptions);
            Assert.True(provider.AudioOptions.StreamIsSeekable);
            Assert.True(provider.AudioOptions.SpoolToDisk);
            Assert.Equal(2, provider.AudioOptions.AudioStreamIndex);
        }
    }

    [Fact]
    public void TryOpen_DisabledAudio_DoesNotOpenAudioRouter()
    {
        var provider = new CapturingDecoderProvider();
        var registry = MediaRegistry.Build(b => b.AddDecoder(provider));
        var options = MediaPlayerOpenOptions.Default with { AudioStreamIndex = MediaPlayerOpenOptions.DisabledStreamIndex };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            Assert.Equal(1, provider.VideoOpenCount);
            Assert.Equal(0, provider.AudioOpenCount);
            Assert.Null(player.AudioRouter);
        }
    }

    [Fact]
    public void IsRunning_TracksFreerunClockForVideoOnlyPlayback()
    {
        var provider = new CapturingDecoderProvider();
        var registry = MediaRegistry.Build(b => b.AddDecoder(provider));
        var options = MediaPlayerOpenOptions.Default with { IncludeAudioRouter = false };

        Assert.True(MediaPlayer.TryOpen(registry, "file:///clip.mp4", options, null, out var player, out var error), error);
        using (player)
        {
            player.Play();

            Assert.True(player.IsRunning);
        }
    }

    private sealed class FixedDecoderProvider(IAudioSource source) : IMediaDecoderProvider
    {
        public string Name => "fixed";
        public double Probe(string uri, MediaKind kind) => kind == MediaKind.Audio ? 1.0 : 0.0;
        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => throw new NotSupportedException();
        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => source;
    }

    private sealed class CapturingDecoderProvider : IMediaDecoderProvider
    {
        public string Name => "capturing";
        public int VideoOpenCount { get; private set; }
        public int AudioOpenCount { get; private set; }
        public VideoSourceOpenOptions VideoOptions { get; private set; } = null!;
        public AudioSourceOpenOptions AudioOptions { get; private set; } = null!;

        public double Probe(string uri, MediaKind kind) => kind is MediaKind.Audio or MediaKind.Video ? 1.0 : 0.0;

        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
        {
            VideoOpenCount++;
            VideoOptions = options ?? new VideoSourceOpenOptions();
            return new SyntheticVideoSource();
        }

        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options)
        {
            AudioOpenCount++;
            AudioOptions = options ?? new AudioSourceOpenOptions();
            return new ToneSource(sampleRate: 48_000, channels: 2, chunks: 16);
        }
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

    private sealed class SyntheticVideoSource : IVideoSource, ISeekableSource
    {
        private int _frameIndex;
        private VideoFormat _format = new(16, 16, PixelFormat.Bgra32, new Rational(24, 1));

        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => false;
        public TimeSpan Duration => TimeSpan.FromSeconds(10);
        public TimeSpan Position { get; private set; }

        public void SelectOutputFormat(PixelFormat format) =>
            _format = _format with { PixelFormat = format };

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var index = Interlocked.Increment(ref _frameIndex);
            Position = TimeSpan.FromSeconds(index / 24.0);
            var stride = _format.Width * 4;
            frame = new VideoFrame(
                Position,
                _format,
                [new byte[stride * _format.Height]],
                [stride]);
            return true;
        }

        public void Seek(TimeSpan position)
        {
            Position = position;
            Volatile.Write(ref _frameIndex, Math.Max(0, (int)Math.Round(position.TotalSeconds * 24)));
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
