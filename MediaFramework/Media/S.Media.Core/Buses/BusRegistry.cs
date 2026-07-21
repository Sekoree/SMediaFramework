using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Video;
using S.Media.Core.Video.Effects;

namespace S.Media.Core.Buses;

/// <summary>Instance parameters for a visual source (visualizer): the video format it should produce
/// and an opaque config blob (preset directory, sensitivity, … - the kind defines the schema).</summary>
public sealed record VisualSourceCreateArgs(
    int Width,
    int Height,
    Rational FrameRate,
    string? ConfigJson = null);

/// <summary>
/// Bus-capability registration (mirrors <c>CompositorRegistry</c>'s kind→factory shape, Doc 05): modules
/// register audio/video effects and audio-visual sources <em>by kind</em>; the UI enumerates the kinds
/// for its insertion menus. Built once at the composition root and injected - no global state.
/// </summary>
public interface IBusRegistryBuilder
{
    /// <summary>Register an audio effect factory (case-insensitive kind; later registration replaces).</summary>
    IBusRegistryBuilder AddAudioEffect(string kind, Func<string?, IAudioBusEffect> factory);

    /// <summary>Register a video effect factory.</summary>
    IBusRegistryBuilder AddVideoEffect(string kind, Func<string?, IVideoBusEffect> factory);

    /// <summary>Register an audio-in → video-out generator (visualizer) factory.</summary>
    IBusRegistryBuilder AddVisualSource(string kind, Func<VisualSourceCreateArgs, IAudioVisualSource> factory);

    /// <summary>Register a per-LAYER video effect factory (the compositor's GPU fragment chain with
    /// optional CPU fallback - see <see cref="VideoLayerEffect"/>). A distinct stage from
    /// <see cref="AddVideoEffect"/>'s per-output CPU bus effects: layer effects run inside the
    /// composite pass before blending.</summary>
    IBusRegistryBuilder AddLayerEffect(string kind, Func<string?, VideoLayerEffect> factory);
}

/// <summary>Immutable resolved bus capabilities.</summary>
public interface IBusRegistry
{
    IReadOnlyCollection<string> AudioEffectKinds { get; }

    IReadOnlyCollection<string> VideoEffectKinds { get; }

    IReadOnlyCollection<string> VisualSourceKinds { get; }

    IReadOnlyCollection<string> LayerEffectKinds { get; }

    bool TryCreateAudioEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IAudioBusEffect effect);

    bool TryCreateVideoEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IVideoBusEffect effect);

    bool TryCreateVisualSource(string kind, VisualSourceCreateArgs args, [MaybeNullWhen(false)] out IAudioVisualSource source);

    bool TryCreateLayerEffect(string kind, string? configJson, [MaybeNullWhen(false)] out VideoLayerEffect effect);
}

/// <summary>Mutable builder for an <see cref="IBusRegistry"/>.</summary>
public sealed class BusRegistryBuilder : IBusRegistryBuilder
{
    private readonly Dictionary<string, Func<string?, IAudioBusEffect>> _audio = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, IVideoBusEffect>> _video = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<VisualSourceCreateArgs, IAudioVisualSource>> _visual = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, VideoLayerEffect>> _layer = new(StringComparer.OrdinalIgnoreCase);

    public IBusRegistryBuilder AddAudioEffect(string kind, Func<string?, IAudioBusEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _audio[kind] = factory;
        return this;
    }

    public IBusRegistryBuilder AddVideoEffect(string kind, Func<string?, IVideoBusEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _video[kind] = factory;
        return this;
    }

    public IBusRegistryBuilder AddVisualSource(string kind, Func<VisualSourceCreateArgs, IAudioVisualSource> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _visual[kind] = factory;
        return this;
    }

    public IBusRegistryBuilder AddLayerEffect(string kind, Func<string?, VideoLayerEffect> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _layer[kind] = factory;
        return this;
    }

    public IBusRegistry Build() => new BusRegistry(_audio, _video, _visual, _layer);

    public static IBusRegistry Build(Action<IBusRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new BusRegistryBuilder();
        configure(builder);
        return builder.Build();
    }
}

internal sealed class BusRegistry(
    Dictionary<string, Func<string?, IAudioBusEffect>> audio,
    Dictionary<string, Func<string?, IVideoBusEffect>> video,
    Dictionary<string, Func<VisualSourceCreateArgs, IAudioVisualSource>> visual,
    Dictionary<string, Func<string?, VideoLayerEffect>> layer) : IBusRegistry
{
    private readonly Dictionary<string, Func<string?, IAudioBusEffect>> _audio =
        new(audio, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, IVideoBusEffect>> _video =
        new(video, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<VisualSourceCreateArgs, IAudioVisualSource>> _visual =
        new(visual, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<string?, VideoLayerEffect>> _layer =
        new(layer, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> AudioEffectKinds => _audio.Keys;

    public IReadOnlyCollection<string> VideoEffectKinds => _video.Keys;

    public IReadOnlyCollection<string> VisualSourceKinds => _visual.Keys;

    public IReadOnlyCollection<string> LayerEffectKinds => _layer.Keys;

    public bool TryCreateAudioEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IAudioBusEffect effect)
    {
        effect = null;
        if (!_audio.TryGetValue(kind, out var factory))
            return false;
        try { effect = factory(configJson); }
        catch { return false; }
        return effect is not null;
    }

    public bool TryCreateVideoEffect(string kind, string? configJson, [MaybeNullWhen(false)] out IVideoBusEffect effect)
    {
        effect = null;
        if (!_video.TryGetValue(kind, out var factory))
            return false;
        try { effect = factory(configJson); }
        catch { return false; }
        return effect is not null;
    }

    public bool TryCreateVisualSource(string kind, VisualSourceCreateArgs args, [MaybeNullWhen(false)] out IAudioVisualSource source)
    {
        source = null;
        if (!_visual.TryGetValue(kind, out var factory))
            return false;
        try { source = factory(args); }
        catch { return false; }
        return source is not null;
    }

    public bool TryCreateLayerEffect(string kind, string? configJson, [MaybeNullWhen(false)] out VideoLayerEffect effect)
    {
        effect = null;
        if (!_layer.TryGetValue(kind, out var factory))
            return false;
        try { effect = factory(configJson); }
        catch { return false; }
        return effect is not null;
    }
}
