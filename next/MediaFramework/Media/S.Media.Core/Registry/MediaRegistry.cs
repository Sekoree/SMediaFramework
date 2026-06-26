using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Registry;

/// <summary>Mutable builder that accumulates module registrations, then freezes into a <see cref="MediaRegistry"/>.</summary>
public sealed class MediaRegistryBuilder : IMediaRegistryBuilder
{
    internal readonly List<IMediaDecoderProvider> DecoderList = [];
    internal readonly List<IAudioBackend> AudioBackendList = [];
    internal readonly Dictionary<string, Func<string, IVideoSource>> ImageFactories = new(StringComparer.OrdinalIgnoreCase);
    internal Func<IVideoCpuFrameConverter>? CpuConverterFactory;
    internal Func<IAudioSource, int, IAudioSource>? ResamplerFactory;
    internal AdaptiveRateOutputFactory? AdaptiveRateFactory;
    internal Func<VideoFormat, IDeinterlacer>? DeinterlacerFactory;

    public IMediaRegistryBuilder AddDecoder(IMediaDecoderProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        DecoderList.Add(provider);
        return this;
    }

    public IMediaRegistryBuilder AddAudioBackend(IAudioBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        AudioBackendList.Add(backend);
        return this;
    }

    public IMediaRegistryBuilder AddImageSource(string extension, Func<string, IVideoSource> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentNullException.ThrowIfNull(factory);
        ImageFactories[NormalizeExtension(extension)] = factory;
        return this;
    }

    public IMediaRegistryBuilder SetCpuConverterFactory(Func<IVideoCpuFrameConverter> factory)
    {
        CpuConverterFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IMediaRegistryBuilder SetResamplerFactory(Func<IAudioSource, int, IAudioSource> factory)
    {
        ResamplerFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IMediaRegistryBuilder SetAdaptiveRateOutputFactory(AdaptiveRateOutputFactory factory)
    {
        AdaptiveRateFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IMediaRegistryBuilder SetDeinterlacerFactory(Func<VideoFormat, IDeinterlacer> factory)
    {
        DeinterlacerFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IMediaRegistryBuilder Use(IMediaModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.Register(this);
        return this;
    }

    internal static string NormalizeExtension(string extension)
    {
        var ext = extension.Trim();
        if (!ext.StartsWith('.'))
            ext = "." + ext;
        return ext.ToLowerInvariant();
    }
}

/// <summary>
/// Immutable capability registry (see <see cref="IMediaRegistry"/>). Build it once at the composition
/// root and inject it. No process-wide mutable state — two registries with different capabilities can
/// coexist in one process, and tests build a registry per case (replaces the old PreserveDefaults hack).
/// </summary>
public sealed class MediaRegistry : IMediaRegistry
{
    private readonly IReadOnlyList<IMediaDecoderProvider> _decoders;
    private readonly Dictionary<string, Func<string, IVideoSource>> _imageFactories;
    private readonly Func<IVideoCpuFrameConverter>? _cpuConverter;
    private readonly Func<IAudioSource, int, IAudioSource>? _resampler;
    private readonly AdaptiveRateOutputFactory? _adaptiveRate;
    private readonly Func<VideoFormat, IDeinterlacer>? _deinterlacer;

    public IReadOnlyList<IAudioBackend> AudioBackends { get; }

    public IReadOnlyList<IMediaDecoderProvider> Decoders => _decoders;

    private MediaRegistry(MediaRegistryBuilder b)
    {
        _decoders = [.. b.DecoderList];
        AudioBackends = [.. b.AudioBackendList];
        _imageFactories = new Dictionary<string, Func<string, IVideoSource>>(b.ImageFactories, StringComparer.OrdinalIgnoreCase);
        _cpuConverter = b.CpuConverterFactory;
        _resampler = b.ResamplerFactory;
        _adaptiveRate = b.AdaptiveRateFactory;
        _deinterlacer = b.DeinterlacerFactory;
    }

    /// <summary>Builds an immutable registry from a configuration callback (the composition root).</summary>
    public static MediaRegistry Build(Action<IMediaRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new MediaRegistryBuilder();
        configure(builder);
        return new MediaRegistry(builder);
    }

    // D3: highest confidence wins; ties go to the earliest-registered provider. Iterating in registration
    // order and replacing only on a strictly-greater score yields exactly that.
    private IMediaDecoderProvider? PickDecoder(string uri, MediaKind kind)
    {
        IMediaDecoderProvider? best = null;
        var bestScore = 0.0;
        foreach (var d in _decoders)
        {
            var score = d.Probe(uri, kind);
            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }

        return best;
    }

    public bool CanOpen(string uri, MediaKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        return PickDecoder(uri, kind) is not null;
    }

    public bool TryOpenVideo(string uri, VideoSourceOpenOptions? options, [MaybeNullWhen(false)] out IVideoSource source)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        var provider = PickDecoder(uri, MediaKind.Video);
        if (provider is null)
        {
            source = null;
            return false;
        }

        source = provider.OpenVideo(uri, options);
        return true;
    }

    public bool TryOpenAudio(string uri, AudioSourceOpenOptions? options, [MaybeNullWhen(false)] out IAudioSource source)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        var provider = PickDecoder(uri, MediaKind.Audio);
        if (provider is null)
        {
            source = null;
            return false;
        }

        source = provider.OpenAudio(uri, options);
        return true;
    }

    public IMediaDecoderProvider? FindDecoder(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var d in _decoders)
            if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    public bool TryOpenVideo(string uri, VideoSourceOpenOptions? options, string providerName, [MaybeNullWhen(false)] out IVideoSource source)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        var provider = FindDecoder(providerName);
        if (provider is null)
        {
            source = null;
            return false;
        }

        source = provider.OpenVideo(uri, options);   // pinned: bypass confidence selection (D3)
        return true;
    }

    public bool TryOpenAudio(string uri, AudioSourceOpenOptions? options, string providerName, [MaybeNullWhen(false)] out IAudioSource source)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        var provider = FindDecoder(providerName);
        if (provider is null)
        {
            source = null;
            return false;
        }

        source = provider.OpenAudio(uri, options);   // pinned: bypass confidence selection (D3)
        return true;
    }

    public bool TryOpenImage(string path, [MaybeNullWhen(false)] out IVideoSource source)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var ext = MediaRegistryBuilder.NormalizeExtension(Path.GetExtension(path));
        if (_imageFactories.TryGetValue(ext, out var factory))
        {
            source = factory(path);
            return true;
        }

        source = null;
        return false;
    }

    public IVideoCpuFrameConverter? CreateCpuConverter() => _cpuConverter?.Invoke();

    public IAudioSource? CreateResampler(IAudioSource source, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _resampler?.Invoke(source, targetSampleRate);
    }

    public bool SupportsAdaptiveRateOutput => _adaptiveRate is not null;

    public IAudioOutput? CreateAdaptiveRateOutput(IAudioOutput inner, Func<double> playbackPpmBias, int maxRateDeltaHz, IDisposable? biasSource)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(playbackPpmBias);
        return _adaptiveRate?.Invoke(inner, playbackPpmBias, maxRateDeltaHz, biasSource);
    }

    public IDeinterlacer CreateDeinterlacer(VideoFormat input) =>
        _deinterlacer?.Invoke(input) ?? new BobDeinterlacer(input);
}
