using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.PortAudio;
using S.Media.SDL3;
using VideoPlaybackSmoke;

Console.OutputEncoding = Encoding.UTF8;

if (!PlaybackCli.TryParse(args, out var opt, out var usageErr))
{
    PlaybackCli.WriteUsageToStdErr();
    if (!string.IsNullOrEmpty(usageErr))
        Console.Error.WriteLine(usageErr);
    return string.IsNullOrEmpty(usageErr) ? 0 : 2;
}

FFmpegRuntime.EnsureInitialized();

if (!File.Exists(opt.MediaPath))
{
    Console.Error.WriteLine($"file not found: {opt.MediaPath}");
    return 3;
}

VideoDecoderOpenOptions? vOpts = opt.HardwareDecode
    ? new VideoDecoderOpenOptions { TryHardwareAcceleration = true, RetainDmabufForGl = opt.LinuxDrmDmabufGl }
    : null;

using var videoDecoder = VideoFileDecoder.Open(opt.MediaPath, vOpts);

var sdlGl = new SDL3GLVideoSink(
    title: Path.GetFileName(opt.MediaPath),
    initialWidth: Math.Min(videoDecoder.Format.Width, 1920),
    initialHeight: Math.Min(videoDecoder.Format.Height, 1080));

TwinCpuVideoSink? twin = null;
NDIOutput? ndi = null;
IVideoSink videoFront = sdlGl;

if (opt.NdiEnable)
{
    ndi = new NDIOutput(opt.NdiName, clockVideo: false, clockAudio: false,
        minimumVideoSubmitSpacing: PaceBelowFramePeriod(videoDecoder));

    twin = new TwinCpuVideoSink(sdlGl, ndi.VideoSink);
    videoFront = twin;
}

AudioRouting? routing = AudioRouting.TryCreate(opt.MediaPath, opt.AudioChunkSamples);
using var freerunClock = new MediaClock();

IMediaClock playClock = routing?.Player.Clock ?? freerunClock;

using VideoPlayer videoPlayer = new(videoDecoder, videoFront, playClock);

try
{
    Console.WriteLine(
        $"{Path.GetFileName(opt.MediaPath)}  video {videoDecoder.Format.Width}x{videoDecoder.Format.Height} " +
        $"{videoDecoder.Format.PixelFormat} @{videoDecoder.Format.FrameRate}");

    if (routing is null)
        Console.WriteLine("[audio] not wired — see stderr above; video clock is wall-time-only.");

    if (opt.NdiEnable && opt.LinuxDrmDmabufGl)
        Console.WriteLine("[ndi] DRM dma-buf decode + twin NDI is unsupported — omit --ndi or disable --drm-gl.");

    using CancellationTokenSource cts = new();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    sdlGl.CloseRequested += (_, _) => cts.Cancel();

    if (ndi is not null && routing is not null)
    {
        var ndSink = ndi.EnableAudio(routing.AudioFormat);
        var ndiSinkId = routing.Player.AddOutput(ndSink);

        routing.Player.Connect(routing.SourceId, ndiSinkId,
            ChannelMap.Identity(Math.Min(2, routing.AudioFormat.Channels)));
    }

    if (routing != null)
    {
        // PortAudio only pulls from Submit() once the native stream is started;
        // AudioPlayer.Play() starts the router, not hardware outputs (see PlaybackSmoke).
        routing.StartHardwareOutput();
        routing.Player.Play();
    }
    else
        freerunClock.Start();

    videoPlayer.Play();

    var ticker = System.Diagnostics.Stopwatch.StartNew();
    while (!cts.IsCancellationRequested)
    {
        if (ticker.ElapsedMilliseconds >= 650)
        {
            var adeckPos = routing?.Decoder.Position ?? TimeSpan.Zero;

            Console.Write(
                $"\r clock {FormatClock(playClock.CurrentPosition)}  vPTS {videoDecoder.Position:mm\\:ss\\.fff}  " +
                $"audio {adeckPos:mm\\:ss\\.fff}  shown {videoPlayer.DisplayedCount} / decoded {videoPlayer.DecodedCount}");
            Console.Out.Flush();

            ticker.Restart();
        }

        Thread.Sleep(25);

        var audioNaturally = routing?.Player.Router.CompletedNaturally ?? true;

        if (videoPlayer.CompletedNaturally && videoDecoder.IsAtEnd && audioNaturally)
            break;
    }

    Console.WriteLine();
    videoPlayer.Stop(cts.Token);
    routing?.Player.Stop();
    freerunClock.Stop(cts.Token);
}
finally
{
    routing?.Dispose();
    twin?.Dispose();
    if (twin is null)
        sdlGl.Dispose();

    ndi?.Dispose();
}

Console.WriteLine("done.");
return 0;

static string FormatClock(TimeSpan t) =>
    $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

static TimeSpan PaceBelowFramePeriod(VideoFileDecoder dec)
{
    var fps = dec.Format.FrameRate.ToDouble();
    if (fps <= 0 || double.IsNaN(fps)) return TimeSpan.Zero;
    return TimeSpan.FromSeconds(1.0 / fps * 0.93);
}

file sealed class AudioRouting : IDisposable
{
    private readonly PortAudioOutput _mainOutput;

    private AudioRouting(AudioPlayer player, AudioFileDecoder decoder, string sourceId, PortAudioOutput mainOutput)
    {
        Player = player;
        Decoder = decoder;
        SourceId = sourceId;
        AudioFormat = decoder.Format;
        _mainOutput = mainOutput;
    }

    internal AudioPlayer Player { get; }
    internal AudioFileDecoder Decoder { get; }
    internal string SourceId { get; }

    internal AudioFormat AudioFormat { get; }

    internal static AudioRouting? TryCreate(string mediaPath, int chunkSamples)
    {
        try
        {
            var decoder = AudioFileDecoder.Open(mediaPath);
            var player = new AudioPlayer(decoder.Format.SampleRate, chunkSamples);

            // Match PlaybackSmoke: second ctor arg is deviceIndex, not sample rate.
            using (var probe = new PortAudioOutput(decoder.Format))
                _ = probe.DeviceIndex;

            var output = new PortAudioOutput(decoder.Format, ringCapacityFrames: decoder.Format.SampleRate);

            string sourceId = player.AddOwnedSource(decoder);
            string sinkMain = player.AddOutput(output); // pacing + playback clock wiring
            player.Connect(sourceId, sinkMain);

            return new AudioRouting(player, decoder, sourceId, output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] could not wire PortAudio/ffmpeg: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opens the native PortAudio stream (must run before <see cref="AudioPlayer.Play"/>).</summary>
    internal void StartHardwareOutput() => _mainOutput.Start();

    public void Dispose()
    {
        Player.Dispose();
        _mainOutput.Dispose();
    }
}

internal readonly record struct PlaybackOptions(
    string MediaPath,
    bool NdiEnable,
    string NdiName,
    bool HardwareDecode,
    bool LinuxDrmDmabufGl,
    int AudioChunkSamples);

file static class PlaybackCli
{
    public static bool TryParse(string[] argv, out PlaybackOptions opt, out string? errorText)
    {
        opt = default;
        errorText = null;

        bool ndi = false;
        string ndiName = "MFPlayer PlaybackVideo";

        bool hw = false;
        bool drmGl = false;
        int chunkSamples = 480;

        var positional = new List<string>();

        for (var i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (!a.StartsWith('-'))
            {
                positional.Add(a);
                continue;
            }

            switch (a)
            {
                case "--ndi":
                    ndi = true;
                    if (i + 1 < argv.Length && !argv[i + 1].StartsWith('-'))
                        ndiName = argv[++i];
                    break;
                case "--hw":
                    hw = true;
                    break;
                case "--drm-gl":
                    drmGl = true;
                    break;
                default:
                    if (TryParseKeyedInt(a, "--chunk-samples=", out var ck) && ck > 0)
                    {
                        chunkSamples = ck;
                        break;
                    }

                    errorText = $"unknown option: {a}";
                    return false;
            }
        }

        if (positional.Count != 1)
        {
            errorText = "expected exactly one media file path";
            return false;
        }

        opt = new PlaybackOptions(positional[0], ndi, ndiName, hw, OperatingSystem.IsLinux() && drmGl, chunkSamples);
        return true;
    }

    private static bool TryParseKeyedInt(string arg, string prefix, out int value)
    {
        value = 0;
        if (!arg.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return int.TryParse(arg.AsSpan(prefix.Length), out value);
    }

    public static void WriteUsageToStdErr()
    {
        Console.Error.WriteLine(
            """
usage: VideoPlaybackSmoke <media-file> [options]

Smoke test for video + mastered audio clock (SDL3 GL by default).

  --ndi [name]        Also send mirrored video/audio over NDI (CPU video copy — not paired with DRM dma-buf decode).
  --hw                Prefer FFmpeg hardware decode when available.
  --drm-gl            (Linux only, with --hw) Prefer DRM PRIME EGL NV12 dma-bufs to GL.

  --chunk-samples=n   AudioRouter chunk span (default 480 ≈ 10 ms @ 48 kHz).
""");
    }
}
