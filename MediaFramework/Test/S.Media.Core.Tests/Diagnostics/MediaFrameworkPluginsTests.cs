using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Diagnostics;

[CollectionDefinition("ProcessWideMediaFrameworkPlugins", DisableParallelization = true)]
public sealed class ProcessWideMediaFrameworkPluginsCollection
{
    public const string Name = "ProcessWideMediaFrameworkPlugins";
}

[Collection(ProcessWideMediaFrameworkPluginsCollection.Name)]
public sealed class MediaFrameworkPluginsTests
{
    [Fact]
    public void PreserveDefaults_RestoresProcessWidePluginSlots()
    {
        var originalConverter = MediaFrameworkPlugins.VideoCpuFrameConverterFactory;
        var originalProbe = MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe;
        var originalResampler = MediaFrameworkPlugins.AudioResampleSourceWrapper;
        var originalAutoResample = AudioRouter.DefaultAutoResample;

        var converter = () => new NoopConverter();
        Func<PixelFormat, PixelFormat, int, int, bool> probe = (_, _, _, _) => true;
        Func<IAudioSource, int, IAudioSource> resampler = (source, _) => source;

        IDisposable scope;
        using (scope = MediaFrameworkPlugins.PreserveDefaults())
        {
            MediaFrameworkPlugins.VideoCpuFrameConverterFactory = converter;
            MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe = probe;
            MediaFrameworkPlugins.AudioResampleSourceWrapper = resampler;
            AudioRouter.DefaultAutoResample = !originalAutoResample;
        }

        scope.Dispose();

        Assert.Same(originalConverter, MediaFrameworkPlugins.VideoCpuFrameConverterFactory);
        Assert.Same(originalProbe, MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe);
        Assert.Same(originalResampler, MediaFrameworkPlugins.AudioResampleSourceWrapper);
        Assert.Equal(originalAutoResample, AudioRouter.DefaultAutoResample);
    }

    [Fact]
    public void SourceOpen_ScopedFactoriesOverrideProcessWideFactories()
    {
        using var _ = MediaFrameworkPlugins.PreserveDefaults();
        var processAudio = new TestAudioSource(new AudioFormat(48_000, 2));
        var scopedAudio = new TestAudioSource(new AudioFormat(44_100, 1));
        var processVideo = new TestVideoSource(new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1)));
        var scopedVideo = new TestVideoSource(new VideoFormat(32, 32, PixelFormat.Rgba32, new Rational(60, 1)));
        var scopedImage = new TestVideoSource(new VideoFormat(8, 8, PixelFormat.Bgra32, new Rational(1, 1)));
        MediaFrameworkPlugins.AudioSourceFileFactory = (_, _) => processAudio;
        MediaFrameworkPlugins.AudioSourceStreamFactory = (_, _) => processAudio;
        MediaFrameworkPlugins.VideoSourceFileFactory = (_, _) => processVideo;
        MediaFrameworkPlugins.VideoSourceStreamFactory = (_, _) => processVideo;
        MediaFrameworkPlugins.ImageFileSourceFactory = _ => processVideo;
        MediaFrameworkPlugins.ImageStreamSourceFactory = _ => processVideo;
        using var stream = new MemoryStream([1, 2, 3]);
        using var imageStream = new MemoryStream([4, 5, 6]);

        var openedAudio = AudioSource.OpenFile("clip.wav", options: null, scopedFactory: (_, _) => scopedAudio);
        var openedAudioStream = AudioSource.OpenStream(stream, options: null, scopedFactory: (_, _) => scopedAudio);
        var openedVideo = VideoSource.OpenFile("clip.mp4", options: null, scopedFactory: (_, _) => scopedVideo);
        var openedVideoStream = VideoSource.OpenStream(stream, options: null, scopedFactory: (_, _) => scopedVideo);
        var openedImage = VideoSource.OpenImage("clip.unknown", scopedFactory: _ => scopedImage);
        var openedImageStream = VideoSource.OpenImage(imageStream, scopedFactory: _ => scopedImage);

        Assert.Same(scopedAudio, openedAudio);
        Assert.Same(scopedAudio, openedAudioStream);
        Assert.Same(scopedVideo, openedVideo);
        Assert.Same(scopedVideo, openedVideoStream);
        Assert.Same(scopedImage, openedImage);
        Assert.Same(scopedImage, openedImageStream);
        Assert.Same(processAudio, AudioSource.OpenFile("clip.wav"));
        Assert.Same(processVideo, VideoSource.OpenFile("clip.mp4"));
    }

    [Fact]
    public void DeinterlacerCreate_UsesScopedThenProcessThenBuiltInFallback()
    {
        using var _ = MediaFrameworkPlugins.PreserveDefaults();
        var format = new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        var process = new TestDeinterlacer(format);
        var scoped = new TestDeinterlacer(format);
        MediaFrameworkPlugins.VideoDeinterlacerFactory = _ => process;

        using var fromScoped = VideoDeinterlacerRegistry.Create(format, _ => scoped);
        using var fromProcess = VideoDeinterlacerRegistry.Create(format);
        MediaFrameworkPlugins.VideoDeinterlacerFactory = null;
        using var fallback = VideoDeinterlacerRegistry.Create(format);

        Assert.Same(scoped, fromScoped);
        Assert.Same(process, fromProcess);
        Assert.IsType<BobDeinterlacer>(fallback);
    }

    private sealed class NoopConverter : IVideoCpuFrameConverter
    {
        public void Configure(PixelFormat src, PixelFormat dst, int width, int height) { }
        public VideoFrame Convert(VideoFrame source, VideoTransferHint hint) => source;
        public void Dispose() { }
    }

    private sealed class TestAudioSource(AudioFormat format) : IAudioSource
    {
        public AudioFormat Format { get; } = format;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> destination)
        {
            destination.Clear();
            return destination.Length;
        }
    }

    private sealed class TestVideoSource(VideoFormat format) : IVideoSource
    {
        public VideoFormat Format { get; private set; } = format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [format.PixelFormat];
        public bool IsExhausted => false;
        public void SelectOutputFormat(PixelFormat format) => Format = Format with { PixelFormat = format };
        public bool TryReadNextFrame(out VideoFrame frame)
        {
            frame = null!;
            return false;
        }
    }

    private sealed class TestDeinterlacer(VideoFormat format) : IDeinterlacer
    {
        public VideoFormat InputFormat { get; private set; } = format;
        public VideoFormat OutputFormat => InputFormat;
        public void Configure(VideoFormat input) => InputFormat = input;
        public int Process(VideoFrame frame, Span<VideoFrame?> outputs)
        {
            outputs[0] = frame;
            return 1;
        }
        public void Dispose() { }
    }
}
