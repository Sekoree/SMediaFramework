using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Media.Players;

/// <summary>
/// Registry-driven open builder for one media object (P2 — opens through <see cref="IMediaRegistry"/>, no
/// globals). The registry is injected at builder creation, so the cue/session layer keeps its
/// <c>OpenFile(...).WithOptions(...).Build()</c> ergonomics without a concrete decoder dependency.
/// </summary>
public abstract class MediaPlayerOpenBuilder
{
    private protected MediaPlayerOpenOptions OpenOptions = MediaPlayerOpenOptions.Default;
    private protected IVideoOutput? VideoLead;
    private protected bool DisposeVideoLeadOnPlayerDispose;

    /// <summary>The options currently configured on this builder.</summary>
    public MediaPlayerOpenOptions CurrentOptions => OpenOptions;

    /// <summary>Builds the player; returns <see langword="false"/> instead of throwing on open/wiring failure.</summary>
    public abstract bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error);

    /// <summary>Builds the player or throws <see cref="InvalidOperationException"/> with the open failure.</summary>
    public MediaPlayer Build() =>
        TryBuild(out var player, out var error)
            ? player
            : throw new InvalidOperationException(error ?? "MediaPlayer open failed.");
}

/// <summary>Opens a file path or URI through the registry (the provider selects on the URI scheme — D2).</summary>
public sealed class MediaPlayerOpenFileBuilder : MediaPlayerOpenBuilder
{
    private readonly IMediaRegistry _registry;

    internal MediaPlayerOpenFileBuilder(IMediaRegistry registry, string filePathOrUri)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Uri = filePathOrUri ?? throw new ArgumentNullException(nameof(filePathOrUri));
    }

    /// <summary>The file path or URI being opened.</summary>
    public string Uri { get; }

    public MediaPlayerOpenFileBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        OpenOptions = options;
        return this;
    }

    public MediaPlayerOpenFileBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        OpenOptions = configure(OpenOptions);
        return this;
    }

    /// <summary>Uses <paramref name="output"/> as the video negotiation lead (local display/preview output).</summary>
    public MediaPlayerOpenFileBuilder WithVideoLead(IVideoOutput output, bool disposeOnPlayerDispose = false)
    {
        VideoLead = output ?? throw new ArgumentNullException(nameof(output));
        DisposeVideoLeadOnPlayerDispose = disposeOnPlayerDispose;
        return this;
    }

    public override bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error) =>
        MediaPlayer.TryOpen(_registry, Uri, OpenOptions, VideoLead, out player, out error);
}
