using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Registry;

/// <summary>The kind of a track described by <see cref="MediaTrackInfo"/>.</summary>
public enum MediaTrackKind
{
    Video,
    Audio,
    Subtitle,
    Data,
}

/// <summary>A track found in an opened asset (probe metadata, no decoding). Lets a host enumerate what a
/// media item contains to drive track-selection UI.</summary>
public sealed record MediaTrackInfo(int Index, MediaTrackKind Kind, string Codec, string? Language = null);

/// <summary>Progress for a (potentially slow / network) media prepare-or-open. <paramref name="Fraction"/> is
/// <c>0..1</c> when known, else null (indeterminate).</summary>
public sealed record MediaPrepareProgress(string Stage, double? Fraction = null, string? Message = null);

/// <summary>
/// What to open, in one atomic operation (NXT-02). Replaces the split <c>OpenVideo</c>/<c>OpenAudio</c> calls
/// that opened a correlated A/V item as two independent demux contexts. A null <see cref="Video"/> or
/// <see cref="Audio"/> means "do not open that kind"; at least one must be non-null.
/// </summary>
public sealed record MediaOpenRequest(string Uri)
{
    /// <summary>Video open options, or null to not open a video track.</summary>
    public VideoSourceOpenOptions? Video { get; init; }

    /// <summary>Audio open options, or null to not open an audio track.</summary>
    public AudioSourceOpenOptions? Audio { get; init; }

    /// <summary>Optional explicit provider name - pins selection, bypassing confidence (D3).</summary>
    public string? ProviderHint { get; init; }

    /// <summary>Opens both kinds with default options.</summary>
    public static MediaOpenRequest AudioAndVideo(string uri) =>
        new(uri) { Video = new VideoSourceOpenOptions(), Audio = new AudioSourceOpenOptions() };
}

/// <summary>
/// The result of an atomic <see cref="IMediaDecoderProvider.OpenAsync"/> (NXT-02): one opened media asset that
/// owns its (correlated) tracks. For a file with both audio and video, <see cref="Video"/> and <see cref="Audio"/>
/// share a single underlying demux - so they have one buffering/seek state and the item is opened/probed once.
/// The result OWNS the asset: dispose it (not the individual sources) to tear everything down. The sources are
/// borrowed views into the asset and must not be disposed independently.
/// </summary>
public sealed class MediaOpenResult : IAsyncDisposable, IDisposable
{
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public MediaOpenResult(
        string providerName,
        IVideoSource? video,
        IAudioSource? audio,
        TimeSpan duration,
        bool isLive,
        bool canSeek,
        IReadOnlyList<MediaTrackInfo>? tracks = null,
        Func<ValueTask>? disposeAsync = null)
    {
        ProviderName = providerName ?? throw new ArgumentNullException(nameof(providerName));
        Video = video;
        Audio = audio;
        Duration = duration;
        IsLive = isLive;
        CanSeek = canSeek;
        Tracks = tracks ?? [];
        _disposeAsync = disposeAsync;
    }

    /// <summary>The provider that opened the asset (diagnostics / pinning).</summary>
    public string ProviderName { get; }

    /// <summary>The selected video track, or null when the request didn't ask for video / the asset has none.
    /// Borrowed - owned by this result; do not dispose it directly.</summary>
    public IVideoSource? Video { get; }

    /// <summary>The selected audio track, or null when the request didn't ask for audio / the asset has none.
    /// Borrowed - owned by this result; do not dispose it directly.</summary>
    public IAudioSource? Audio { get; }

    /// <summary>Asset duration, or <see cref="TimeSpan.Zero"/> when unknown / live.</summary>
    public TimeSpan Duration { get; }

    /// <summary>True for a live source (NDI / capture) - no duration, not seekable.</summary>
    public bool IsLive { get; }

    /// <summary>True when the asset supports seeking.</summary>
    public bool CanSeek { get; }

    /// <summary>Every track the asset advertises (for selection UI). May be empty when the provider doesn't enumerate.</summary>
    public IReadOnlyList<MediaTrackInfo> Tracks { get; }

    /// <summary>True when <see cref="Video"/> is album-art / a single attached picture, so a consumer can drive a
    /// still-frame display mode. Set by the provider; default false.</summary>
    public bool VideoIsAttachedPicture { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_disposeAsync is not null)
            await _disposeAsync().ConfigureAwait(false);
    }

    /// <summary>Synchronous disposal for callers that own the result on a non-async path.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
