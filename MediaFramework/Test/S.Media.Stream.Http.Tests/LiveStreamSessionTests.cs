using System.Net.Sockets;
using System.Text;
using Xunit;

namespace S.Media.Stream.Http.Tests;

/// <summary>
/// End-to-end LAN streaming: synthetic frames/PCM through a <see cref="LiveStreamSession"/> with the
/// local server enabled, verified over a REAL TCP connection (TS sync bytes / HLS playlist) - no push
/// servers needed. URL-push coverage is option-validation only (network targets are out of CI scope).
/// </summary>
public sealed class LiveStreamSessionTests
{
    private static VideoFrame MakeBgraFrame(int width, int height, int index, Rational fps)
    {
        var stride = width * 4;
        var bytes = new byte[stride * height];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = (byte)(index * 8 & 0xFF);
            bytes[i + 3] = 255;
        }

        return new VideoFrame(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond * index * fps.Denominator / fps.Numerator),
            new VideoFormat(width, height, PixelFormat.Bgra32, fps),
            [bytes],
            [stride]);
    }

    private static LiveStreamOptions VideoOnlyOptions(LocalServerOptions server) => new()
    {
        Encode = new EncodeSessionOptions
        {
            Container = EncodeContainer.MpegTs,
            OutputMode = EncodeOutputMode.VideoOnly,
            Video = new VideoEncodeOptions { Codec = EncodeVideoCodec.H264, Crf = 35, Preset = "ultrafast", GopSize = 10 },
        },
        LocalServer = server,
    };

    private static bool EncodersAvailable(LiveStreamOptions options) => options.Validate().Count == 0;

    private static async Task PumpFramesAsync(LiveStreamSession session, int count)
    {
        var fps = new Rational(30, 1);
        session.VideoSink!.Configure(new VideoFormat(128, 96, PixelFormat.Bgra32, fps));
        for (var i = 0; i < count; i++)
        {
            session.VideoSink.Submit(MakeBgraFrame(128, 96, i, fps));
            await Task.Delay(5); // let the encode worker + sink drain threads interleave like live playback
        }
    }

    private static async Task<byte[]> HttpGetRawAsync(int port, string path, int maxBytes, TimeSpan timeout)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);

        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (buffer.Length < maxBytes)
            {
                var read = await stream.ReadAsync(chunk, cts.Token);
                if (read == 0)
                    break;
                buffer.Write(chunk, 0, read);
            }
        }
        catch (OperationCanceledException)
        {
            // timeout reached with whatever we have - the TS stream is endless by design
        }

        return buffer.ToArray();
    }

    private static (string Headers, byte[] Body) SplitResponse(byte[] raw)
    {
        var text = Encoding.ASCII.GetString(raw);
        var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(idx > 0, "no HTTP header terminator in response");
        return (text[..idx], raw[(idx + 4)..]);
    }

    [Fact]
    public async Task TsStream_ServesSyncBytePacketsToTcpClient()
    {
        var options = VideoOnlyOptions(new LocalServerOptions(Port: 0, EnableTs: true, EnableHls: false));
        if (!EncodersAvailable(options))
            return;

        using var session = LiveStreamSession.Start(options);
        var pump = PumpFramesAsync(session, 90);

        // Give the encoder a head start so a keyframe join point exists before the client connects.
        await Task.Delay(400);
        var raw = await HttpGetRawAsync(session.LocalServerPort, "/stream.ts", 64 * 1024, TimeSpan.FromSeconds(8));
        await pump;

        var (headers, body) = SplitResponse(raw);
        Assert.Contains("200 OK", headers);
        Assert.Contains("video/mp2t", headers);
        Assert.True(body.Length >= 188 * 4, $"expected several TS packets, got {body.Length} bytes");
        // MPEG-TS: 0x47 sync byte every 188 bytes; joining at a mux packet boundary means offset 0 aligns.
        for (var i = 0; i + 188 <= 188 * 4; i += 188)
            Assert.Equal(0x47, body[i]);

        await session.StopAsync();
    }

    [Fact]
    public async Task Hls_PlaylistAppearsAndListsSegments()
    {
        var options = VideoOnlyOptions(new LocalServerOptions(Port: 0, EnableTs: false, EnableHls: true));
        if (!EncodersAvailable(options))
            return;

        using var session = LiveStreamSession.Start(options);
        // 150 frames @30fps = 5 s of content = at least two 2 s segments.
        await PumpFramesAsync(session, 150);

        // The hls muxer writes the playlist when the first segment completes; poll briefly while live.
        string playlist = "";
        string headers = "";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var raw = await HttpGetRawAsync(session.LocalServerPort, "/hls/live.m3u8", 64 * 1024, TimeSpan.FromSeconds(4));
            (headers, var body) = SplitResponse(raw);
            playlist = Encoding.UTF8.GetString(body);
            if (headers.Contains("200 OK") && playlist.Contains("seg_"))
                break;
            await Task.Delay(250);
        }

        Assert.Contains("200 OK", headers);
        Assert.Contains("application/vnd.apple.mpegurl", headers);
        Assert.Contains("#EXTM3U", playlist);
        Assert.Contains("seg_", playlist);

        await session.StopAsync();
    }

    [Fact]
    public async Task StatusPage_ReportsRoutes()
    {
        var options = VideoOnlyOptions(new LocalServerOptions(Port: 0));
        if (!EncodersAvailable(options))
            return;

        using var session = LiveStreamSession.Start(options);
        var raw = await HttpGetRawAsync(session.LocalServerPort, "/", 8192, TimeSpan.FromSeconds(3));
        var (headers, body) = SplitResponse(raw);

        Assert.Contains("200 OK", headers);
        var text = Encoding.UTF8.GetString(body);
        Assert.Contains("/stream.ts", text);
        Assert.Contains("/hls/live.m3u8", text);

        await session.StopAsync();
    }

    [Fact]
    public async Task UnknownRoute_Returns404_AndTraversalIsRejected()
    {
        var options = VideoOnlyOptions(new LocalServerOptions(Port: 0));
        if (!EncodersAvailable(options))
            return;

        using var session = LiveStreamSession.Start(options);

        var raw = await HttpGetRawAsync(session.LocalServerPort, "/nope", 8192, TimeSpan.FromSeconds(3));
        Assert.Contains("404", SplitResponse(raw).Headers);

        var traversal = await HttpGetRawAsync(session.LocalServerPort, "/hls/..%2fsecrets.ts", 8192, TimeSpan.FromSeconds(3));
        Assert.Contains("404", SplitResponse(traversal).Headers);

        await session.StopAsync();
    }

    [Fact]
    public void Validate_RtmpConstraints_AreEnforcedAcrossDestinations()
    {
        var options = new LiveStreamOptions
        {
            Encode = new EncodeSessionOptions
            {
                Container = EncodeContainer.MpegTs,
                Video = new VideoEncodeOptions { Codec = EncodeVideoCodec.Hevc },
                AudioLegs = [new AudioLegOptions(), new AudioLegOptions { Codec = EncodeAudioCodec.Opus }],
            },
            PushTargets = [new PushTarget(PushProtocol.Rtmp, "rtmp://live.example/app/key")],
        };

        var errors = options.Validate(probeEncoders: false);
        Assert.Contains(errors, e => e.Contains("H.264"));
        Assert.Contains(errors, e => e.Contains("single audio track"));
    }

    [Fact]
    public void Validate_RequiresAtLeastOneDestination_AndSchemeMatch()
    {
        var none = new LiveStreamOptions();
        Assert.Contains(none.Validate(probeEncoders: false), e => e.Contains("no destination"));

        var badScheme = new LiveStreamOptions
        {
            PushTargets = [new PushTarget(PushProtocol.Srt, "rtmp://wrong")],
        };
        Assert.Contains(badScheme.Validate(probeEncoders: false), e => e.Contains("srt://"));
    }

    [Fact]
    public void TsFanOutBuffer_JoinsAtKeyframeBoundary_AndEvictsSlowClients()
    {
        var buffer = new TsFanOutBuffer();

        // Packet 1 (non-key), packet 2 (KEY), packet 3 (non-key).
        buffer.OnBytes(new byte[] { 1, 1, 1 });
        buffer.OnPacketBoundary(videoKeyframe: false);
        buffer.OnBytes(new byte[] { 2, 2, 2 });
        buffer.OnPacketBoundary(videoKeyframe: true); // key packet occupied [3,6) → join offset 3
        buffer.OnBytes(new byte[] { 3, 3, 3 });
        buffer.OnPacketBoundary(videoKeyframe: false);

        var reader = buffer.Register(out var registration);
        Assert.True(reader.TryRead(out var first));
        Assert.Equal(new byte[] { 2, 2, 2 }, first); // history starts AT the keyframe packet
        Assert.True(reader.TryRead(out var second));
        Assert.Equal(new byte[] { 3, 3, 3 }, second);
        buffer.Unregister(registration);

        // Slow client: never reads → channel fills → evicted on overflow.
        buffer.Register(out _);
        for (var i = 0; i < 400; i++)
            buffer.OnBytes(new byte[] { 9 });
        Assert.Equal(0, buffer.ClientCount);
        Assert.Equal(1, buffer.EvictedClients);
    }
}
