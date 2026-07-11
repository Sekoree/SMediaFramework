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

public sealed record PushTarget(PushProtocol Protocol, string Url);

/// <summary>The built-in LAN server's configuration. Port 0 binds an ephemeral port.</summary>
public sealed record LocalServerOptions(int Port = 8620, bool EnableTs = true, bool EnableHls = true);

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
    private readonly HttpMediaServer? _server;
    private readonly TsFanOutBuffer? _tsBuffer;
    private readonly string? _hlsDirectory;
    private bool _disposed;

    private LiveStreamSession(
        FFmpegEncodeSession session, HttpMediaServer? server, TsFanOutBuffer? tsBuffer, string? hlsDirectory)
    {
        _session = session;
        _server = server;
        _tsBuffer = tsBuffer;
        _hlsDirectory = hlsDirectory;
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
        HttpMediaServer? server = null;
        try
        {
            foreach (var push in options.PushTargets)
            {
                var target = push.Protocol switch
                {
                    PushProtocol.Rtmp => UrlEncodeTarget.Rtmp(push.Url),
                    PushProtocol.Srt => UrlEncodeTarget.Srt(push.Url),
                    PushProtocol.Rtsp => UrlEncodeTarget.Rtsp(push.Url),
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

                server = new HttpMediaServer(local.Port, tsBuffer, hlsDirectory);
            }

            var session = FFmpegEncodeSession.CreateWithSinks(options.Encode, sinks, audioInputSampleRate);
            Trace.LogInformation("live stream started: {Push} push target(s), local server {Server}",
                options.PushTargets.Count, server is not null ? $"port {server.Port}" : "off");
            return new LiveStreamSession(session, server, tsBuffer, hlsDirectory);
        }
        catch
        {
            server?.Dispose();
            foreach (var sink in sinks)
                sink.Dispose();
            if (hlsDirectory is not null)
                TryDeleteDirectory(hlsDirectory);
            throw;
        }
    }

    /// <summary>The video leg to attach to the video router (null for audio-only streams).</summary>
    public IVideoOutput? VideoSink => _session.VideoSink;

    /// <summary>One audio sink per configured track (attach with the matching channel map).</summary>
    public IReadOnlyList<IAudioOutput> AudioSinks => _session.AudioSinks;

    /// <summary>The LAN server's bound port (0 when no local server).</summary>
    public int LocalServerPort => _server?.Port ?? 0;

    public LiveStreamStatus GetStatus() => new(
        _session.GetMetrics(),
        LocalServerPort,
        _server?.ActiveClients ?? 0,
        _server?.BytesServed ?? 0,
        _tsBuffer is not null ? "/stream.ts" : null,
        _hlsDirectory is not null ? "/hls/live.m3u8" : null);

    /// <summary>Flushes the encoders, finalizes every destination, stops the LAN server.</summary>
    public async Task StopAsync()
    {
        try
        {
            await _session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "live stream finish did not complete cleanly");
        }

        _tsBuffer?.Complete();
        _server?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _tsBuffer?.Complete();
        MediaDiagnostics.SwallowDisposeErrors(() => _server?.Dispose(), "LiveStreamSession.Dispose: server");
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
