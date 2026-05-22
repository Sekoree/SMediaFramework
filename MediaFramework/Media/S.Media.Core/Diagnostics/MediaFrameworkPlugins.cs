using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Diagnostics;

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
}
