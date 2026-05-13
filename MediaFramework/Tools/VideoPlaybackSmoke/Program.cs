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

var videoSource = media.Video;
var sdlGl = new SDL3GLVideoSink(
    title: Path.GetFileName(opt.MediaPath),
    initialWidth: Math.Min(videoSource.Format.Width, 1920),
    initialHeight: Math.Min(videoSource.Format.Height, 1080),
    createFallbackD3D11InteropDeviceForWin32Nv12: !(opt.WindowsD3d11SharedGl && opt.WindowsD3d11ZeroHostGl));

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

MediaContainerPlaybackHost? audioHost = MediaContainerPlaybackHost.TryCreatePortAudioMain(
    media, opt.AudioChunkSamples, opt.DeviceLatencyMs,
    msg => Console.Error.WriteLine($"[audio] could not wire PortAudio/ffmpeg: {msg}"));
using var freerunClock = new MediaClock();

IMediaClock playClock = audioHost?.Player.Clock ?? freerunClock;

using VideoPlayer videoPlayer = new(videoSource, videoFront, playClock);
LogVideoPixelRouting(videoSource, videoPlayer, videoRouter, ndiVideoRouterInputId);
var playback = new MediaContainerPlaybackGraph(media, videoPlayer, playClock, audioHost?.Player);
var av = playback.Router;

try
{
    Console.WriteLine(
        $"{Path.GetFileName(opt.MediaPath)}  video {videoSource.Format.Width}x{videoSource.Format.Height} " +
        $"{videoSource.Format.PixelFormat} @{videoSource.Format.FrameRate}");

    if (audioHost is null)
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

    if (ndi is not null && audioHost is not null)
    {
        IAudioSink ndAudio = ndi.EnableAudio(audioHost.AudioFormat);
        var agg = opt.NdiAudioAggregateSamples;
        if (agg < 0)
        {
            var fps = videoSource.Format.FrameRate.ToDouble();
            agg = fps > 0 && !double.IsNaN(fps)
                ? (int)Math.Clamp(audioHost.AudioFormat.SampleRate / fps, 960, 8192)
                : 2000;
        }

        if (agg > 0)
        {
            ndiAudioAgg = new NdiAudioAggregatingSink(ndAudio, agg);
            ndAudio = ndiAudioAgg;
        }

        ndiAudioSinkId = audioHost.Player.AddOutput(ndAudio, sinkPumpCapacityChunks: opt.NdiAudioPumpCapacityChunks);

        audioHost.Player.Connect(audioHost.SourceId, ndiAudioSinkId,
            ChannelMap.Identity(Math.Min(2, audioHost.AudioFormat.Channels)));

        ndiMirrorPrefill = ndAudio;
    }

    WaitForFirstNdiReceiverIfRequested(ndi, opt.NdiWaitFirstReceiverMs);

    if (audioHost is not null)
    {
        // While PortAudio hasn't started, SinkSlavedRouterClock's WaitForCapacity() returns
        // unconditionally — the router would decode as fast as CPU allows, overfill the ring,
        // and Submit() would drop samples while mux audio Position still advanced
        // ("audio 7s ahead" in the HUD). Match PlaybackSmoke: prebuffer by reading the
        // shared decoder directly into the device ring, open the stream, then start the router.
        audioHost.PrefillMainOutputDirectFromDecoder(TimeSpan.FromSeconds(3),
            mirrorPackedFloats: ndiMirrorPrefill);
        audioHost.StartHardwareOutput();
        av.Play();
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
        av.Play(videoOnlyMaster: videoPtsClock);
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

            var aHeard = audioHost is not null
                ? TimeSpan.FromSeconds(audioHost.MainOutput.PlayedSamples / (double)audioHost.MainOutput.Format.SampleRate)
                : TimeSpan.Zero;
            var aDeckDec = (media.Audio as ISeekableSource)?.Position ?? TimeSpan.Zero;
            long pumpDr = 0, paUnd = 0, paDr = 0, ndiDr = 0;
            long ndiVidDr = 0;
            int ndiVidQ = 0;
            if (audioHost is not null)
            {
                pumpDr = audioHost.Player.Router.GetPumpStats(audioHost.PrimarySinkId).Dropped;
                paUnd = audioHost.MainOutput.UnderrunSamples;
                paDr = audioHost.MainOutput.DroppedSamples;
                if (ndiAudioSinkId is not null)
                    ndiDr = audioHost.Player.Router.GetPumpStats(ndiAudioSinkId).Dropped;
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

        var audioNaturally = audioHost?.Player.Router.CompletedNaturally ?? true;
        var routerEof = audioHost?.Player.Router.CompletedNaturally == true;

        if (videoSource.IsExhausted && audioNaturally &&
            (videoPlayer.CompletedNaturally || routerEof))
            break;

        // Wake promptly on Ctrl+C (CTS) instead of always sleeping a full slice.
        _ = cts.Token.WaitHandle.WaitOne(25);
    }

    Console.WriteLine();
    if (cts.IsCancellationRequested)
        Console.Error.WriteLine("[smoke] interrupt: stopping playback…");
    try
    {
        // Ctrl+C: skip SeekPresentation/flush — it can block on demux/decoder while the UI thread
        // is tearing down; EOF exit still flushes for a clean mux snapshot.
        Action? flushMux = cts.IsCancellationRequested ? () => { } : null;
        av.Pause(CancellationToken.None, flushMux);
    }
    catch (OperationCanceledException)
    {
        // Defensive: coordinator uses None today; keep smoke from dying on cooperative cancel.
    }

    if (cts.IsCancellationRequested)
        Console.Error.WriteLine("[smoke] av session paused.");
    videoPtsClock?.Pause();
    if (audioHost is null)
        freerunClock.Stop(CancellationToken.None);
}
finally
{
    if (videoPtsHook is not null)
        videoPlayer.FramePresentationTimePresented -= videoPtsHook;

    ndiAudioAgg?.Dispose();
    audioHost?.Dispose();
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

/// <summary>Blocks until an NDI receiver attaches or the SDK times out (full-wire harness).</summary>
static void WaitForFirstNdiReceiverIfRequested(NDIOutput? ndi, int maxWaitMs)
{
    if (ndi is null || maxWaitMs <= 0)
        return;
    var ms = (uint)maxWaitMs;
    var n = ndi.GetReceiverConnectionCount(ms);
    if (n < 1)
        Console.Error.WriteLine($"[ndi] no receiver within {ms} ms — continuing (Monitor may still connect).");
    else
        Console.WriteLine($"[ndi] {n} receiver(s) connected (wait up to {ms} ms).");
}

static TimeSpan PaceBelowFramePeriod(VideoFormat fmt)
{
    var fps = fmt.FrameRate.ToDouble();
    if (fps <= 0 || double.IsNaN(fps)) return TimeSpan.Zero;
    return TimeSpan.FromSeconds(1.0 / fps * 0.93);
}

internal readonly record struct PlaybackOptions(
    string MediaPath,
    bool NdiEnable,
    string NdiName,
    bool NoHardwareDecode,
    bool LinuxDrmDmabufGl,
    bool WindowsD3d11SharedGl,
    bool WindowsD3d11ZeroHostGl,
    int AudioChunkSamples,
    int? DeviceLatencyMs,
    int NdiAudioAggregateSamples,
    int? NdiAudioPumpCapacityChunks,
    bool NdiClockVideo,
    bool NdiDisableWallPace,
    int NdiVideoPumpFrames,
    NDIVideoTimecodeMode NdiVideoTimecodeMode,
    int NdiWaitFirstReceiverMs);

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
        var ndiWaitFirstRxMs = 0;

        bool noHw = false;
        bool drmGl = false;
        bool d3d11Gl = false;
        bool d3d11GlZeroHost = false;
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
                case "--d3d11-gl-zero-host":
                    d3d11GlZeroHost = true;
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

                    if (TryParseKeyedInt(a, "--ndi-wait-first-receiver-ms=", out var nwr) && nwr >= 0)
                    {
                        ndiWaitFirstRxMs = Math.Clamp(nwr, 0, 300_000);
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

        if (d3d11GlZeroHost && !d3d11Gl)
        {
            errorText = "--d3d11-gl-zero-host requires --d3d11-gl";
            return false;
        }

        if (ndiWaitFirstRxMs > 0 && !ndi)
        {
            errorText = "--ndi-wait-first-receiver-ms requires --ndi";
            return false;
        }

        int? ndiPumpCap = !ndiPumpFromCli ? 24 : (ndiPumpCliValue < 2 ? null : ndiPumpCliValue);

        opt = new PlaybackOptions(positional[0], ndi, ndiName, noHw, OperatingSystem.IsLinux() && drmGl,
            OperatingSystem.IsWindows() && d3d11Gl, OperatingSystem.IsWindows() && d3d11Gl && d3d11GlZeroHost, chunkSamples,
            deviceLatencyMs, ndiAudioAggregateSamples, ndiPumpCap,
            ndiClockVideo, ndiDisableWallPace, ndiVideoPumpFrames, ndiVideoTc, ndiWaitFirstRxMs);
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
  --d3d11-gl          (Windows only) Prefer D3D11 NV12 shared handles + GL upload (do not combine with --ndi).
  --d3d11-gl-zero-host  (Windows, with --d3d11-gl) Do not create SDL's fallback D3D11GlInteropDeviceHost; bind the GL uploader from libav's ID3D11Device on the first decoded frame (true zero-host; requires LibavD3D11DeviceComPtr on Win32 NV12 backing or negotiator borrow).

  --chunk-samples=n   AudioRouter chunk span (default 960 ≈ 20 ms @ 48 kHz).
  --device-latency-ms=n  PortAudio suggested latency (ms) + larger TargetQueueSamples floor (JACK/ALSA-hw).
  --ndi-audio-aggregate=n  Fixed NDI audio packet size in samples/channel (0=off, default auto from video fps).
  --ndi-audio-pump-chunks=n  NDI audio only: per-sink SinkPump depth (default 24 if omitted; 0–1 = router default).
  --ndi-clock-video       Let the NDI SDK pace video (clockVideo:true); try with --ndi-disable-wall-pace.
  --ndi-disable-wall-pace Disable host wall throttle between NDI video submits (see NDIVideoSender).
  --ndi-video-pump-frames=n  NDI branch VideoSinkPump queue depth (default 8; was 4).
  --ndi-video-tc=pts|synth  Video timecode: pts = PresentationRelativeTicks (default); synth = SDK synthesize.
  --ndi-wait-first-receiver-ms=n  (With --ndi) Block up to n ms for the first NDI receiver (NDIlib_send_get_no_connections); 0=off (default).

Lab (NDI egress timeline soak, not this tool's runtime): RUN_NDI_EGRESS_SOAK=1, optional RUN_NDI_EGRESS_SOAK_ROUNDS=<n> (1k–10M), RUN_NDI_EGRESS_SOAK_STRESS=1 — NdiEgressPresentationTimelineTests. RUN_NDI_MUX_SOAK=1 — NdiEgressMuxPlayheadClockTests. RUN_MEDIA_SOAK=1, optional RUN_MEDIA_SOAK_ROUNDS=<n> (8–10000) — MediaContainerDecoderSoakTests.
""");
    }
}
