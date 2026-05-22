using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Video;

namespace S.Media.FFmpeg;

internal static class MediaFrameworkPluginRegistration
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;

        FFmpegRuntime.EnsureInitialized();

        MediaFrameworkPlugins.AudioSourceFileFactory = (path, options) =>
        {
            var opt = MapAudioOptions(options);
            return AudioFileDecoder.Open(path, opt);
        };

        MediaFrameworkPlugins.AudioSourceStreamFactory = (stream, options) =>
        {
            var container = OpenContainerFromStream(stream, options?.StreamIsSeekable ?? stream.CanSeek,
                options?.StreamProbeHintName, options?.SpoolToDisk == true, videoOptions: null);
            return new ContainerOwnedAudioSource(container);
        };

        MediaFrameworkPlugins.VideoSourceFileFactory = (path, options) =>
        {
            var container = MediaContainerDecoder.Open(path, MapVideoOptions(options));
            return new ContainerOwnedVideoSource(container);
        };

        MediaFrameworkPlugins.VideoSourceStreamFactory = (stream, options) =>
        {
            var container = OpenContainerFromStream(stream, options?.StreamIsSeekable ?? stream.CanSeek,
                options?.StreamProbeHintName, options?.SpoolToDisk == true, MapVideoOptions(options));
            return new ContainerOwnedVideoSource(container);
        };
    }

    private static MediaContainerDecoder OpenContainerFromStream(
        Stream stream,
        bool isSeekable,
        string? probeHintName,
        bool spoolToDisk,
        VideoDecoderOpenOptions? videoOptions)
    {
        return spoolToDisk
            ? MediaContainerDecoder.OpenStreamSpooled(stream, probeHintName, videoOptions)
            : MediaContainerDecoder.OpenStream(stream, isSeekable, probeHintName, videoOptions);
    }

    private static AudioFileDecoderOpenOptions MapAudioOptions(AudioSourceOpenOptions? options)
    {
        if (options is null)
            return default;
        return new AudioFileDecoderOpenOptions
        {
            CodecThreadCount = options.CodecThreadCount,
        };
    }

    private static VideoDecoderOpenOptions? MapVideoOptions(VideoSourceOpenOptions? options)
    {
        if (options is null)
            return null;
        return new VideoDecoderOpenOptions
        {
            RetainDmabufForGl = options.RetainDmabufForGl,
            RetainD3D11SharedHandleForGl = options.RetainD3D11SharedHandleForGl,
        };
    }
}

/// <summary>Owns a <see cref="MediaContainerDecoder"/> and exposes its audio track.</summary>
internal sealed class ContainerOwnedAudioSource : IAudioSource, IDisposable
{
    private readonly MediaContainerDecoder _container;
    private readonly IAudioSource _inner;
    private bool _disposed;

    public ContainerOwnedAudioSource(MediaContainerDecoder container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _inner = container.Audio;
        Format = _inner.Format;
    }

    public AudioFormat Format { get; }

    public bool IsExhausted => _inner.IsExhausted;

    public int ReadInto(Span<float> dst) => _inner.ReadInto(dst);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _container.Dispose();
    }
}

/// <summary>Owns a <see cref="MediaContainerDecoder"/> and exposes its video track.</summary>
internal sealed class ContainerOwnedVideoSource : IVideoSource, IDisposable
{
    private readonly MediaContainerDecoder _container;
    private readonly IVideoSource _inner;
    private bool _disposed;

    public ContainerOwnedVideoSource(MediaContainerDecoder container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _inner = container.Video;
    }

    public VideoFormat Format => _inner.Format;

    public IReadOnlyList<PixelFormat> NativePixelFormats => _inner.NativePixelFormats;

    public bool IsExhausted => _inner.IsExhausted;

    public void SelectOutputFormat(PixelFormat format) => _inner.SelectOutputFormat(format);

    public bool TryReadNextFrame(out VideoFrame frame) => _inner.TryReadNextFrame(out frame);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _container.Dispose();
    }
}
