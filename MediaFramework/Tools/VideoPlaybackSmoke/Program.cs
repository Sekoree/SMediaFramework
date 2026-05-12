using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
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
        // While PortAudio hasn't started, SinkSlavedRouterClock's WaitForCapacity() returns
        // unconditionally — the router would decode as fast as CPU allows, overfill the ring,
        // and Submit() would drop samples while AudioFileDecoder.Position still advanced
        // ("audio 7s ahead" in the HUD). Match PlaybackSmoke: prebuffer by reading the
        // shared decoder directly into the device ring, open the stream, then start the router.
        routing.PrefillMainOutputDirectFromDecoder(TimeSpan.FromSeconds(3));
        routing.StartHardwareOutput();
        AvPlaybackCoordinator.Play(videoPlayer, routing.Player);
    }
    else
    {
        freerunClock.Start();
        videoPlayer.Play();
    }

    var ticker = System.Diagnostics.Stopwatch.StartNew();
    while (!cts.IsCancellationRequested)
    {
        if (ticker.ElapsedMilliseconds >= 650)
        {
            var aHeard = routing != null
                ? TimeSpan.FromSeconds(routing.MainOutput.PlayedSamples / (double)routing.MainOutput.Format.SampleRate)
                : TimeSpan.Zero;
            var aDeckDec = routing?.Decoder.Position ?? TimeSpan.Zero;
            long pumpDr = 0, paUnd = 0, paDr = 0;
            if (routing != null)
            {
                pumpDr = routing.Player.Router.GetPumpStats(routing.PrimarySinkId).Dropped;
                paUnd = routing.MainOutput.UnderrunSamples;
                paDr = routing.MainOutput.DroppedSamples;
            }

            Console.Write(
                $"\r clock {FormatClock(playClock.CurrentPosition)}  vPTS {videoDecoder.Position:mm\\:ss\\.fff}  " +
                $"aHeard {aHeard:mm\\:ss\\.fff}  aDec {aDeckDec:mm\\:ss\\.fff}  show {videoPlayer.DisplayedCount}/{videoPlayer.DecodedCount}  " +
                $"vLate {videoPlayer.DroppedLate}  paUnd {paUnd}  paDr {paDr}  pumpDr {pumpDr}");
            Console.Out.Flush();

            ticker.Restart();
        }

        Thread.Sleep(25);

        var audioNaturally = routing?.Player.Router.CompletedNaturally ?? true;

        if (videoPlayer.CompletedNaturally && videoDecoder.IsAtEnd && audioNaturally)
            break;
    }

    Console.WriteLine();
    AvPlaybackCoordinator.Pause(videoPlayer, routing?.Player, cts.Token);
    if (routing is null)
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

    private AudioRouting(AudioPlayer player, AudioFileDecoder decoder, string sourceId, PortAudioOutput mainOutput,
                         string primarySinkId)
    {
        Player = player;
        Decoder = decoder;
        SourceId = sourceId;
        AudioFormat = decoder.Format;
        _mainOutput = mainOutput;
        PrimarySinkId = primarySinkId;
    }

    internal AudioPlayer Player { get; }
    internal AudioFileDecoder Decoder { get; }
    internal string SourceId { get; }

    internal AudioFormat AudioFormat { get; }

    /// <summary>Router id of the main <see cref="PortAudioOutput"/> (for <see cref="AudioRouter.GetPumpStats"/>).</summary>
    internal string PrimarySinkId { get; }

    internal PortAudioOutput MainOutput => _mainOutput;

    internal static AudioRouting? TryCreate(string mediaPath, int chunkSamples)
    {
        try
        {
            var decoder = AudioFileDecoder.Open(mediaPath);
            var player = new AudioPlayer(decoder.Format.SampleRate, chunkSamples);

            var output = new PortAudioOutput(
                decoder.Format,
                framesPerBuffer: chunkSamples,
                ringCapacityFrames: decoder.Format.SampleRate);
            // Default target is half the ring (~0.68 s here) — the router runs unthrottled whenever
            // queued + chunk fits, then sleeps in bursts, which interacts badly with Pulse/ALSA jitter.
            // Keep a modest cushion (~8 router chunks ≈ 160 ms @ 960 samples).
            output.TargetQueueSamples = Math.Clamp(chunkSamples * 8, chunkSamples * 4, output.CapacitySamples / 8);

            string sourceId = player.AddOwnedSource(decoder);
            string sinkMain = player.AddOutput(output); // pacing + playback clock wiring
            player.Connect(sourceId, sinkMain);

            return new AudioRouting(player, decoder, sourceId, output, sinkMain);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] could not wire PortAudio/ffmpeg: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prefill the hardware ring by pulling from <see cref="Decoder"/> before any router thread runs
    /// (<see cref="PlaybackSmoke"/> pattern). With <c>--ndi</c>, only PortAudio receives this segment;
    /// the mixer path starts on the next decoded sample (brief NDI audio delay vs speakers).
    /// </summary>
    internal void PrefillMainOutputDirectFromDecoder(TimeSpan timeout)
    {
        var sr = _mainOutput.Format.SampleRate;
        var chunk = Player.Router.ChunkSamples;
        var targetQueued = Math.Max(sr / 10, chunk * 4);
        var ch = Decoder.Format.Channels;
        var bufFloats = Math.Min(65536, Math.Max(chunk * ch * 8, 8192 * ch));
        var buf = new float[bufFloats];
        var deadline = DateTime.UtcNow + timeout;
        while (_mainOutput.QueuedSamples < targetQueued && DateTime.UtcNow < deadline)
        {
            var read = Decoder.ReadInto(buf);
            if (read == 0) break;
            _mainOutput.Submit(buf.AsSpan(0, read));
        }
    }

    /// <summary>Opens the native PortAudio stream (after <see cref="PrefillMainOutputDirectFromDecoder"/>, before <see cref="AudioPlayer.Play"/>).</summary>
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
        int chunkSamples = 960;

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

  --ndi [name]        Mirror video/audio over NDI (CPU copy of decoded frames — incompatible with --drm-gl).
  --hw                Prefer FFmpeg hardware decode when available.
  --drm-gl            (Linux only, with --hw) Prefer DRM PRIME EGL NV12 dma-bufs to GL (do not combine with --ndi).

  --chunk-samples=n   AudioRouter chunk span (default 960 ≈ 20 ms @ 48 kHz).
""");
    }
}
