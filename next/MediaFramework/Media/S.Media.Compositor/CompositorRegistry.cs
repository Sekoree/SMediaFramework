using System.Diagnostics.CodeAnalysis;

namespace S.Media.Compositor;

/// <summary>
/// Compositor-owned registration extension (Doc 05): higher layers and native (<c>S.Abi</c>) plugins add
/// custom GL layer-surface factories <em>by kind</em> without Core ever depending on the compositor. Built
/// once at the composition root and injected — no process-wide mutable state (P2/D6).
/// </summary>
public interface ICompositorRegistryBuilder
{
    /// <summary>Register a factory for layer surfaces of <paramref name="kind"/> (case-insensitive). A
    /// later registration of the same kind replaces the earlier one.</summary>
    ICompositorRegistryBuilder AddLayerSurface(string kind, Func<IVideoCompositorLayerSurface> factory);

    /// <summary>Register a <em>config-aware</em> factory for layer surfaces of <paramref name="kind"/>. The
    /// opaque <c>configJson</c> blob comes verbatim from the cue/composition layer spec (e.g. the MMD models +
    /// motion), letting one kind mint differently-configured instances. Mirrors the ABI's
    /// <c>MfpLayerSurfaceFactoryVTable.create(config_json)</c>.</summary>
    ICompositorRegistryBuilder AddLayerSurface(string kind, Func<string?, IVideoCompositorLayerSurface> factory);
}

/// <summary>Immutable resolved compositor capabilities — the layer-surface factories registered by kind.</summary>
public interface ICompositorRegistry
{
    /// <summary>The registered layer-surface kinds.</summary>
    IReadOnlyCollection<string> LayerSurfaceKinds { get; }

    /// <summary>Creates a new (unconfigured) layer surface for <paramref name="kind"/>, or <c>false</c> if none registered.</summary>
    bool TryCreateLayerSurface(string kind, [MaybeNullWhen(false)] out IVideoCompositorLayerSurface surface);

    /// <summary>Creates a layer surface for <paramref name="kind"/> configured by the opaque
    /// <paramref name="configJson"/> blob, or <c>false</c> if none registered.</summary>
    bool TryCreateLayerSurface(string kind, string? configJson, [MaybeNullWhen(false)] out IVideoCompositorLayerSurface surface);
}

/// <summary>Mutable builder for an <see cref="ICompositorRegistry"/>.</summary>
public sealed class CompositorRegistryBuilder : ICompositorRegistryBuilder
{
    private readonly Dictionary<string, Func<string?, IVideoCompositorLayerSurface>> _surfaces =
        new(StringComparer.OrdinalIgnoreCase);

    public ICompositorRegistryBuilder AddLayerSurface(string kind, Func<IVideoCompositorLayerSurface> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return AddLayerSurface(kind, _ => factory());
    }

    public ICompositorRegistryBuilder AddLayerSurface(string kind, Func<string?, IVideoCompositorLayerSurface> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(factory);
        _surfaces[kind] = factory;
        return this;
    }

    /// <summary>Freezes the registered factories into an immutable registry.</summary>
    public ICompositorRegistry Build() => new CompositorRegistry(_surfaces);

    /// <summary>Builds a registry from a configuration callback.</summary>
    public static ICompositorRegistry Build(Action<ICompositorRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new CompositorRegistryBuilder();
        configure(builder);
        return builder.Build();
    }
}

internal sealed class CompositorRegistry : ICompositorRegistry
{
    private readonly Dictionary<string, Func<string?, IVideoCompositorLayerSurface>> _surfaces;

    public CompositorRegistry(Dictionary<string, Func<string?, IVideoCompositorLayerSurface>> surfaces) =>
        _surfaces = new Dictionary<string, Func<string?, IVideoCompositorLayerSurface>>(surfaces, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> LayerSurfaceKinds => _surfaces.Keys;

    public bool TryCreateLayerSurface(string kind, [MaybeNullWhen(false)] out IVideoCompositorLayerSurface surface) =>
        TryCreateLayerSurface(kind, null, out surface);

    public bool TryCreateLayerSurface(string kind, string? configJson, [MaybeNullWhen(false)] out IVideoCompositorLayerSurface surface)
    {
        if (kind is not null && _surfaces.TryGetValue(kind, out var factory))
        {
            surface = factory(configJson);
            return true;
        }

        surface = null;
        return false;
    }
}
