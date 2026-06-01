using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.Playback;

/// <summary>Shared fluent configuration for <see cref="MediaPlayer"/> open builders.</summary>
public abstract class MediaPlayerOpenBuilder
{
    private MediaPlayerOpenOptions _options = MediaPlayerOpenOptions.Default;
    private IVideoOutput? _videoLead;
    private bool _disposeVideoLead;
    private MediaPlayerDecoderOwnership _decoderOwnership = MediaPlayerDecoderOwnership.BundleDisposesDecoder;
    private IVideoSource? _videoSourceOverride;
    private bool _disposeLiveSources = true;
    private string? _streamInputName;

    internal MediaPlayerOpenOptions Options => _options;
    internal IVideoOutput? VideoLead => _videoLead;
    internal bool DisposeVideoLead => _disposeVideoLead;
    internal MediaPlayerDecoderOwnership DecoderOwnership => _decoderOwnership;
    internal IVideoSource? VideoSourceOverride => _videoSourceOverride;
    internal bool DisposeLiveSources => _disposeLiveSources;
    internal string? StreamInputName => _streamInputName;

    internal readonly List<Func<MediaPlayer, bool>> CompanionSteps = [];
    internal string? CompanionFailureMessage { get; set; }

    /// <summary>Set by optional package companions (e.g. PortAudio) after a successful build.</summary>
    internal object? WiredPortAudioHost { get; set; }

    public MediaPlayerOpenOptions CurrentOptions => _options;

    protected void ApplyOptions(MediaPlayerOpenOptions options) => _options = options;

    protected void ApplyOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure) =>
        _options = configure(_options);

    protected void ApplyVideoLead(IVideoOutput output, bool disposeOnPlayerDispose)
    {
        _videoLead = output;
        _disposeVideoLead = disposeOnPlayerDispose;
    }

    protected void ApplyDecoderOwnership(MediaPlayerDecoderOwnership ownership) =>
        _decoderOwnership = ownership;

    protected void ApplyStreamInputName(string? inputName) => _streamInputName = inputName;

    protected void ApplyDisposeLiveSources(bool dispose) => _disposeLiveSources = dispose;

    protected void ApplyVideoSourceOverride(IVideoSource? source) => _videoSourceOverride = source;

    public abstract bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error);

    public Task<MediaPlayer> OpenAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryBuild(out var player, out var error))
                throw new InvalidOperationException(error ?? "MediaPlayer open failed.");
            return player!;
        }, cancellationToken);

    protected bool TryBuildWithCompanions(
        bool opened,
        MediaPlayer? player,
        out MediaPlayer? result,
        out string? error)
    {
        result = player;
        error = null;
        if (!opened)
        {
            result = null;
            return false;
        }

        foreach (var step in CompanionSteps)
        {
            if (!step(player!))
            {
                error = CompanionFailureMessage ?? "MediaPlayer companion step failed.";
                player!.Dispose();
                result = null;
                return false;
            }
        }

        player!.AttachBuilderContext(this);
        result = player;
        return true;
    }
}

/// <summary>Opens a local media file path.</summary>
public sealed class MediaPlayerOpenFileBuilder(string filePath) : MediaPlayerOpenBuilder
{
    public string FilePath { get; } = filePath ?? throw new ArgumentNullException(nameof(filePath));

    public MediaPlayerOpenFileBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        ApplyOptions(options);
        return this;
    }

    public MediaPlayerOpenFileBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ApplyOptions(configure);
        return this;
    }

    public MediaPlayerOpenFileBuilder WithVideoLead(IVideoOutput output, bool disposeOnPlayerDispose = false)
    {
        ApplyVideoLead(output, disposeOnPlayerDispose);
        return this;
    }

    public MediaPlayerOpenFileBuilder WithDecoderOwnership(MediaPlayerDecoderOwnership ownership)
    {
        ApplyDecoderOwnership(ownership);
        return this;
    }

    public override bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error)
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            error = $"file not found: {FilePath}";
            player = null;
            return false;
        }

#pragma warning disable CS0618
        var opened = MediaPlayer.TryOpen(
            FilePath,
            Options,
            VideoLead,
            DisposeVideoLead,
            DecoderOwnership,
            out player,
            out error);
#pragma warning restore CS0618
        return TryBuildWithCompanions(opened, player, out player, out error);
    }
}

/// <summary>Opens a media URI (http, rtsp, file:, …).</summary>
public sealed class MediaPlayerOpenUriBuilder(Uri mediaUri) : MediaPlayerOpenBuilder
{
    public Uri MediaUri { get; } = mediaUri ?? throw new ArgumentNullException(nameof(mediaUri));

    public MediaPlayerOpenUriBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        ApplyOptions(options);
        return this;
    }

    public MediaPlayerOpenUriBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ApplyOptions(configure);
        return this;
    }

    public MediaPlayerOpenUriBuilder WithVideoLead(IVideoOutput output, bool disposeOnPlayerDispose = false)
    {
        ApplyVideoLead(output, disposeOnPlayerDispose);
        return this;
    }

    public MediaPlayerOpenUriBuilder WithDecoderOwnership(MediaPlayerDecoderOwnership ownership)
    {
        ApplyDecoderOwnership(ownership);
        return this;
    }

    public override bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error)
    {
#pragma warning disable CS0618
        var opened = MediaPlayer.TryOpenUri(
            MediaUri,
            Options,
            VideoLead,
            DisposeVideoLead,
            out player,
            out error);
#pragma warning restore CS0618
        return TryBuildWithCompanions(opened, player, out player, out error);
    }
}

/// <summary>Opens a finite readable stream (AVIO or spooled).</summary>
public sealed class MediaPlayerOpenStreamBuilder(Stream mediaStream) : MediaPlayerOpenBuilder
{
    public Stream MediaStream { get; } = mediaStream ?? throw new ArgumentNullException(nameof(mediaStream));

    public MediaPlayerOpenStreamBuilder WithInputName(string? inputName)
    {
        ApplyStreamInputName(inputName);
        return this;
    }

    public MediaPlayerOpenStreamBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        ApplyOptions(options);
        return this;
    }

    public MediaPlayerOpenStreamBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ApplyOptions(configure);
        return this;
    }

    public MediaPlayerOpenStreamBuilder WithVideoLead(IVideoOutput output, bool disposeOnPlayerDispose = false)
    {
        ApplyVideoLead(output, disposeOnPlayerDispose);
        return this;
    }

    public override bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error)
    {
#pragma warning disable CS0618
        var opened = MediaPlayer.TryOpenStream(
            MediaStream,
            StreamInputName,
            Options,
            VideoLead,
            DisposeVideoLead,
            out player,
            out error);
#pragma warning restore CS0618
        return TryBuildWithCompanions(opened, player, out player, out error);
    }
}

/// <summary>Opens from live <see cref="IAudioSource"/> / <see cref="IVideoSource"/> (no container decoder).</summary>
public sealed class MediaPlayerOpenLiveBuilder(
    IAudioSource? audioSource,
    IVideoSource? videoSource) : MediaPlayerOpenBuilder
{
    public IAudioSource? AudioSource { get; } = audioSource;
    public IVideoSource? VideoSource { get; } = videoSource;

    public MediaPlayerOpenLiveBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        ApplyOptions(options);
        return this;
    }

    public MediaPlayerOpenLiveBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ApplyOptions(configure);
        return this;
    }

    public MediaPlayerOpenLiveBuilder WithVideoLead(IVideoOutput output, bool disposeOnPlayerDispose = false)
    {
        ApplyVideoLead(output, disposeOnPlayerDispose);
        return this;
    }

    public MediaPlayerOpenLiveBuilder WithDisposeSourcesOnPlayerDispose(bool dispose = true)
    {
        ApplyDisposeLiveSources(dispose);
        return this;
    }

    public override bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error)
    {
        if (AudioSource is null && VideoSource is null)
        {
            error = "at least one live audio or video source is required.";
            player = null;
            return false;
        }

        if (AudioSource is not null && VideoSource is null && !Options.IncludeAudioRouter)
        {
            error = "audio-only live sources require IncludeAudioRouter.";
            player = null;
            return false;
        }

#pragma warning disable CS0618
        var opened = MediaPlayer.TryOpenLive(
            AudioSource,
            VideoSource,
            Options,
            VideoLead,
            DisposeVideoLead,
            DisposeLiveSources,
            out player,
            out error);
#pragma warning restore CS0618
        return TryBuildWithCompanions(opened, player, out player, out error);
    }
}

/// <summary>Uses an already-opened <see cref="MediaContainerDecoder"/>.</summary>
public sealed class MediaPlayerOpenDecoderBuilder(MediaContainerDecoder decoder) : MediaPlayerOpenBuilder
{
    public MediaContainerDecoder Decoder { get; } = decoder ?? throw new ArgumentNullException(nameof(decoder));

    public MediaPlayerOpenDecoderBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        ApplyOptions(options);
        return this;
    }

    public MediaPlayerOpenDecoderBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ApplyOptions(configure);
        return this;
    }

    public MediaPlayerOpenDecoderBuilder WithVideoLead(IVideoOutput output, bool disposeOnPlayerDispose = false)
    {
        ApplyVideoLead(output, disposeOnPlayerDispose);
        return this;
    }

    public MediaPlayerOpenDecoderBuilder WithDecoderOwnership(MediaPlayerDecoderOwnership ownership)
    {
        ApplyDecoderOwnership(ownership);
        return this;
    }

    public MediaPlayerOpenDecoderBuilder WithVideoSourceOverride(IVideoSource videoSource)
    {
        ApplyVideoSourceOverride(videoSource);
        return this;
    }

    public override bool TryBuild([NotNullWhen(true)] out MediaPlayer? player, out string? error)
    {
#pragma warning disable CS0618
        var opened = MediaPlayer.TryOpen(
            Decoder,
            Options,
            VideoLead,
            DisposeVideoLead,
            DecoderOwnership,
            out player,
            out error,
            VideoSourceOverride);
#pragma warning restore CS0618
        return TryBuildWithCompanions(opened, player, out player, out error);
    }
}

/// <summary>Static entry verbs for <see cref="MediaPlayerOpenBuilder"/>.</summary>
public static class MediaPlayerOpen
{
    public static MediaPlayerOpenFileBuilder File(string filePath) => new(filePath);

    public static MediaPlayerOpenUriBuilder Uri(Uri mediaUri) => new(mediaUri);

    /// <summary>Opens an <strong>absolute</strong> URI (FFmpeg requires one). For filesystem paths use
    /// <see cref="File(string)"/> instead — a relative string is rejected here rather than failing later
    /// inside FFmpeg open.</summary>
    public static MediaPlayerOpenUriBuilder Uri(string uri)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
            throw new ArgumentException(
                $"'{uri}' is not an absolute URI. Use MediaPlayerOpen.File(path) for filesystem paths, " +
                "or pass an absolute URI (e.g. file:///…, http://…, rtsp://…).",
                nameof(uri));
        return new MediaPlayerOpenUriBuilder(absolute);
    }

    public static MediaPlayerOpenStreamBuilder Stream(Stream mediaStream) => new(mediaStream);

    public static MediaPlayerOpenLiveBuilder Live(IAudioSource? audioSource, IVideoSource? videoSource) =>
        new(audioSource, videoSource);

    public static MediaPlayerOpenDecoderBuilder Decoder(MediaContainerDecoder decoder) => new(decoder);
}
