using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;

namespace S.Media.Source.YouTube;

/// <summary>
/// Registers the youtube decoder provider. RELIABLE MODE: the provider only ever opens the locally
/// prepared cache asset — an unprepared <c>youtube://</c> open throws with an actionable message
/// instead of implicitly starting a network download on the fire path (review Gate-5 contract:
/// GO is accepted only after the source reports ready; the UI drives <see cref="YouTubePreparer"/>).
/// </summary>
public sealed class YouTubeSourceModule(YouTubePreparer? preparer = null) : IMediaModule
{
    /// <summary>The preparer/cache this module's provider consults. Hosts share it with their prepare UI.</summary>
    public YouTubePreparer Preparer { get; } = preparer ?? new YouTubePreparer(new YoutubeExplodeGateway());

    public string Name => "YouTube";

    public void Register(IMediaRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddDecoder(new YouTubeDecoderProvider(Preparer));
    }
}

/// <summary>
/// Opens prepared youtube sources from the local cache through the normal FFmpeg file path. Probing
/// scores canonical <c>youtube://</c> URIs at 1.0 and recognizable watch URLs at 0.9 (above FFmpeg's
/// generic network score, so YouTube links never fall through to raw ffmpeg HTTP).
/// </summary>
public sealed class YouTubeDecoderProvider(YouTubePreparer preparer) : IMediaDecoderProvider
{
    private readonly YouTubePreparer _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));

    // A private single-module registry: the youtube provider plays LOCAL files and simply delegates to
    // the FFmpeg provider — referencing it through a registry keeps FFmpegDecoderProvider internal.
    private static readonly Lazy<IMediaRegistry> LocalFFmpeg = new(
        static () => MediaRegistry.Build(b => b.Use(new FFmpegModule())),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public string Name => "YouTube";

    public double Probe(string uri, MediaKind kind)
    {
        if (!YouTubeSourceUri.TryParse(uri, out _, out var selection))
            return 0.0;
        if (kind == MediaKind.Video && !selection.IncludeVideo)
            return 0.0; // audio-only selection has no video track to offer
        return uri.StartsWith(YouTubeSourceUri.Scheme, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.9;
    }

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
    {
        var path = ResolvePreparedAsset(uri);
        if (!LocalFFmpeg.Value.TryOpenVideo(path, options, out var source))
            throw new InvalidOperationException($"prepared YouTube asset '{path}' has no playable video track");
        return source;
    }

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options)
    {
        var path = ResolvePreparedAsset(uri);
        if (!LocalFFmpeg.Value.TryOpenAudio(path, options, out var source))
            throw new InvalidOperationException($"prepared YouTube asset '{path}' has no playable audio track");
        return source;
    }

    /// <summary>Maps a canonical URI to its cached asset, throwing the reliable-mode error when absent.
    /// "Best" selections cannot be resolved offline — the UI persists resolved descriptors after prepare.</summary>
    private string ResolvePreparedAsset(string uri)
    {
        if (!YouTubeSourceUri.TryParse(uri, out var videoId, out var selection))
            throw new ArgumentException($"not a recognizable YouTube source: '{uri}'", nameof(uri));

        var path = _preparer.AssetPathFor(
            videoId,
            selection.IncludeVideo ? selection.Video : null,
            selection.Audio);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"YouTube source '{videoId}' is not prepared for this stream selection — download/cache it " +
                "before playback (reliable mode: cue fire must not start a network download).");
        return path;
    }
}
