using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;
using S.Media.OpenGL;
using S.Media.PortAudio;
using S.Media.SDL3;

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

// NDI packs from CPU plane memory; GPU NV12 (Linux dma-buf / Win32 shared) has empty stubs.
var videoDecoderOptions = new VideoDecoderOpenOptions
{
    TryHardwareAcceleration = !opt.NoHardwareDecode && !opt.NdiEnable,
    RetainDmabufForGl = opt.LinuxDrmDmabufGl,
    RetainD3D11SharedHandleForGl = opt.WindowsD3d11SharedGl,
};
using var media = MediaContainerDecoder.Open(opt.MediaPath, videoDecoderOptions);
media.SeekPresentation(TimeSpan.Zero);

nint borrowD3d = 0;
if (OperatingSystem.IsWindows() && opt.WindowsD3d11SharedGl)
    media.TryGetHardwareD3D11DeviceForWin32Gl(out borrowD3d);

var videoSource = media.Video;
var sdlGl = new SDL3GLVideoSink(
    title: Path.GetFileName(opt.MediaPath),
    initialWidth: Math.Min(videoSource.Format.Width, 1920),
    initialHeight: Math.Min(videoSource.Format.Height, 1080),
    borrowD3D11DeviceComPtrForNv12Gl: borrowD3d);

VideoRouter? videoRouter = null;
string? ndiVideoRouterInputId = null;
NDIOutput? ndi = null;
string? ndiVideoOutputId = null;
NdiAudioAggregatingSink? ndiAudioAgg = null;
IAudioSink? ndiMirrorPrefill = null;
IVideoSink videoFront = sdlGl;
string? ndiAudioSinkId = null;
VideoPtsClock? videoPtsClock = null;
Action<TimeSpan>? videoPtsHook = null;

if (opt.NdiEnable)
{
    var wallPace = opt.NdiDisableWallPace ? (TimeSpan?)null : PaceBelowFramePeriod(videoSource.Format);
    ndi = new NDIOutput(opt.NdiName, clockVideo: opt.NdiClockVideo, clockAudio: true,
        minimumVideoSubmitSpacing: wallPace,
        videoTimecodeMode: opt.NdiVideoTimecodeMode);

    videoRouter = new VideoRouter(null);
    var outSdl = videoRouter.AddOutput(sdlGl, "sdl", disposeSinkOnRouterDispose: true);
    ndiVideoOutputId = videoRouter.AddOutput(ndi.VideoSink, "ndi", disposeSinkOnRouterDispose: true,
        asyncPump: new VideoSinkPumpAttachOptions(opt.NdiVideoPumpFrames, "ndi-video", null,
            DisposeInnerSinkWhenPumpDisposes: false));
    var mainIn = videoRouter.AddInput(outSdl);
    ndiVideoRouterInputId = mainIn.Id;
    if (!videoRouter.TryAddRoute(mainIn.Id, ndiVideoOutputId, out var routeErr))
        throw new InvalidOperationException(routeErr ?? "VideoRouter.TryAddRoute(ndi) failed");
    videoFront = mainIn.Sink;
}

AudioRouting? routing = AudioRouting.TryCreate(media, opt.AudioChunkSamples, opt.DeviceLatencyMs);
using var freerunClock = new MediaClock();

IMediaClock playClock = routing?.Player.Clock ?? freerunClock;

using VideoPlayer videoPlayer = new(videoSource, videoFront, playClock);
LogVideoPixelRouting(videoSource, videoPlayer, videoRouter, ndiVideoRouterInputId);
var playbackSession = new MediaPlaybackSession(videoPlayer, playClock, routing?.Player);

try
{
    Console.WriteLine(
        $"{Path.GetFileName(opt.MediaPath)}  video {videoSource.Format.Width}x{videoSource.Format.Height} " +
        $"{videoSource.Format.PixelFormat} @{videoSource.Format.FrameRate}");

    if (routing is null)
        Console.WriteLine("[audio] not wired — see stderr above; using VideoPtsClock as MediaClock master.");

    if (opt.NdiEnable && !opt.NoHardwareDecode)
        Console.WriteLine("[ndi] Software video decode is used with --ndi so NDI can read pixel bytes (GPU NV12 has no CPU path here).");

    if (opt.NdiEnable && opt.LinuxDrmDmabufGl)
        Console.WriteLine("[ndi] DRM dma-buf decode + NDI branch routing is unsupported — omit --ndi or disable --drm-gl.");

    if (opt.NdiEnable && opt.WindowsD3d11SharedGl)
        Console.WriteLine("[ndi] D3D11 shared-handle NV12 decode + NDI branch routing is unsupported — omit --ndi or disable --d3d11-gl.");

    if (opt.NdiEnable)
        Console.WriteLine("[ndi] SDL mirrors local video; PortAudio still plays to the default device — use the HUD show/decoded counts if the window stays blank.");

    using CancellationTokenSource cts = new();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    sdlGl.CloseRequested += (_, _) => cts.Cancel();

    if (ndi is not null && routing is not null)
    {
        IAudioSink ndAudio = ndi.EnableAudio(routing.AudioFormat);
        var agg = opt.NdiAudioAggregateSamples;
        if (agg < 0)
        {
            var fps = videoSource.Format.FrameRate.ToDouble();
            agg = fps > 0 && !double.IsNaN(fps)
                ? (int)Math.Clamp(routing.AudioFormat.SampleRate / fps, 960, 8192)
                : 2000;
        }

        if (agg > 0)
        {
            ndiAudioAgg = new NdiAudioAggregatingSink(ndAudio, agg);
            ndAudio = ndiAudioAgg;
        }

        ndiAudioSinkId = routing.Player.AddOutput(ndAudio, sinkPumpCapacityChunks: opt.NdiAudioPumpCapacityChunks);

        routing.Player.Connect(routing.SourceId, ndiAudioSinkId,
            ChannelMap.Identity(Math.Min(2, routing.AudioFormat.Channels)));

        ndiMirrorPrefill = ndAudio;
    }

    if (routing != null)
    {
        // While PortAudio hasn't started, SinkSlavedRouterClock's WaitForCapacity() returns
        // unconditionally — the router would decode as fast as CPU allows, overfill the ring,
        // and Submit() would drop samples while mux audio Position still advanced
        // ("audio 7s ahead" in the HUD). Match PlaybackSmoke: prebuffer by reading the
        // shared decoder directly into the device ring, open the stream, then start the router.
        routing.PrefillMainOutputDirectFromDecoder(TimeSpan.FromSeconds(3),
            mirrorPackedFloats: ndiMirrorPrefill);
        routing.StartHardwareOutput();
        playbackSession.Play();
    }
    else
    {
        videoPtsClock = new VideoPtsClock();
        var vpt = videoPtsClock;
        var begun = false;
        videoPtsHook = pts =>
        {
            if (!begun)
            {
                vpt.BeginSession(pts);
                begun = true;
            }
            else
            {
                vpt.NotifyFramePts(pts);
            }
        };
        videoPlayer.FramePresentationTimePresented += videoPtsHook;
        playbackSession.Play(videoOnlyMaster: videoPtsClock);
    }

    var ticker = System.Diagnostics.Stopwatch.StartNew();
    long lastHudDisplayed = 0;
    while (!cts.IsCancellationRequested)
    {
        if (ticker.ElapsedMilliseconds >= 650)
        {
            var intervalSec = ticker.Elapsed.TotalSeconds;
            var dShown = videoPlayer.DisplayedCount - lastHudDisplayed;
            lastHudDisplayed = videoPlayer.DisplayedCount;
            var vFps = intervalSec > 1e-6 ? dShown / intervalSec : 0.0;
            var nomFps = videoSource.Format.FrameRate.ToDouble();
            var nomFpsStr = nomFps > 0 && !double.IsNaN(nomFps) ? $"{nomFps:0.##}Hz" : "?Hz";

            var aHeard = routing != null
                ? TimeSpan.FromSeconds(routing.MainOutput.PlayedSamples / (double)routing.MainOutput.Format.SampleRate)
                : TimeSpan.Zero;
            var aDeckDec = (routing?.Container.Audio as ISeekableSource)?.Position ?? TimeSpan.Zero;
            long pumpDr = 0, paUnd = 0, paDr = 0, ndiDr = 0;
            long ndiVidDr = 0;
            int ndiVidQ = 0;
            if (routing != null)
            {
                pumpDr = routing.Player.Router.GetPumpStats(routing.PrimarySinkId).Dropped;
                paUnd = routing.MainOutput.UnderrunSamples;
                paDr = routing.MainOutput.DroppedSamples;
                if (ndiAudioSinkId is not null)
                    ndiDr = routing.Player.Router.GetPumpStats(ndiAudioSinkId).Dropped;
            }

            if (videoRouter is not null && ndiVideoOutputId is not null
                && videoRouter.TryGetVideoSinkPumpMetrics(ndiVideoOutputId, out var vpMetrics))
            {
                ndiVidDr = vpMetrics.DroppedFrames;
                ndiVidQ = vpMetrics.CurrentQueuedDepth;
            }

            var vPts = (videoSource as ISeekableSource)?.Position ?? TimeSpan.Zero;
            Console.Write(
                $"\r clock {FormatClock(playClock.CurrentPosition)}  vPTS {vPts:mm\\:ss\\.fff}  " +
                $"aHeard {aHeard:mm\\:ss\\.fff}  aDec {aDeckDec:mm\\:ss\\.fff}  " +
                $"show {videoPlayer.DisplayedCount}/{videoPlayer.DecodedCount}  vFps~{vFps:0.#}  nom {nomFpsStr}  " +
                $"mux shared  vLate {videoPlayer.DroppedLate}  vDrn {videoPlayer.DroppedDrain}  " +
                $"glDr {sdlGl.DroppedNewer}  ndiVidDr {ndiVidDr}  ndiVidQ {ndiVidQ}  paUnd {paUnd}  paDr {paDr}  pumpDr {pumpDr}  ndiAuDr {ndiDr}");
            Console.Out.Flush();

            ticker.Restart();
        }

        var audioNaturally = routing?.Player.Router.CompletedNaturally ?? true;
        var routerEof = routing?.Player.Router.CompletedNaturally == true;

        if (videoSource.IsExhausted && audioNaturally &&
            (videoPlayer.CompletedNaturally || routerEof))
            break;

        // Wake promptly on Ctrl+C (CTS) instead of always sleeping a full slice.
        _ = cts.Token.WaitHandle.WaitOne(25);
    }

    Console.WriteLine();
    try
    {
        playbackSession.Pause(CancellationToken.None, media.FlushCodecPipelines);
    }
    catch (OperationCanceledException)
    {
        // Defensive: coordinator uses None today; keep smoke from dying on cooperative cancel.
    }
    videoPtsClock?.Pause();
    if (routing is null)
        freerunClock.Stop(CancellationToken.None);
}
finally
{
    if (videoPtsHook is not null)
        videoPlayer.FramePresentationTimePresented -= videoPtsHook;

    ndiAudioAgg?.Dispose();
    routing?.Dispose();
    videoRouter?.Dispose();
    if (videoRouter is null)
        sdlGl.Dispose();

    ndi?.Dispose();
}

Console.WriteLine("done.");
return 0;

static void LogVideoPixelRouting(
    IVideoSource videoSourceAfterNegotiate,
    VideoPlayer videoPlayer,
    VideoRouter? router,
    string? routerInputId)
{
    var negotiated = videoPlayer.Format;
    var decoderNative = false;
    for (var i = 0; i < videoSourceAfterNegotiate.NativePixelFormats.Count; i++)
    {
        if (videoSourceAfterNegotiate.NativePixelFormats[i] == negotiated.PixelFormat)
        {
            decoderNative = true;
            break;
        }
    }

    if (router is not null
        && !string.IsNullOrEmpty(routerInputId)
        && router.TryGetInputFanOutPixelFormats(routerInputId, out var neg, out var per)
        && per is { Count: > 0 })
    {
        var dec = decoderNative
            ? "decoder emits negotiated pixel format natively"
            : "decoder converts to negotiated format in FFmpeg (codec / sws / upload path)";
        var segments = new List<string>(per.Count + 1)
        {
            $"negotiated {neg.PixelFormat} @ {neg.Width}x{neg.Height} ({dec})"
        };

        for (var i = 0; i < per.Count; i++)
        {
            var r = per[i];
            var isPrimary = i == 0;
            var label = r.OutputId switch
            {
                "sdl" => "SDL local",
                "ndi" => "NDI",
                _ => r.OutputId
            };

            string tail;
            if (r.UsesRouterCpuConverter)
            {
                tail =
                    $"{r.PixelFormat} — router CPU fan-out (VideoCpuFrameConverter / swscale from {neg.PixelFormat})";
            }
            else if (isPrimary)
            {
                var gl = YuvVideoRenderer.SupportedPixelFormats.Contains(r.PixelFormat);
                tail = gl
                    ? $"{r.PixelFormat} — same as negotiated; OpenGL shader upload (direct)"
                    : $"{r.PixelFormat} — same as negotiated (check OpenGL / YuvVideoRenderer support)";
            }
            else
            {
                tail =
                    $"{r.PixelFormat} — no router CPU conversion on this branch (duplicate or shared backing)";
            }

            segments.Add($"{label}: {tail}");
        }

        Console.WriteLine("[video] " + string.Join("; ", segments));
        return;
    }

    var glDirect = YuvVideoRenderer.SupportedPixelFormats.Contains(negotiated.PixelFormat);
    Console.WriteLine(
        "[video] " +
        $"{negotiated.PixelFormat} @ {negotiated.Width}x{negotiated.Height} — " +
        (decoderNative
            ? "decoder emits format natively; "
            : "decoder converts to negotiated in FFmpeg; ") +
        (glDirect
            ? "SDL GL uses OpenGL shader upload (direct)."
            : "SDL GL: verify OpenGL support for this pixel format."));
}

static string FormatClock(TimeSpan t) =>
    $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

static TimeSpan PaceBelowFramePeriod(VideoFormat fmt)
{
    var fps = fmt.FrameRate.ToDouble();
    if (fps <= 0 || double.IsNaN(fps)) return TimeSpan.Zero;
    return TimeSpan.FromSeconds(1.0 / fps * 0.93);
}

file sealed class AudioRouting : IDisposable
{
    private readonly PortAudioOutput _mainOutput;

    private AudioRouting(AudioPlayer player, MediaContainerDecoder container, string sourceId, PortAudioOutput mainOutput,
                         string primarySinkId)
    {
        Player = player;
        Container = container;
        SourceId = sourceId;
        AudioFormat = container.Audio.Format;
        _mainOutput = mainOutput;
        PrimarySinkId = primarySinkId;
    }

    internal AudioPlayer Player { get; }
    /// <summary>Shared demux — audio is registered on <see cref="AudioPlayer.Router"/> without player ownership.</summary>
    internal MediaContainerDecoder Container { get; }
    internal string SourceId { get; }

    internal AudioFormat AudioFormat { get; }

    /// <summary>Router id of the main <see cref="PortAudioOutput"/> (for <see cref="AudioRouter.GetPumpStats"/>).</summary>
    internal string PrimarySinkId { get; }

    internal PortAudioOutput MainOutput => _mainOutput;

    internal static AudioRouting? TryCreate(MediaContainerDecoder container, int chunkSamples, int? deviceLatencyMs = null)
    {
        try
        {
            var audioSource = container.Audio;
            var player = new AudioPlayer(audioSource.Format.SampleRate, chunkSamples);

            double? latencySec = deviceLatencyMs is > 0 ? deviceLatencyMs.Value / 1000.0 : null;
            var output = new PortAudioOutput(
                audioSource.Format,
                deviceIndex: null,
                suggestedLatency: latencySec,
                framesPerBuffer: chunkSamples,
                ringCapacityFrames: audioSource.Format.SampleRate);
            // Target ~16 chunks (≈320 ms @ 960 samples / 48 kHz), capped at ring/3, floored at 4 chunks.
            // Avoids Math.Clamp throwing when capacity/8 < 4*chunk and reduces mid-stream underruns vs a 160 ms cap.
            var cap = output.CapacitySamples;
            var floor = chunkSamples * 4;
            var target = Math.Max(floor, Math.Min(chunkSamples * 16, cap / 3));
            if (latencySec is { } s && s > 0)
            {
                var latencySamples = (int)(audioSource.Format.SampleRate * s);
                target = Math.Max(target, Math.Min(latencySamples * 2, cap / 2));
            }

            output.TargetQueueSamples = target;

            // Borrowed source: container owns mux/audio disposal; do not use AddOwnedSource.
            string sourceId = player.Router.AddSource(audioSource);
            string sinkMain = player.AddOutput(output); // pacing + playback clock wiring
            player.Connect(sourceId, sinkMain);

            return new AudioRouting(player, container, sourceId, output, sinkMain);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] could not wire PortAudio/ffmpeg: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Prefill the hardware ring by pulling from <see cref="MediaContainerDecoder.Audio"/> before any router thread runs
    /// (same ordering as other smoke tools: prefill before router/device pacing). Delegates to
    /// <see cref="AudioPlayerPortAudioExtensions.TryPrefillPrimaryPortAudio"/>.
    /// Optionally mirrors the same PCM into <paramref name="mirrorPackedFloats"/>
    /// (for example an <see cref="NDIAudioSink"/> wrapper) so NDI audio starts aligned with PortAudio prefill.
    /// </summary>
    internal void PrefillMainOutputDirectFromDecoder(TimeSpan timeout, IAudioSink? mirrorPackedFloats = null)
    {
        if (!Player.TryPrefillPrimaryPortAudio(Container.Audio, timeout, mirrorPackedFloats))
            throw new InvalidOperationException("Primary sink must be PortAudio for hardware prefill.");
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
    bool NoHardwareDecode,
    bool LinuxDrmDmabufGl,
    bool WindowsD3d11SharedGl,
    int AudioChunkSamples,
    int? DeviceLatencyMs,
    int NdiAudioAggregateSamples,
    int? NdiAudioPumpCapacityChunks,
    bool NdiClockVideo,
    bool NdiDisableWallPace,
    int NdiVideoPumpFrames,
    NDIVideoTimecodeMode NdiVideoTimecodeMode);

file static class PlaybackCli
{
    public static bool TryParse(string[] argv, out PlaybackOptions opt, out string? errorText)
    {
        opt = default;
        errorText = null;

        bool ndi = false;
        string ndiName = "MFPlayer PlaybackVideo";
        bool ndiClockVideo = false;
        bool ndiDisableWallPace = false;
        var ndiVideoPumpFrames = 8;
        var ndiVideoTc = NDIVideoTimecodeMode.PresentationRelativeTicks;

        bool noHw = false;
        bool drmGl = false;
        bool d3d11Gl = false;
        int chunkSamples = 960;
        int? deviceLatencyMs = null;
        int ndiAudioAggregateSamples = -1;
        var ndiPumpFromCli = false;
        var ndiPumpCliValue = 0;

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
                case "--ndi-clock-video":
                    ndiClockVideo = true;
                    break;
                case "--ndi-disable-wall-pace":
                    ndiDisableWallPace = true;
                    break;
                case "--no-hw":
                    noHw = true;
                    break;
                case "--hw":
                    // Kept for scripts; hardware decode is now the default.
                    break;
                case "--drm-gl":
                    drmGl = true;
                    break;
                case "--d3d11-gl":
                    d3d11Gl = true;
                    break;
                default:
                    if (TryParseKeyedInt(a, "--chunk-samples=", out var ck) && ck > 0)
                    {
                        chunkSamples = ck;
                        break;
                    }

                    if (TryParseKeyedInt(a, "--device-latency-ms=", out var dlm) && dlm > 0)
                    {
                        deviceLatencyMs = dlm;
                        break;
                    }

                    if (TryParseKeyedInt(a, "--ndi-audio-aggregate=", out var naa))
                    {
                        ndiAudioAggregateSamples = naa;
                        break;
                    }

                    if (TryParseKeyedInt(a, "--ndi-audio-pump-chunks=", out var napc))
                    {
                        ndiPumpFromCli = true;
                        ndiPumpCliValue = napc;
                        break;
                    }

                    if (TryParseKeyedInt(a, "--ndi-video-pump-frames=", out var nvpf) && nvpf >= 1)
                    {
                        ndiVideoPumpFrames = nvpf;
                        break;
                    }

                    if (TryParseKeyedString(a, "--ndi-video-tc=", out var nvtc))
                    {
                        if (string.Equals(nvtc, "synth", StringComparison.OrdinalIgnoreCase))
                            ndiVideoTc = NDIVideoTimecodeMode.Synthesize;
                        else if (string.Equals(nvtc, "pts", StringComparison.OrdinalIgnoreCase))
                            ndiVideoTc = NDIVideoTimecodeMode.PresentationRelativeTicks;
                        else
                        {
                            errorText = "--ndi-video-tc= expects pts or synth";
                            return false;
                        }

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

        int? ndiPumpCap = !ndiPumpFromCli ? 24 : (ndiPumpCliValue < 2 ? null : ndiPumpCliValue);

        opt = new PlaybackOptions(positional[0], ndi, ndiName, noHw, OperatingSystem.IsLinux() && drmGl,
            OperatingSystem.IsWindows() && d3d11Gl, chunkSamples,
            deviceLatencyMs, ndiAudioAggregateSamples, ndiPumpCap,
            ndiClockVideo, ndiDisableWallPace, ndiVideoPumpFrames, ndiVideoTc);
        return true;
    }

    private static bool TryParseKeyedString(string arg, string prefix, out string value)
    {
        value = "";
        if (!arg.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        value = arg[prefix.Length..];
        return value.Length > 0;
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

  --ndi [name]        Mirror video/audio over NDI (forces software video decode so pixels are CPU-readable; do not combine with --drm-gl / --d3d11-gl).
  --no-hw             Force software video decode (default is hardware when --ndi is not used).
  --drm-gl            (Linux only) Prefer DRM PRIME EGL NV12 dma-bufs to GL (do not combine with --ndi).
  --d3d11-gl          (Windows only) Prefer D3D11 NV12 shared handles + GL staging upload (do not combine with --ndi).

  --chunk-samples=n   AudioRouter chunk span (default 960 ≈ 20 ms @ 48 kHz).
  --device-latency-ms=n  PortAudio suggested latency (ms) + larger TargetQueueSamples floor (JACK/ALSA-hw).
  --ndi-audio-aggregate=n  Fixed NDI audio packet size in samples/channel (0=off, default auto from video fps).
  --ndi-audio-pump-chunks=n  NDI audio only: per-sink SinkPump depth (default 24 if omitted; 0–1 = router default).
  --ndi-clock-video       Let the NDI SDK pace video (clockVideo:true); try with --ndi-disable-wall-pace.
  --ndi-disable-wall-pace Disable host wall throttle between NDI video submits (see NDIVideoSender).
  --ndi-video-pump-frames=n  NDI branch VideoSinkPump queue depth (default 8; was 4).
  --ndi-video-tc=pts|synth  Video timecode: pts = PresentationRelativeTicks (default); synth = SDK synthesize.
""");
    }
}
