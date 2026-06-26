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

        // One ref-counted NDI runtime reference. NDIRuntime has a static refcount and no finalizer, so the
        // native runtime stays initialised for the process; the disposable handle is released by host /
        // session shutdown wiring (deferred — the same model as PortAudio). Fail at registration if the
        // SDK/CPU is unavailable rather than silently at first open.
        var rc = NDIRuntime.Create(out var runtime);
        if (rc != 0 || runtime is null)
            throw new InvalidOperationException($"NDI runtime init failed (error {rc}).");

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
        _cache = new SharedNdiSourceCache(name =>
        {
            var source = NDISource.Open(ResolveSource(name), new NDISourceOptions
            {
                ReceiveVideo = true,
                ReceiveAudio = true,
                // The audio jitter reserve — the dominant tunable latency between the audio and the live video.
                AudioMinBufferedDuration = audioMinBuffer,
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
        var source = _cache.LeaseVideo(SourceNameFromUri(uri));
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
        var source = _cache.LeaseAudio(SourceNameFromUri(uri));
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

    /// <summary>The NDI source name from <c>ndi:&lt;name&gt;</c> / <c>ndi://&lt;name&gt;</c> (URL-decoded).</summary>
    private static string SourceNameFromUri(string uri)
    {
        var rest = uri["ndi:".Length..];
        if (rest.StartsWith("//", StringComparison.Ordinal))
            rest = rest[2..];
        return Uri.UnescapeDataString(rest);
    }
}
