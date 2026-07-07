using System.Diagnostics;
using S.Media.Core;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.Players.Tests;

public sealed class MediaPlayerTests(ITestOutputHelper output)
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
    public void TryOpen_TargetAudioSampleRate_ResamplesTheDecodedSource()
    {
        var source = new ToneSource(sampleRate: 44_100, channels: 2, chunks: 8);
        var requestedRate = 0;
        var registry = MediaRegistry.Build(b => b
            .AddDecoder(new FixedDecoderProvider(source))
            .SetResamplerFactory((inner, rate) =>
            {
                requestedRate = rate;
                return new TargetRateAudioSource(inner, rate);
            }));
        var options = MediaPlayerOpenOptions.Default with { TargetAudioSampleRate = 48_000 };

        Assert.True(MediaPlayer.TryOpen(
            registry, "file:///tone.wav", options, null, out var player, out var error), error);
        using (player)
        {
            Assert.Equal(48_000, requestedRate);
            Assert.Equal(48_000, player.SampleRate);
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

    [Fact]
    public void ManySimultaneousPlayers_AllStayScheduled_ThreadCostMeasured()
    {
        // TIME-01: evidence for the per-clip-thread scheduling model at a representative max simultaneous clip
        // count. Each player runs its own decode/pump; if that model missed deadlines or starved a clip under
        // load, some players would produce no audio within the soak window. We assert every player stays
        // scheduled and record the thread cost, so a "consolidate the scheduler" decision can be made on
        // evidence rather than speculation (the finding: the per-clip model keeps every clip scheduled here).
        // Scale the count with core count: the representative max is 24, but a constrained CI runner (2 cores,
        // heavily contended) would otherwise be oversubscribed into a multi-minute stall / blame-hang. Kept at
        // ≥2× cores so the per-clip model is still exercised under real oversubscription — which is the point.
        var players = Math.Clamp(Environment.ProcessorCount * 2, 4, 24);
        var startThreads = Process.GetCurrentProcess().Threads.Count;

        var running = new List<(MediaPlayer Player, CollectingBackend Backend)>();
        try
        {
            for (var i = 0; i < players; i++)
            {
                var source = new ToneSource(sampleRate: 48_000, channels: 2, chunks: 10_000_000); // effectively infinite for the soak
                var backend = new CollectingBackend();
                var registry = MediaRegistry.Build(b => b.AddDecoder(new FixedDecoderProvider(source)));
                var player = MediaPlayer.OpenAudio(registry, backend, $"file:///tone{i}.wav");
                player.Play();
                running.Add((player, backend));
            }

            // Every player must reach non-zero output within the window — i.e. none is starved of scheduling.
            var allScheduled = SpinWait.SpinUntil(
                () => running.All(r => r.Backend.Output.NonZeroSamples > 0),
                TimeSpan.FromSeconds(15));

            var peakThreads = Process.GetCurrentProcess().Threads.Count;
            var progressed = running.Count(r => r.Backend.Output.NonZeroSamples > 0);
            output.WriteLine(
                $"TIME-01 soak: {players} simultaneous players, {progressed}/{players} producing audio, " +
                $"process threads {startThreads} → {peakThreads} (~{peakThreads - startThreads} added, " +
                $"{(peakThreads - startThreads) / (double)players:0.0}/player)");

            Assert.True(allScheduled,
                $"only {progressed}/{players} simultaneous players stayed scheduled — the per-clip model starved a clip under load");
        }
        finally
        {
            foreach (var (player, _) in running)
                player.Dispose();
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

    /// <summary>Format-only test resampler. The test verifies graph negotiation and never starts playback.</summary>
    private sealed class TargetRateAudioSource(IAudioSource inner, int sampleRate) : IAudioSource
    {
        public AudioFormat Format { get; } = new(sampleRate, inner.Format.Channels);
        public bool IsExhausted => inner.IsExhausted;
        public int ReadInto(Span<float> destination) => inner.ReadInto(destination);
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
