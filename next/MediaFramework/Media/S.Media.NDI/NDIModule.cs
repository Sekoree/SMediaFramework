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
public sealed class NDIModule : IMediaModule
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

        builder.AddDecoder(new NDIDecoderProvider());
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

    public string Name => "NDI";

    public double Probe(string uri, MediaKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        return SchemeOf(uri) == "ndi" ? 1.0 : 0.0;
    }

    // Receive only the stream we were asked for. Disposing either adapter disposes the NDISource, so a
    // video-only / audio-only open owns its connection outright. (Correlating one NDISource's video and
    // audio through a SourceSyncGroup is the live-convergence step.)
    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) =>
        OpenSource(uri, receiveVideo: true, receiveAudio: false).Video;

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        OpenSource(uri, receiveVideo: false, receiveAudio: true).Audio;

    private static NDISource OpenSource(string uri, bool receiveVideo, bool receiveAudio)
    {
        var discovered = ResolveSource(SourceNameFromUri(uri));
        return NDISource.Open(discovered, new NDISourceOptions
        {
            ReceiveVideo = receiveVideo,
            ReceiveAudio = receiveAudio,
        });
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
