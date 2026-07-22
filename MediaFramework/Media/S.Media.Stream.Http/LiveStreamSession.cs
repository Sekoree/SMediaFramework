using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.Encode.FFmpeg.Sinks;

namespace S.Media.Stream.Http;

/// <summary>Where a live session pushes to (in addition to the optional LAN server).</summary>
public enum PushProtocol
{
    Rtmp,
    Srt,
    Rtsp,
}

/// <summary>A push destination. <paramref name="StreamKey"/> is the ingest key/auth token (Twitch/
/// YouTube-style): RTMP appends it to the URL path, SRT sets it as <c>streamid</c>. Leave empty when
/// the key is already in the URL or the endpoint needs none.</summary>
public sealed record PushTarget(PushProtocol Protocol, string Url, string? StreamKey = null)
{
    /// <summary>SRT retransmission window in milliseconds. Null leaves libsrt's default in effect;
    /// an explicit <c>latency=</c> URL query always takes precedence.</summary>
    public int? SrtLatencyMilliseconds { get; init; }

    /// <summary>The full URL handed to libavformat, folding in <see cref="StreamKey"/> per protocol.</summary>
    public string ResolveUrl()
    {
        var resolvedUrl = Url;
        if (Protocol == PushProtocol.Srt
            && SrtLatencyMilliseconds is { } latencyMilliseconds
            && !HasQueryOption(resolvedUrl, "latency"))
        {
            // FFmpeg's SRT protocol option is microseconds, while the public/UI setting is the much
            // less error-prone millisecond unit used by most SRT operator documentation.
            resolvedUrl = AppendQueryOption(
                resolvedUrl,
                "latency",
                checked(latencyMilliseconds * 1000L).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (string.IsNullOrWhiteSpace(StreamKey))
            return resolvedUrl;
        var key = StreamKey.Trim();
        return Protocol switch
        {
            // rtmp://host/app  +  /streamkey  (the classic ingest pattern).
            PushProtocol.Rtmp => resolvedUrl.TrimEnd('/') + "/" + key,
            // srt://host:port?streamid=key (appended to any existing query).
            PushProtocol.Srt => AppendQueryOption(resolvedUrl, "streamid", Uri.EscapeDataString(key)),
            // RTSP auth is user:pass in the URL; a bare key isn't standard - append as a path segment.
            PushProtocol.Rtsp => resolvedUrl.TrimEnd('/') + "/" + key,
            _ => resolvedUrl,
        };
    }

    private static string AppendQueryOption(string url, string name, string value)
    {
        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : "";
        var baseUrl = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        return baseUrl + (baseUrl.Contains('?') ? "&" : "?") + name + "=" + value + fragment;
    }

    private static bool HasQueryOption(string url, string name)
    {
        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0)
            return false;
        var query = url[(queryIndex + 1)..];
        var fragmentIndex = query.IndexOf('#');
        if (fragmentIndex >= 0)
            query = query[..fragmentIndex];
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)[0])
            .Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Credential-free destination label for metrics and logs. Query option names remain
    /// visible for diagnosis, but their values (stream IDs, passphrases, tokens) never do.</summary>
    internal string SafeDisplayName()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
            return $"{Protocol.ToString().ToLowerInvariant()} destination";

        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.Host}]" : uri.Host;
        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        var queryKeys = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2)[0])
            .Where(key => key.Length > 0)
            .Select(key => $"{key}=…")
            .ToArray();
        var query = queryKeys.Length > 0 ? $"?{string.Join('&', queryKeys)}" : "";
        var separateKey = string.IsNullOrWhiteSpace(StreamKey) ? "" : " (+key)";
        return $"{uri.Scheme}://{authority}{uri.AbsolutePath}{query}{separateKey}";
    }
}

/// <summary>The built-in LAN server's configuration. Port 0 binds an ephemeral port. <paramref name="MountName"/>
/// is the URL path segment (e.g. "stage" → <c>/stage.ts</c>, <c>/stage/hls/live.m3u8</c>), so several
/// streams can share one server on the same port under different names.</summary>
public sealed record LocalServerOptions(
    int Port = 8620, bool EnableTs = true, bool EnableHls = true, string MountName = "stream");

/// <summary>Everything a live-stream session needs: shared encode settings + destinations.</summary>
public sealed record LiveStreamOptions
{
    public EncodeSessionOptions Encode { get; init; } = new() { Container = EncodeContainer.MpegTs };

    public IReadOnlyList<PushTarget> PushTargets { get; init; } = [];

    public LocalServerOptions? LocalServer { get; init; }

    /// <summary>Structured validation across all destinations (RTMP's FLV limits apply regardless of
    /// the primary container because every destination shares ONE encode).</summary>
    public IReadOnlyList<string> Validate(bool probeEncoders = true, int audioInputSampleRate = 48_000)
    {
        var errors = new List<string>(Encode.Validate(probeEncoders, audioInputSampleRate));

        if (PushTargets.Count == 0 && LocalServer is null)
            errors.Add("The stream has no destination: add a push target or enable the local server.");
        if (LocalServer is { } local)
        {
            if (!local.EnableTs && !local.EnableHls)
                errors.Add("The local server has neither MPEG-TS nor HLS enabled.");
            if (local.Port is < 0 or > 65_535)
                errors.Add("The local server port must be between 0 and 65535.");
        }

        foreach (var target in PushTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Url))
            {
                errors.Add("A push target has an empty URL.");
                continue;
            }

            var expectedScheme = target.Protocol switch
            {
                PushProtocol.Rtmp => "rtmp",
                PushProtocol.Srt => "srt",
                PushProtocol.Rtsp => "rtsp",
                _ => null,
            };
            if (expectedScheme is not null && !target.Url.StartsWith(expectedScheme, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Push URL '{target.Url}' does not look like a {expectedScheme}:// address.");
            if (target.SrtLatencyMilliseconds is not null && target.Protocol != PushProtocol.Srt)
                errors.Add("SRT latency can only be set on an SRT push target.");
            if (target.SrtLatencyMilliseconds is { } srtLatency && srtLatency is < 20 or > 8_000)
                errors.Add("SRT latency must be between 20 and 8000 ms.");
        }

        if (PushTargets.Any(t => t.Protocol == PushProtocol.Rtmp))
        {
            if (Encode.IncludesVideo && Encode.Video.Codec != EncodeVideoCodec.H264)
                errors.Add("RTMP (FLV) requires H.264 video.");
            if (Encode.IncludesAudio)
            {
                if (Encode.AudioLegs.Count > 1)
                    errors.Add("RTMP (FLV) carries a single audio track - remove the extra tracks or drop the RTMP target.");
                if (Encode.AudioLegs.Count > 0 && Encode.AudioLegs[0].Codec != EncodeAudioCodec.Aac)
                    errors.Add("RTMP (FLV) requires AAC audio.");
            }
        }

        // A live stream must declare an explicit output resolution + frame rate: clients (and the
        // keep-alive that streams blank before media plays) need a fixed, unchanging format. Source-
        // following ("0") would renegotiate mid-stream when a track's raster differs and confuse players.
        if (Encode.IncludesVideo)
        {
            if (Encode.Video.ScaleWidth <= 0 || Encode.Video.ScaleHeight <= 0)
                errors.Add("A live stream needs an explicit output resolution (set width and height).");
            if (Encode.Video.Fps <= 0)
                errors.Add("A live stream needs an explicit frame rate.");
        }

        return errors;
    }
}

/// <summary>Health/state of one destination for HUDs.</summary>
public sealed record LiveStreamStatus(
    FFmpegEncodeSessionMetrics Encode,
    int LocalServerPort,
    int LocalServerClients,
    long LocalServerBytesServed,
    string? TsUrlPath,
    string? HlsUrlPath);

/// <summary>
/// One live stream: a single <see cref="FFmpegEncodeSession"/> whose packets fan out to every push
/// target (each behind its own drain thread - a stalled RTMP ingest cannot stall the encoder or the
/// LAN viewers) and, optionally, the built-in LAN HTTP server (MPEG-TS broadcast + FFmpeg-hls
/// segments). Attach <see cref="VideoSink"/>/<see cref="AudioSinks"/> exactly like the file output;
/// <see cref="StopAsync"/> ends every destination cleanly.
/// </summary>
public sealed class LiveStreamSession : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Stream.Http.LiveStreamSession");

    private readonly FFmpegEncodeSession _session;
    private readonly HttpMediaServer.MountHandle? _mount;
    private readonly TsFanOutBuffer? _tsBuffer;
    private readonly string? _hlsDirectory;
    private readonly string _mountName;
    private readonly ContinuousEncodeCarrier? _carrier;
    private bool _disposed;

    private LiveStreamSession(
        FFmpegEncodeSession session, HttpMediaServer.MountHandle? mount, TsFanOutBuffer? tsBuffer,
        string? hlsDirectory, string mountName, ContinuousEncodeCarrier? carrier)
    {
        _session = session;
        _mount = mount;
        _tsBuffer = tsBuffer;
        _hlsDirectory = hlsDirectory;
        _mountName = mountName;
        _carrier = carrier;
    }

    public static LiveStreamSession Start(LiveStreamOptions options, int audioInputSampleRate = 48_000)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.Validate(audioInputSampleRate: audioInputSampleRate);
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid stream options: {string.Join(" | ", errors)}", nameof(options));

        var sinks = new List<IEncodedPacketSink>();
        TsFanOutBuffer? tsBuffer = null;
        string? hlsDirectory = null;
        HttpMediaServer.MountHandle? mount = null;
        ContinuousEncodeCarrier? carrier = null;
        FFmpegEncodeSession? session = null;
        var sinksTransferred = false;
        var mountName = options.LocalServer?.MountName ?? "stream";
        try
        {
            foreach (var push in options.PushTargets)
            {
                var url = push.ResolveUrl(); // folds in the stream key/auth per protocol
                var target = push.Protocol switch
                {
                    PushProtocol.Rtmp => UrlEncodeTarget.Rtmp(url),
                    PushProtocol.Srt => UrlEncodeTarget.Srt(url),
                    PushProtocol.Rtsp => UrlEncodeTarget.Rtsp(url),
                    _ => throw new ArgumentOutOfRangeException(nameof(options), $"unknown protocol {push.Protocol}"),
                };
                // Sink names flow into metrics, warnings and operator health. Always strip query values:
                // SRT providers commonly put both stream tokens and encryption passphrases in the URL.
                target = target with { DisplayName = push.SafeDisplayName() };
                var container = push.Protocol == PushProtocol.Rtmp ? EncodeContainer.Flv : EncodeContainer.MpegTs;
                var reconnecting = new ReconnectingPacketSink(
                    target.DisplayName,
                    () => new MuxPacketSink(target, container));
                sinks.Add(new AsyncPacketSink(reconnecting));
            }

            if (options.LocalServer is { } local && (local.EnableTs || local.EnableHls))
            {
                if (local.EnableTs)
                {
                    tsBuffer = new TsFanOutBuffer();
                    var buffer = tsBuffer;
                    sinks.Add(new AsyncPacketSink(new MuxPacketSink(
                        new CallbackEncodeTarget("mpegts", buffer.OnBytes),
                        EncodeContainer.MpegTs)
                    {
                        PacketBoundaryWritten = buffer.OnPacketBoundary,
                    }));
                }

                if (local.EnableHls)
                {
                    hlsDirectory = Path.Combine(Path.GetTempPath(), $"haplay-hls-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(hlsDirectory);
                    var playlist = Path.Combine(hlsDirectory, "live.m3u8");
                    sinks.Add(new AsyncPacketSink(new MuxPacketSink(
                        new UrlEncodeTarget(playlist, "hls")
                        {
                            // FFmpeg does the segmenting: 2 s segments, rolling window, atomic renames.
                            MuxerOptions = new Dictionary<string, string>
                            {
                                ["hls_time"] = "2",
                                ["hls_list_size"] = "6",
                                ["hls_flags"] = "delete_segments+temp_file",
                                ["hls_segment_filename"] = Path.Combine(hlsDirectory, "seg_%05d.ts"),
                            },
                        },
                        EncodeContainer.MpegTs)));
                }

                mount = HttpMediaServer.AcquireMount(local.Port, mountName, tsBuffer, hlsDirectory);
                mountName = mount.MountName;
            }

            // CreateWithSinks assumes ownership even when construction fails: native-core setup is
            // exception-safe and rolls every sink back with it.
            sinksTransferred = true;
            session = FFmpegEncodeSession.CreateWithSinks(options.Encode, sinks, audioInputSampleRate);

            // Keep-alive: stream blank video + silence at the locked format from the moment we go live,
            // so a client that connects before any media plays sees a valid running stream (not a
            // never-starting one). Each track yields only while actual media samples are arriving and
            // resumes after inactivity. Runs for video, audio-only, or both.
            if (options.Encode.IncludesVideo || options.Encode.IncludesAudio)
            {
                carrier = new ContinuousEncodeCarrier(
                    session,
                    options.Encode.IncludesVideo ? options.Encode.Video.ScaleWidth : 0,
                    options.Encode.IncludesVideo ? options.Encode.Video.ScaleHeight : 0,
                    options.Encode.IncludesVideo ? Math.Max(1, options.Encode.Video.Fps) : 0);
                carrier.Start();
            }

            Trace.LogInformation("live stream started: {Push} push target(s), local server {Server}",
                options.PushTargets.Count, mount is not null ? $"port {mount.Port} /{mountName}" : "off");
            return new LiveStreamSession(session, mount, tsBuffer, hlsDirectory, mountName, carrier);
        }
        catch
        {
            carrier?.Dispose();
            session?.Dispose();
            mount?.Dispose();
            if (!sinksTransferred)
                foreach (var sink in sinks)
                    sink.Dispose();
            if (hlsDirectory is not null)
                TryDeleteDirectory(hlsDirectory);
            throw;
        }
    }

    /// <summary>Declares whether playback owns each route. Acquisition alone does not stop filler;
    /// activity-aware sinks yield per track on real submissions and resume after silence or release.</summary>
    public void SetPlaybackActive(bool videoActive, bool audioActive) =>
        _carrier?.SetPlaybackActive(videoActive, audioActive);

    /// <summary>The video leg to attach to the video router (null for audio-only streams).</summary>
    public IVideoOutput? VideoSink => _carrier?.VideoSink ?? _session.VideoSink;

    /// <summary>One audio sink per configured track (attach with the matching channel map).</summary>
    public IReadOnlyList<IAudioOutput> AudioSinks => _carrier?.AudioSinks ?? _session.AudioSinks;

    /// <summary>Every track as one concatenated-channel sink (see the encode session's docs).</summary>
    public IAudioOutput? CombinedAudioSink => _carrier?.CombinedAudioSink ?? _session.CombinedAudioSink;

    /// <summary>The LAN server's bound port (0 when no local server).</summary>
    public int LocalServerPort => _mount?.Port ?? 0;

    /// <summary>The endpoint mount name (URL path segment) of the local server, or null.</summary>
    public string? MountName => _mount is not null ? _mountName : null;

    public LiveStreamStatus GetStatus() => new(
        _session.GetMetrics(),
        LocalServerPort,
        _mount?.ActiveClients ?? 0,
        _mount?.BytesServed ?? 0,
        _tsBuffer is not null ? $"/{_mountName}.ts" : null,
        _hlsDirectory is not null ? $"/{_mountName}/hls/live.m3u8" : null);

    /// <summary>Flushes the encoders, finalizes every destination, stops the LAN server.</summary>
    public async Task StopAsync()
    {
        _carrier?.Dispose();
        try
        {
            await _session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "live stream finish did not complete cleanly");
        }

        _tsBuffer?.Complete();
        _mount?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _carrier?.Dispose();
        _tsBuffer?.Complete();
        MediaDiagnostics.SwallowDisposeErrors(() => _mount?.Dispose(), "LiveStreamSession.Dispose: server mount");
        MediaDiagnostics.SwallowDisposeErrors(_session.Dispose, "LiveStreamSession.Dispose: encode session");
        if (_hlsDirectory is not null)
            TryDeleteDirectory(_hlsDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* scratch dir - best effort */ }
    }
}
