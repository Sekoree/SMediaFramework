using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Registry;

/// <summary>
/// Build-time registration surface (AOT-pure). Modules contribute capabilities here; the result is an
/// immutable <see cref="IMediaRegistry"/>. Replaces the static <c>MediaFrameworkPlugins</c> slots (P2).
/// Compositor (layer surfaces) and control (decoders/profiles) add their own scoped builders rather than
/// putting GL/control types in Core (05, OQ10).
/// </summary>
public interface IMediaRegistryBuilder
{
    /// <summary>Registers a URI-scheme decoder provider (D2/D3).</summary>
    IMediaRegistryBuilder AddDecoder(IMediaDecoderProvider provider);

    /// <summary>Registers an audio host backend (PortAudio, miniaudio, …).</summary>
    IMediaRegistryBuilder AddAudioBackend(IAudioBackend backend);

    /// <summary>Registers a still-image source for a file extension (e.g. <c>.png</c>).</summary>
    IMediaRegistryBuilder AddImageSource(string extension, Func<string, IVideoSource> factory);

    /// <summary>Sets the CPU pixel-format converter factory (swscale-backed when FFmpeg is registered).</summary>
    IMediaRegistryBuilder SetCpuConverterFactory(Func<IVideoCpuFrameConverter> factory);

    /// <summary>Sets the audio resample-source factory <c>(inner, targetSampleRate) =&gt; wrapped</c>.</summary>
    IMediaRegistryBuilder SetResamplerFactory(Func<IAudioSource, int, IAudioSource> factory);

    /// <summary>Sets the fixed-rate output adapter factory. The returned output advertises
    /// <paramref name="routerFormat"/> to an audio router and resamples into the inner output's format.</summary>
    IMediaRegistryBuilder SetResamplingOutputFactory(Func<IAudioOutput, AudioFormat, IAudioOutput> factory) =>
        throw new NotSupportedException("This registry builder does not support audio output resamplers.");

    /// <summary>Sets the adaptive-rate output-wrapper factory (FFmpeg-backed) the router uses to
    /// drift-correct non-master audio outputs.</summary>
    IMediaRegistryBuilder SetAdaptiveRateOutputFactory(AdaptiveRateOutputFactory factory);

    /// <summary>Sets the deinterlacer factory (yadif when FFmpeg is registered; else the built-in Bob).</summary>
    IMediaRegistryBuilder SetDeinterlacerFactory(Func<VideoFormat, IDeinterlacer> factory);

    /// <summary>Applies a module's registrations. Fluent so a composition root reads as a list of modules.</summary>
    IMediaRegistryBuilder Use(IMediaModule module);

    /// <summary>Registers a disposable lifetime (e.g. a native runtime lease) that the built
    /// <see cref="IMediaRegistry"/> owns and disposes when the registry is disposed - so a module can acquire a
    /// process/native resource at registration and release it deterministically with the registry instead of
    /// leaking it (NXT-05). Lifetimes are disposed in reverse registration order.</summary>
    IMediaRegistryBuilder AddLifetime(IDisposable lifetime);

    // Presenter (SDL3/Avalonia/NDI-out) and subtitle registration arrive with those modules in Phase 3/6.
}
