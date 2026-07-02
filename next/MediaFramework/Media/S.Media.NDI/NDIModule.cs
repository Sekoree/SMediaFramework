using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// Registers NDI receive capabilities into the media registry — the AOT-pure replacement for the old
/// static <c>.UseNDI()</c> hook (P2). Acquires one ref-counted NDI runtime scope and contributes a
/// decoder provider for the <c>ndi:</c> scheme, so <c>ndi://&lt;source-name&gt;</c> opens as an
/// <see cref="IVideoSource"/> / <see cref="IAudioSource"/>. The NDI <em>sender</em> path (egress as a
/// CpuFrameCompositeTarget, OQ3) and live A/V correlation onto a SourceSyncGroup land with the live-sync
/// convergence work; this module is the receive + runtime half.
/// </summary>
/// <param name="audioMinBuffer">Overrides the receiver's audio jitter reserve (<see cref="NDISource"/> default
/// 50 ms). Smaller brings the audio forward — lower latency, closer to the live video — at more underrun risk;
/// <c>null</c> keeps the default. This is the lever for live A/V sync: shrink it so audio meets the low-latency
/// video instead of holding video back.</param>
public sealed class NDIModule(TimeSpan? audioMinBuffer = null) : IMediaModule
{
    public string Name => "NDI";

    public void Register(IMediaRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // One ref-counted NDI runtime reference, owned by the registry: the disposable handle is registered as a
        // lifetime and released when the registry is disposed (NXT-05), instead of being dropped on the floor and
        // leaking the ref. Fail at registration if the SDK/CPU is unavailable rather than silently at first open.
        var rc = NDIRuntime.Create(out var runtime);
        if (rc != 0 || runtime is null)
            throw new InvalidOperationException($"NDI runtime init failed (error {rc}).");

        builder.AddLifetime(runtime);
        builder.AddDecoder(new NDIDecoderProvider(audioMinBuffer));
    }
}

/// <summary>
/// Opens <c>ndi:</c> URIs through the registry (D2/D3). <c>ndi://&lt;name&gt;</c> (name optionally
/// URL-encoded) identifies an NDI sender on the network; the provider resolves it via
/// <see cref="NDISource.Find"/> (<c>find_wait_for_sources</c>, OQ9) and connects a receiver. Claims the
/// <c>ndi:</c> scheme exclusively — FFmpeg's provider defers it.
/// </summary>
internal sealed class NDIDecoderProvider : IMediaDecoderProvider
{
    // Discovery is inherently latent; bound the blocking wait during open.
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    // One ref-counted receiver per paired OpenVideo + OpenAudio call for the same ndi://, so audio and video
    // arrive on a single connection anchored together (one audio-driven ingest clock) rather than two
    // independently-anchored receivers. Independent consumers get independent receivers.
    private readonly SharedNdiSourceCache _cache;

    public NDIDecoderProvider(TimeSpan? audioMinBuffer = null) =>
        _cache = new SharedNdiSourceCache(uri =>
        {
            var descriptor = ParseSourceUri(uri);
            var source = NDISource.Open(ResolveSource(descriptor.SourceName), new NDISourceOptions
            {
                ReceiveVideo = descriptor.ReceiveVideo,
                ReceiveAudio = descriptor.ReceiveAudio,
                Bandwidth = NDIReceiveBandwidthPolicy.Resolve(
                    descriptor.ReceiveAudio,
                    descriptor.ReceiveVideo,
                    descriptor.LowBandwidth ? NDILib.NDIRecvBandwidth.Lowest : NDILib.NDIRecvBandwidth.Highest),
                // The audio jitter reserve — the dominant tunable latency between the audio and the live video.
                AudioMinBufferedDuration = descriptor.AudioMinBufferedDuration ?? audioMinBuffer,
            });
            // Warm up so the A/V formats are available before the open path reads them — the audio router needs
            // the format up front (live NDI delivers no format until the first frame arrives). Best-effort: an
            // A/V sender is ready in ~ms; a video-only sender just won't satisfy the audio wait.
            source.WaitForStreams(TimeSpan.FromSeconds(3));
            return source;
        });

    public string Name => "NDI";

    public double Probe(string uri, MediaKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        return SchemeOf(uri) == "ndi" ? 1.0 : 0.0;
    }

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
    {
        var descriptor = ParseSourceUri(uri);
        if (!descriptor.ReceiveVideo)
            throw new NotSupportedException($"NDI source '{descriptor.SourceName}' is configured without video.");
        var source = _cache.LeaseVideo(uri);
        try
        {
            _ = source.Format;
            return source;
        }
        catch
        {
            (source as IDisposable)?.Dispose();
            throw;
        }
    }

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options)
    {
        var descriptor = ParseSourceUri(uri);
        if (!descriptor.ReceiveAudio)
            throw new NotSupportedException($"NDI source '{descriptor.SourceName}' is configured without audio.");
        var source = _cache.LeaseAudio(uri);
        try
        {
            _ = source.Format;
            return source;
        }
        catch
        {
            (source as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>NXT-02 atomic open: leases the requested video + audio from <strong>one</strong> shared NDI
    /// receiver (anchored on a single audio-driven ingest clock), reported as a live, non-seekable result.</summary>
    public async ValueTask<MediaOpenResult> OpenAsync(
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
            progress?.Report(new MediaPrepareProgress("connecting", Message: request.Uri));
            var descriptor = ParseSourceUri(request.Uri);
            var video = request.Video is not null && descriptor.ReceiveVideo ? _cache.LeaseVideo(request.Uri) : null;
            IAudioSource? audio = null;
            try
            {
                audio = request.Audio is not null && descriptor.ReceiveAudio ? _cache.LeaseAudio(request.Uri) : null;
                if (video is null && audio is null)
                    throw new InvalidOperationException(
                        $"NDI source '{descriptor.SourceName}' has no enabled stream requested by the player.");
                _ = video?.Format; // surface formats before returning (live NDI delivers none until the first frame)
                _ = audio?.Format;
            }
            catch
            {
                (audio as IDisposable)?.Dispose();
                (video as IDisposable)?.Dispose();
                throw;
            }

            return new MediaOpenResult(
                Name, video, audio, TimeSpan.Zero, isLive: true, canSeek: false,
                disposeAsync: () =>
                {
                    (audio as IDisposable)?.Dispose();
                    (video as IDisposable)?.Dispose();
                    return ValueTask.CompletedTask;
                });
        }, cancellationToken).ConfigureAwait(false);
    }

    private static NDIDiscoveredSource ResolveSource(string name)
    {
        foreach (var s in NDISource.Find(DiscoveryTimeout))
            if (string.Equals(s.Name, name, StringComparison.Ordinal))
                return s;
        throw new InvalidOperationException(
            $"NDI source '{name}' was not found on the network within {DiscoveryTimeout.TotalSeconds:0}s.");
    }

    /// <summary>Lowercased scheme up to the first ':' (empty if none).</summary>
    private static string SchemeOf(string uri)
    {
        var i = uri.IndexOf(':');
        return i > 0 ? uri[..i].ToLowerInvariant() : string.Empty;
    }

    internal sealed record SourceDescriptor(
        string SourceName,
        bool ReceiveAudio,
        bool ReceiveVideo,
        bool LowBandwidth,
        TimeSpan? AudioMinBufferedDuration);

    /// <summary>Parses an NDI source descriptor. Query options are deliberately provider-owned so a persisted
    /// HaPlay item keeps its stream-selection, bandwidth, and jitter-buffer policy when opened through the
    /// provider-neutral media registry.</summary>
    internal static SourceDescriptor ParseSourceUri(string uri)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        var rest = uri["ndi:".Length..];
        if (rest.StartsWith("//", StringComparison.Ordinal))
            rest = rest[2..];
        var queryAt = rest.IndexOf('?');
        var encodedName = queryAt >= 0 ? rest[..queryAt] : rest;
        var values = queryAt >= 0 ? ParseQuery(rest[(queryAt + 1)..]) : new Dictionary<string, string>();
        var receiveAudio = ReadBool(values, "audio", true);
        var receiveVideo = ReadBool(values, "video", true);
        if (!receiveAudio && !receiveVideo)
            throw new ArgumentException("an NDI source must enable audio, video, or both.", nameof(uri));

        TimeSpan? audioBuffer = null;
        if (values.TryGetValue("audioBufferMs", out var bufferText))
        {
            if (!int.TryParse(bufferText, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms) || ms is < 0 or > 2000)
                throw new ArgumentException("NDI audioBufferMs must be between 0 and 2000.", nameof(uri));
            audioBuffer = TimeSpan.FromMilliseconds(ms);
        }

        return new SourceDescriptor(
            Uri.UnescapeDataString(encodedName), receiveAudio, receiveVideo,
            ReadBool(values, "lowBandwidth", false), audioBuffer);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = Uri.UnescapeDataString(equals >= 0 ? part[..equals] : part);
            var value = Uri.UnescapeDataString(equals >= 0 ? part[(equals + 1)..] : string.Empty);
            values[key] = value;
        }
        return values;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var text))
            return fallback;
        return text switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => throw new ArgumentException($"NDI option '{key}' must be a boolean."),
        };
    }
}
