using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Registry;

/// <summary>
/// Immutable, queryable snapshot of registered capabilities, injected into players/sessions. The
/// capability set is frozen at build time (D6 — AOT-clean); device enumeration <em>within</em> a backend
/// stays dynamic (see <see cref="IDeviceChangeNotifier"/>). A <c>false</c>/<c>null</c> result means no
/// registered module provides the capability — which is how "no audio module ⇒ no audio output" falls
/// out without special-casing.
/// </summary>
/// <remarks>
/// <strong>Lifetime/lease semantics (CORE-01).</strong> The registry is <em>owned</em> by the host that
/// built it (via <c>MediaHost</c>/<c>MediaRegistry.Build</c>); disposing it releases every module's native
/// runtime. Players/sessions handed a registry are <em>borrowers</em> and must NOT dispose it. The owner
/// disposes only once all borrowers are done. After disposal the concrete registry rejects capability
/// operations (open/create) with <see cref="ObjectDisposedException"/> instead of touching released native
/// runtime; disposal itself is idempotent and thread-safe.
/// </remarks>
public interface IMediaRegistry
{
    /// <summary>Registered audio backends, in registration order. Empty ⇒ no audio I/O available.</summary>
    IReadOnlyList<IAudioBackend> AudioBackends { get; }

    /// <summary>Registered decoder providers, in registration order.</summary>
    IReadOnlyList<IMediaDecoderProvider> Decoders { get; }

    /// <summary>True if some decoder reports non-zero confidence for <paramref name="uri"/> (D2/D3).</summary>
    bool CanOpen(string uri, MediaKind kind);

    /// <summary>Opens the video track of <paramref name="uri"/> via the highest-confidence decoder (D3). The
    /// boolean is decoder <em>selection</em>, not open success: it returns <c>false</c> when no registered
    /// provider claims the URI, and <c>true</c> once a provider is chosen. A provider that claims the URI but
    /// then cannot open it (e.g. the container has no video stream) <strong>throws</strong> — that is a genuine
    /// open failure, distinct from "nothing here can play this".</summary>
    bool TryOpenVideo(string uri, VideoSourceOpenOptions? options, [MaybeNullWhen(false)] out IVideoSource source);

    /// <summary>Opens an audio track of <paramref name="uri"/> via the highest-confidence decoder (D3). As with
    /// <see cref="TryOpenVideo(string, VideoSourceOpenOptions?, IVideoSource)"/>, <c>false</c> means no provider
    /// claims the URI; a claimed source that cannot be opened (e.g. no audio stream) throws.</summary>
    bool TryOpenAudio(string uri, AudioSourceOpenOptions? options, [MaybeNullWhen(false)] out IAudioSource source);

    /// <summary>The decoder provider registered under <paramref name="name"/> (case-insensitive), or <c>null</c>.</summary>
    IMediaDecoderProvider? FindDecoder(string name);

    /// <summary>Opens video via an explicitly <strong>pinned</strong> provider (D3 — bypasses confidence
    /// selection). Returns <c>false</c> if no provider named <paramref name="providerName"/> is registered.</summary>
    bool TryOpenVideo(string uri, VideoSourceOpenOptions? options, string providerName, [MaybeNullWhen(false)] out IVideoSource source);

    /// <summary>Opens audio via an explicitly <strong>pinned</strong> provider (D3 — bypasses confidence selection).</summary>
    bool TryOpenAudio(string uri, AudioSourceOpenOptions? options, string providerName, [MaybeNullWhen(false)] out IAudioSource source);

    /// <summary>Opens a still image by file extension (an image source registered by a module).</summary>
    bool TryOpenImage(string path, [MaybeNullWhen(false)] out IVideoSource source);

    /// <summary>
    /// Opens <paramref name="request"/> atomically (NXT-02) via the highest-confidence provider (or the pinned
    /// <see cref="MediaOpenRequest.ProviderHint"/>): for a correlated A/V item, capable providers share a single
    /// demux for both tracks (one open/probe, one buffering/seek state) instead of the split video+audio opens.
    /// Throws when no provider can open the URI; the provider's real failure surfaces as the thrown exception.
    /// Default implementation picks the provider by confidence and delegates to <see cref="IMediaDecoderProvider.OpenAsync"/>.
    /// </summary>
    ValueTask<MediaOpenResult> OpenAsync(
        MediaOpenRequest request,
        IProgress<MediaPrepareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Uri);

        IMediaDecoderProvider? provider;
        if (request.ProviderHint is { Length: > 0 } hint)
        {
            provider = FindDecoder(hint);
        }
        else
        {
            // Highest-confidence provider across either kind (most providers open both); ties to earliest-registered.
            provider = null;
            var best = 0.0;
            foreach (var d in Decoders)
            {
                var score = Math.Max(d.Probe(request.Uri, MediaKind.Video), d.Probe(request.Uri, MediaKind.Audio));
                if (score > best)
                {
                    best = score;
                    provider = d;
                }
            }
        }

        if (provider is null)
            throw new InvalidOperationException(
                $"no registered decoder could open '{request.Uri}' " +
                $"(registered: {string.Join(", ", Decoders.Select(d => d.Name))}).");

        return provider.OpenAsync(request, progress, cancellationToken);
    }

    /// <summary>Creates a CPU pixel converter, or <c>null</c> when no module registered one.</summary>
    IVideoCpuFrameConverter? CreateCpuConverter();

    /// <summary>Wraps <paramref name="source"/> to resample to <paramref name="targetSampleRate"/>, or <c>null</c> if unavailable.</summary>
    IAudioSource? CreateResampler(IAudioSource source, int targetSampleRate);

    /// <summary>True when a module registered an adaptive-rate output factory (FFmpeg) — i.e. the router can
    /// drift-correct non-master audio outputs.</summary>
    bool SupportsAdaptiveRateOutput { get; }

    /// <summary>Wraps a non-master <paramref name="inner"/> output for adaptive-rate drift correction, or
    /// <c>null</c> when no module registered a factory. See <see cref="AdaptiveRateOutputFactory"/>.</summary>
    IAudioOutput? CreateAdaptiveRateOutput(IAudioOutput inner, Func<double> playbackPpmBias, int maxRateDeltaHz, IDisposable? biasSource);

    /// <summary>Creates a deinterlacer; falls back to the built-in <see cref="BobDeinterlacer"/> when no module set one.</summary>
    IDeinterlacer CreateDeinterlacer(VideoFormat input);
}
