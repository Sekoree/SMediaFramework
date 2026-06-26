using S.Media.Decode.FFmpeg.Audio;
using S.Media.Decode.FFmpeg.Video;

namespace S.Media.Decode.FFmpeg;

/// <summary>
/// Registers FFmpeg decode capabilities into the media registry — the AOT-pure replacement for the old
/// static <c>MediaFrameworkPlugins</c> slots (P2). Contributes a URI decoder provider plus the swscale
/// CPU converter, swresample source-resampler, yadif/Bob deinterlacer, and the swresample adaptive-rate
/// output-wrapper factories.
/// </summary>
public sealed class FFmpegModule : IMediaModule
{
    public string Name => "FFmpeg";

    public void Register(IMediaRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        FFmpegRuntime.EnsureInitialized();

        builder
            .AddDecoder(new FFmpegDecoderProvider())
            .SetCpuConverterFactory(static () => new VideoCpuFrameConverter())
            .SetResamplerFactory(static (inner, targetRate) =>
                ResamplingAudioSource.Create(inner, targetRate, disposeInnerWhenDisposed: false))
            .SetAdaptiveRateOutputFactory(static (inner, bias, maxDeltaHz, biasSource) =>
                new AdaptiveRateAudioOutput(inner, bias, maxDeltaHz, biasSource))
            .SetDeinterlacerFactory(static input =>
                input.PixelFormat is PixelFormat.I420 or PixelFormat.Nv12
                    or PixelFormat.Yuv422P or PixelFormat.Yuv444P
                    ? new YadifDeinterlacer(input)
                    : (IDeinterlacer)new BobDeinterlacer(input));
    }
}

/// <summary>
/// FFmpeg's general-purpose container decoder, exposed through the registry (D2/D3). Handles
/// <c>file:</c> / <c>http(s):</c> URIs and bare paths at moderate confidence so a more-specific provider
/// (e.g. a dedicated capture/NDI plugin) can outrank it.
/// </summary>
internal sealed class FFmpegDecoderProvider : IMediaDecoderProvider
{
    public string Name => "FFmpeg";

    public double Probe(string uri, MediaKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        // Live/image schemes belong to other providers; FFmpeg takes files, common network media
        // protocols, and bare paths. Unknown schemes are left for explicit providers.
        return SchemeOf(uri) switch
        {
            "" or "file" or "http" or "https" or "rtsp" or "rtmp" => 0.5,
            _ => 0.0,
        };
    }

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
    {
        var container = TryCreateAbsoluteMediaUri(uri, out var parsed)
            ? MediaContainerDecoder.OpenUri(parsed, MapStandaloneVideo(options))
            : MediaContainerDecoder.Open(uri, MapStandaloneVideo(options));
        return new ContainerOwnedVideoSource(container);
    }

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        TryCreateAbsoluteMediaUri(uri, out var parsed)
            ? AudioFileDecoder.OpenUri(parsed, MapAudio(options))
            : AudioFileDecoder.Open(uri, MapAudio(options));

    /// <summary>Known URI scheme, lowercased; empty for a bare path.</summary>
    private static string SchemeOf(string uri)
    {
        return TryCreateAbsoluteMediaUri(uri, out var parsed) ? parsed.Scheme.ToLowerInvariant() : string.Empty;
    }

    private static bool TryCreateAbsoluteMediaUri(string uri, out Uri parsed)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out parsed!)
            && parsed.Scheme.Length > 1) // Avoid treating Windows drive paths like C:\media.mp4 as a URI.
            return true;

        parsed = null!;
        return false;
    }

    private static VideoDecoderOpenOptions MapStandaloneVideo(VideoSourceOpenOptions? o) =>
        new()
        {
            TryHardwareAcceleration = o?.TryHardwareAcceleration ?? true,
            RetainDmabufForGl = o?.RetainDmabufForGl ?? false,
            RetainD3D11SharedHandleForGl = o?.RetainD3D11SharedHandleForGl ?? false,
            Win32Nv12SharedHandleOnlyExport = o?.Win32Nv12SharedHandleOnlyExport ?? false,
            AudioPacketQueueDepth = o?.AudioPacketQueueDepth ?? 0,
            VideoPacketQueueDepth = o?.VideoPacketQueueDepth ?? 0,
            FileReadBufferBytes = o?.FileReadBufferBytes ?? 0,
            // The registry OpenVideo contract returns only an IVideoSource. If the shared demux also opens
            // audio here, its packet queue has no consumer and can eventually block video decode.
            AudioStreamIndex = MediaStreamSelection.Disabled,
            VideoStreamIndex = o?.VideoStreamIndex,
        };

    private static AudioFileDecoderOpenOptions MapAudio(AudioSourceOpenOptions? o) =>
        o is null
            ? default
            : new AudioFileDecoderOpenOptions
            {
                CodecThreadCount = o.CodecThreadCount,
                AudioStreamIndex = o.AudioStreamIndex,
            };
}

/// <summary>Owns a <see cref="MediaContainerDecoder"/> and exposes its video track as an <see cref="IVideoSource"/>.</summary>
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
        if (_disposed)
            return;
        _disposed = true;
        _container.Dispose();
    }
}
