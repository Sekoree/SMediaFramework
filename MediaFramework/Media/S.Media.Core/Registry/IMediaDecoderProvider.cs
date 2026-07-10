using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Registry;

/// <summary>Which stream kind a decoder is being asked to open.</summary>
public enum MediaKind
{
    Video,
    Audio,
}

/// <summary>
/// Opens media addressed by a URI scheme (D2: <c>file:</c> / <c>ndi:</c> / <c>capture:</c> / <c>mic:</c> /
/// <c>image:</c> / <c>http(s):</c>). When several providers can open the same URI the registry picks by
/// confidence (D3): highest <see cref="Probe"/> wins, ties broken by registration order.
/// </summary>
public interface IMediaDecoderProvider
{
    /// <summary>Stable provider name (e.g. <c>"FFmpeg"</c>). Used for diagnostics and explicit pinning.</summary>
    string Name { get; }

    /// <summary>
    /// Confidence in <c>[0,1]</c> that this provider can open <paramref name="uri"/> for
    /// <paramref name="kind"/>; <c>0</c> means "cannot". The registry selects the highest score and
    /// breaks ties in favour of the earliest-registered provider (D3).
    /// </summary>
    double Probe(string uri, MediaKind kind);

    /// <summary>Opens the video track of <paramref name="uri"/>. Throws if it cannot.</summary>
    IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options);

    /// <summary>
    /// Opens an audio track of <paramref name="uri"/>. Throws if it cannot. Multi-track selection
    /// (none/one/many, 03 §6) is carried in <paramref name="options"/>; per-track enumeration arrives
    /// with the FFmpeg provider in Phase 2.
    /// </summary>
    IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options);

    /// <summary>
    /// Opens the tracks requested by <paramref name="request"/> in <strong>one atomic operation</strong> (NXT-02),
    /// returning a <see cref="MediaOpenResult"/> that owns the asset. A provider that can demux a correlated A/V
    /// item once (e.g. FFmpeg) overrides this to share a <em>single</em> demux for the audio and video tracks -
    /// one open/probe, one buffering/seek state - instead of the split <see cref="OpenVideo"/> + <see cref="OpenAudio"/>
    /// path that opened two independent contexts. The default bridges to those per-kind methods (preserving older
    /// behaviour for providers that don't override) and runs on a worker thread so a slow open doesn't block the
    /// caller. Cancellation is honoured at stage boundaries (a synchronous native open can't be interrupted mid-call).
    /// </summary>
    async ValueTask<MediaOpenResult> OpenAsync(
        MediaOpenRequest request,
        IProgress<MediaPrepareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Video is null && request.Audio is null)
            throw new ArgumentException("MediaOpenRequest must request at least one of video or audio.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            progress?.Report(new MediaPrepareProgress("opening", Message: request.Uri));
            // Open each requested kind opportunistically: a split-open provider legitimately throws for a kind
            // the source lacks (an audio-only file has no video, and vice-versa). Tolerate that and fail only
            // when BOTH requested sides fail - surfacing the real cause(s), not a generic message (NXT-02).
            IVideoSource? video = null;
            Exception? videoError = null;
            if (request.Video is not null)
                try { video = OpenVideo(request.Uri, request.Video); }
                catch (Exception ex) { videoError = ex; }

            IAudioSource? audio = null;
            Exception? audioError = null;
            if (request.Audio is not null)
                try { audio = OpenAudio(request.Uri, request.Audio); }
                catch (Exception ex) { audioError = ex; }

            if (video is null && audio is null)
            {
                var causes = new List<string>();
                if (videoError is not null) causes.Add($"video: {videoError.Message}");
                if (audioError is not null) causes.Add($"audio: {audioError.Message}");
                throw new InvalidOperationException(causes.Count > 0
                    ? $"could not open '{request.Uri}' - {string.Join("; ", causes)}"
                    : $"'{request.Uri}' produced neither a requested audio nor video track.");
            }

            return new MediaOpenResult(
                Name, video, audio, TimeSpan.Zero, isLive: false, canSeek: false,
                disposeAsync: () =>
                {
                    (audio as IDisposable)?.Dispose();
                    (video as IDisposable)?.Dispose();
                    return ValueTask.CompletedTask;
                });
        }, cancellationToken).ConfigureAwait(false);
    }
}
