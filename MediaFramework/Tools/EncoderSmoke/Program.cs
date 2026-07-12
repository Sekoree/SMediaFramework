// EncoderSmoke - manual eyeball tool for the encode/stream stack (revives the pre-rewrite tool for
// the packet-fanout session). Generates a moving test pattern + 440 Hz tone and either records a
// file or goes live (push URL and/or the built-in LAN server).
//
//   dotnet run --project MediaFramework/Tools/EncoderSmoke -- --file /tmp/smoke.mp4 --seconds 10
//   dotnet run --project MediaFramework/Tools/EncoderSmoke -- --serve 8620 --seconds 120
//       → open http://localhost:8620/stream.ts in VLC/mpv, /hls/live.m3u8 in a browser player
//   dotnet run --project MediaFramework/Tools/EncoderSmoke -- --push rtmp://localhost/live/key --seconds 60
//       → point it at a local MediaMTX/OBS ingest to verify RTMP/SRT/RTSP push interactively

using System.Diagnostics;
using S.Media.Core.Video;
using S.Media.Encode.FFmpeg;
using S.Media.Stream.Http;

string? filePath = null;
string? pushUrl = null;
int? servePort = null;
var seconds = 10;
const int width = 640, height = 360, fps = 30, sampleRate = 48_000;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file": filePath = args[++i]; break;
        case "--push": pushUrl = args[++i]; break;
        case "--serve": servePort = int.Parse(args[++i]); break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        default:
            Console.Error.WriteLine($"unknown arg {args[i]}");
            return 2;
    }
}

if (filePath is null && pushUrl is null && servePort is null)
{
    Console.Error.WriteLine("usage: EncoderSmoke (--file <path> | --push <url> | --serve <port>)… [--seconds N]");
    return 2;
}

var encode = new EncodeSessionOptions
{
    Container = filePath is not null && pushUrl is null && servePort is null
        ? EncodeContainer.Mp4
        : EncodeContainer.MpegTs,
    Video = new VideoEncodeOptions { Codec = EncodeVideoCodec.H264, BitrateBps = 2_500_000, Preset = "veryfast", GopSize = fps * 2 },
    AudioLegs = [new AudioLegOptions { Codec = EncodeAudioCodec.Aac, Channels = 2, BitrateBps = 160_000 }],
};
if (encode.Validate() is { Count: > 0 } errors)
{
    foreach (var error in errors)
        Console.Error.WriteLine($"invalid options: {error}");
    return 1;
}

FFmpegEncodeSession? fileSession = null;
LiveStreamSession? liveSession = null;
S.Media.Core.Video.IVideoOutput videoSink;
S.Media.Core.Audio.IAudioOutput audioSink;

if (pushUrl is not null || servePort is not null)
{
    var protocol = pushUrl switch
    {
        null => (PushProtocol?)null,
        var u when u.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase) => PushProtocol.Rtmp,
        var u when u.StartsWith("srt", StringComparison.OrdinalIgnoreCase) => PushProtocol.Srt,
        var u when u.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase) => PushProtocol.Rtsp,
        _ => PushProtocol.Rtmp,
    };
    liveSession = LiveStreamSession.Start(new LiveStreamOptions
    {
        Encode = encode,
        PushTargets = protocol is { } p ? [new PushTarget(p, pushUrl!)] : [],
        LocalServer = servePort is { } port ? new LocalServerOptions(port) : null,
    }, sampleRate);
    videoSink = liveSession.VideoSink!;
    audioSink = liveSession.CombinedAudioSink!;
    if (liveSession.LocalServerPort > 0)
        Console.WriteLine($"LAN: http://localhost:{liveSession.LocalServerPort}/stream.ts | /hls/live.m3u8");
}
else
{
    fileSession = FFmpegEncodeSession.Create(encode, new FileEncodeTarget(filePath!), sampleRate);
    videoSink = fileSession.VideoSink!;
    audioSink = fileSession.CombinedAudioSink!;
    Console.WriteLine($"recording: {filePath}");
}

var format = new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(fps, 1));
videoSink.Configure(format);

// Real-time paced generation: a scrolling gradient + tone, one frame + one audio chunk per tick.
var totalFrames = seconds * fps;
var samplesPerFrame = sampleRate / fps;
var audio = new float[samplesPerFrame * 2];
var started = Stopwatch.GetTimestamp();
for (var f = 0; f < totalFrames; f++)
{
    var stride = width * 4;
    var pixels = new byte[stride * height];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var o = y * stride + x * 4;
        pixels[o] = (byte)((x + f * 4) & 0xFF);
        pixels[o + 1] = (byte)((y + f * 2) & 0xFF);
        pixels[o + 2] = (byte)(((x + y) / 2 + f) & 0xFF);
        pixels[o + 3] = 255;
    }

    videoSink.Submit(new VideoFrame(
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond * f / fps), format, [pixels], [stride]));

    for (var s = 0; s < samplesPerFrame; s++)
    {
        var t = (f * samplesPerFrame + s) / (double)sampleRate;
        var v = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * t));
        audio[s * 2] = v;
        audio[s * 2 + 1] = v;
    }

    audioSink.Submit(audio);

    // Pace to wall clock so live viewers see real-time output (a file render could free-run).
    var target = TimeSpan.FromSeconds((f + 1) / (double)fps);
    var sleep = target - Stopwatch.GetElapsedTime(started);
    if (sleep > TimeSpan.Zero)
        Thread.Sleep(sleep);
    if (f % fps == 0)
        Console.Write($"\r{f / fps + 1}/{seconds}s ");
}

Console.WriteLine("\nfinishing…");
if (liveSession is not null)
{
    await liveSession.StopAsync();
    var status = liveSession.GetStatus();
    Console.WriteLine($"encoded {status.Encode.VideoFramesEncoded} frames; sinks:");
    foreach (var sink in status.Encode.Sinks)
        Console.WriteLine($"  {(sink.Healthy ? "ok " : "ERR")} {sink.Name} ({sink.BytesWritten:N0} B) {sink.Error}");
    liveSession.Dispose();
}

if (fileSession is not null)
{
    await fileSession.FinishAsync();
    var metrics = fileSession.GetMetrics();
    Console.WriteLine($"encoded {metrics.VideoFramesEncoded}/{metrics.VideoFramesSubmitted} frames → {filePath}");
    fileSession.Dispose();
}

return 0;
