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
    /// <summary>The full URL handed to libavformat, folding in <see cref="StreamKey"/> per protocol.</summary>
    public string ResolveUrl()
    {
        if (string.IsNullOrWhiteSpace(StreamKey))
            return Url;
        var key = StreamKey.Trim();
        return Protocol switch
        {
            // rtmp://host/app  +  /streamkey  (the classic ingest pattern).
            PushProtocol.Rtmp => Url.TrimEnd('/') + "/" + key,
            // srt://host:port?streamid=key (appended to any existing query).
            PushProtocol.Srt => Url + (Url.Contains('?') ? "&" : "?") + "streamid=" + Uri.EscapeDataString(key),
            // RTSP auth is user:pass in the URL; a bare key isn't standard - append as a path segment.
            PushProtocol.Rtsp => Url.TrimEnd('/') + "/" + key,
            _ => Url,
        };
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
    public IReadOnlyList<string> Validate(bool probeEncoders = true)
    {
        var errors = new List<string>(Encode.Validate(probeEncoders));

        if (PushTargets.Count == 0 && LocalServer is null)
            errors.Add("The stream has no destination: add a push target or enable the local server.");

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
    private readonly StreamKeepAlive? _keepAlive;
    private bool _disposed;

    private LiveStreamSession(
        FFmpegEncodeSession session, HttpMediaServer.MountHandle? mount, TsFanOutBuffer? tsBuffer,
        string? hlsDirectory, string mountName, StreamKeepAlive? keepAlive)
    {
        _session = session;
        _mount = mount;
        _tsBuffer = tsBuffer;
        _hlsDirectory = hlsDirectory;
        _mountName = mountName;
        _keepAlive = keepAlive;
    }

    public static LiveStreamSession Start(LiveStreamOptions options, int audioInputSampleRate = 48_000)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid stream options: {string.Join(" | ", errors)}", nameof(options));

        var sinks = new List<IEncodedPacketSink>();
        TsFanOutBuffer? tsBuffer = null;
        string? hlsDirectory = null;
        HttpMediaServer.MountHandle? mount = null;
        StreamKeepAlive? keepAlive = null;
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
                var container = push.Protocol == PushProtocol.Rtmp ? EncodeContainer.Flv : EncodeContainer.MpegTs;
                sinks.Add(new AsyncPacketSink(new MuxPacketSink(target, container)));
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
            }

            var session = FFmpegEncodeSession.CreateWithSinks(options.Encode, sinks, audioInputSampleRate);

            // Keep-alive: stream blank video + silence at the locked format from the moment we go live,
            // so a client that connects before any media plays sees a valid running stream (not a
            // never-starting one). Yields the moment real playback drives the sinks. Runs for video,
            // audio-only (silence), or both - whatever the output mode carries.
            if (options.Encode.IncludesVideo || options.Encode.IncludesAudio)
            {
                keepAlive = new StreamKeepAlive(
                    session,
                    options.Encode.IncludesVideo ? options.Encode.Video.ScaleWidth : 0,
                    options.Encode.IncludesVideo ? options.Encode.Video.ScaleHeight : 0,
                    options.Encode.IncludesVideo ? Math.Max(1, options.Encode.Video.Fps) : 0,
                    options.Encode.IncludesAudio ? audioInputSampleRate : 0,
                    options.Encode.IncludesAudio && options.Encode.AudioLegs.Count > 0
                        ? options.Encode.AudioLegs.Sum(l => l.Channels > 0 ? l.Channels : 2)
                        : 0);
                keepAlive.Start();
            }

            Trace.LogInformation("live stream started: {Push} push target(s), local server {Server}",
                options.PushTargets.Count, mount is not null ? $"port {mount.Port} /{mountName}" : "off");
            return new LiveStreamSession(session, mount, tsBuffer, hlsDirectory, mountName, keepAlive);
        }
        catch
        {
            keepAlive?.Dispose();
            mount?.Dispose();
            foreach (var sink in sinks)
                sink.Dispose();
            if (hlsDirectory is not null)
                TryDeleteDirectory(hlsDirectory);
            throw;
        }
    }

    /// <summary>Signals that real playback is (or is not) driving the sinks, so the blank keep-alive
    /// yields to media and resumes when playback stops. Called by the runtime on acquire/release.</summary>
    public void SetPlaybackActive(bool videoActive, bool audioActive) =>
        _keepAlive?.SetPlaybackActive(videoActive, audioActive);

    /// <summary>The video leg to attach to the video router (null for audio-only streams).</summary>
    public IVideoOutput? VideoSink => _session.VideoSink;

    /// <summary>One audio sink per configured track (attach with the matching channel map).</summary>
    public IReadOnlyList<IAudioOutput> AudioSinks => _session.AudioSinks;

    /// <summary>Every track as one concatenated-channel sink (see the encode session's docs).</summary>
    public IAudioOutput? CombinedAudioSink => _session.CombinedAudioSink;

    /// <summary>The LAN server's bound port (0 when no local server).</summary>
    public int LocalServerPort => _mount?.Port ?? 0;

    /// <summary>The endpoint mount name (URL path segment) of the local server, or null.</summary>
    public string? MountName => _mount is not null ? _mountName : null;

    public LiveStreamStatus GetStatus() => new(
        _session.GetMetrics(),
        LocalServerPort,
        _mount?.ActiveClients ?? 0,
        0,
        _tsBuffer is not null ? $"/{_mountName}.ts" : null,
        _hlsDirectory is not null ? $"/{_mountName}/hls/live.m3u8" : null);

    /// <summary>Flushes the encoders, finalizes every destination, stops the LAN server.</summary>
    public async Task StopAsync()
    {
        _keepAlive?.Dispose();
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
        _keepAlive?.Dispose();
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
