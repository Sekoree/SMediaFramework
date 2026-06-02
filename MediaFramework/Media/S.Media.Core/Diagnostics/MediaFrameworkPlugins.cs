using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Diagnostics;

/// <summary>Wraps a leaf <see cref="IAudioOutput"/> for per-output adaptive rate (FFmpeg).</summary>
public delegate IAudioOutput AdaptiveRateOutputWrapper(
    AudioRouter router,
    IAudioOutput inner,
    string outputId,
    int maxRateDeltaHz);

/// <summary>
/// Process-wide plugin slots for optional framework backends (FFmpeg file decode, SkiaSharp images, etc.).
/// Populated by <c>MediaFrameworkRuntime</c> module hooks — typically
/// <c>.UseFFmpeg()</c> and <c>.UseSkiaSharpImages()</c>.
/// </summary>
public static class MediaFrameworkPlugins
{
    private static Func<IAudioSource, int, IAudioSource>? _audioResampleSourceWrapper;
    private static Func<IVideoCpuFrameConverter>? _videoCpuFrameConverterFactory;
    private static Func<PixelFormat, PixelFormat, int, int, bool>? _videoCpuFrameCanConvertProbe;
    private static Func<VideoFormat, IDeinterlacer>? _videoDeinterlacerFactory;

    /// <summary>Opens a file-backed <see cref="IAudioSource"/> (FFmpeg-backed when installed).</summary>
    public static Func<string, AudioSourceOpenOptions?, IAudioSource>? AudioSourceFileFactory { get; set; }

    /// <summary>Opens a stream-backed <see cref="IAudioSource"/> (container audio track when installed).</summary>
    public static Func<Stream, AudioSourceOpenOptions?, IAudioSource>? AudioSourceStreamFactory { get; set; }

    /// <summary>Opens a file-backed <see cref="IVideoSource"/> (container video track when installed).</summary>
    public static Func<string, VideoSourceOpenOptions?, IVideoSource>? VideoSourceFileFactory { get; set; }

    /// <summary>Opens a stream-backed <see cref="IVideoSource"/> (container video track when installed).</summary>
    public static Func<Stream, VideoSourceOpenOptions?, IVideoSource>? VideoSourceStreamFactory { get; set; }

    /// <summary>Opens a still image as <see cref="IVideoSource"/> (SkiaSharp-backed when installed).</summary>
    public static Func<string, IVideoSource>? ImageFileSourceFactory { get; set; }

    /// <summary>Opens a still image stream as <see cref="IVideoSource"/> (SkiaSharp-backed when installed).</summary>
    public static Func<Stream, IVideoSource>? ImageStreamSourceFactory { get; set; }

    /// <summary>
    /// Factory: <c>(innerSource, targetSampleRate) =&gt; wrappedSource</c> for router auto-resample.
    /// </summary>
    public static Func<IAudioSource, int, IAudioSource>? AudioResampleSourceWrapper
    {
        get => _audioResampleSourceWrapper;
        set => _audioResampleSourceWrapper = value;
    }

    /// <summary>CPU pixel converter factory (swscale-backed when FFmpeg is installed).</summary>
    public static Func<IVideoCpuFrameConverter>? VideoCpuFrameConverterFactory
    {
        get => _videoCpuFrameConverterFactory;
        set => _videoCpuFrameConverterFactory = value;
    }

    /// <summary>CPU pixel convert probe (installed with FFmpeg).</summary>
    public static Func<PixelFormat, PixelFormat, int, int, bool>? VideoCpuFrameCanConvertProbe
    {
        get => _videoCpuFrameCanConvertProbe;
        set => _videoCpuFrameCanConvertProbe = value;
    }

    /// <summary>Deinterlacer factory (yadif-backed when FFmpeg is installed).</summary>
    public static Func<VideoFormat, IDeinterlacer>? VideoDeinterlacerFactory
    {
        get => _videoDeinterlacerFactory;
        set => _videoDeinterlacerFactory = value;
    }

    /// <summary>Registered by FFmpeg to support <see cref="AudioRouter.EnableAdaptiveRateOnNonMasterOutputs"/>.</summary>
    public static AdaptiveRateOutputWrapper? WrapAdaptiveRateOutput { get; set; }

    /// <summary>
    /// Captures all process-wide plugin/default slots and restores them when the returned scope is disposed.
    /// Use this around tests or short-lived host customization so static defaults do not leak across sessions.
    /// </summary>
    public static IDisposable PreserveDefaults() => new DefaultsScope();

    private sealed class DefaultsScope : IDisposable
    {
        private readonly Func<string, AudioSourceOpenOptions?, IAudioSource>? _audioSourceFileFactory = AudioSourceFileFactory;
        private readonly Func<Stream, AudioSourceOpenOptions?, IAudioSource>? _audioSourceStreamFactory = AudioSourceStreamFactory;
        private readonly Func<string, VideoSourceOpenOptions?, IVideoSource>? _videoSourceFileFactory = VideoSourceFileFactory;
        private readonly Func<Stream, VideoSourceOpenOptions?, IVideoSource>? _videoSourceStreamFactory = VideoSourceStreamFactory;
        private readonly Func<string, IVideoSource>? _imageFileSourceFactory = ImageFileSourceFactory;
        private readonly Func<Stream, IVideoSource>? _imageStreamSourceFactory = ImageStreamSourceFactory;
        private readonly Func<IAudioSource, int, IAudioSource>? _audioResampleSourceWrapper = AudioResampleSourceWrapper;
        private readonly Func<IVideoCpuFrameConverter>? _videoCpuFrameConverterFactory = VideoCpuFrameConverterFactory;
        private readonly Func<PixelFormat, PixelFormat, int, int, bool>? _videoCpuFrameCanConvertProbe = VideoCpuFrameCanConvertProbe;
        private readonly Func<VideoFormat, IDeinterlacer>? _videoDeinterlacerFactory = VideoDeinterlacerFactory;
        private readonly AdaptiveRateOutputWrapper? _wrapAdaptiveRateOutput = WrapAdaptiveRateOutput;
        private readonly bool _defaultAutoResample = AudioRouter.DefaultAutoResample;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            AudioSourceFileFactory = _audioSourceFileFactory;
            AudioSourceStreamFactory = _audioSourceStreamFactory;
            VideoSourceFileFactory = _videoSourceFileFactory;
            VideoSourceStreamFactory = _videoSourceStreamFactory;
            ImageFileSourceFactory = _imageFileSourceFactory;
            ImageStreamSourceFactory = _imageStreamSourceFactory;
            AudioResampleSourceWrapper = _audioResampleSourceWrapper;
            VideoCpuFrameConverterFactory = _videoCpuFrameConverterFactory;
            VideoCpuFrameCanConvertProbe = _videoCpuFrameCanConvertProbe;
            VideoDeinterlacerFactory = _videoDeinterlacerFactory;
            WrapAdaptiveRateOutput = _wrapAdaptiveRateOutput;
            AudioRouter.DefaultAutoResample = _defaultAutoResample;
        }
    }
}
