using System.Text;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.NDI;
using S.Media.NDI.Video;
using S.Media.Playback;
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

if (opt.WindowsD3d11SharedGl && opt.WindowsD3d11ZeroHostGl && opt.WindowsD3d11GlSharedHandleOnly)
{
    Console.Error.WriteLine(
        "error: --d3d11-gl-zero-host conflicts with Win32 NV12 shared-handle-only export (VideoDecoderOpenOptions.Win32Nv12SharedHandleOnlyExport or MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY=1).");
    return 2;
}

if (!VideoPlaybackSmokeSession.TryCreate(opt,
        msg => Console.Error.WriteLine($"[audio] could not wire PortAudio/ffmpeg: {msg}"),
        out var session,
        out var sessionErr))
{
    Console.Error.WriteLine(sessionErr ?? "could not open playback session");
    return 3;
}

var media = session.Media;
var videoSource = media.Video;
var windowSink = session.WindowSink;
var videoRouter = session.VideoRouter;
var ndi = session.NDI;
var ndiVideoRouterInputId = session.NDIVideoRouterInputId;
var ndiVideoOutputId = session.NDIVideoOutputId;
var ndiAudioAgg = session.NDIAudioAggregatingSink;
var ndiMirrorPrefill = session.NDIMirrorPrefill;
var ndiAudioSinkId = session.NDIAudioSinkId;
var audioHost = session.AudioHost;
IMediaClock playClock = session.PlayClock;
var videoPlayer = session.VideoPlayer;
var av = session.Av;
VideoPtsClock? videoPtsClock = null;
Action<TimeSpan>? videoPtsHook = null;

try
{
    SmokeVideoRouting.WriteLine(Console.Out, videoSource, videoPlayer, videoRouter, ndiVideoRouterInputId);

    Console.WriteLine(
        $"{Path.GetFileName(opt.MediaPath)}  video {videoSource.Format.Width}x{videoSource.Format.Height} " +
        $"{videoSource.Format.PixelFormat} @{videoSource.Format.FrameRate}");

    if (audioHost is null)
        Console.WriteLine("[audio] not wired — see stderr above; using VideoPtsClock as MediaClock master.");

    if (opt.NDIEnable && !opt.NoHardwareDecode)
        Console.WriteLine("[ndi] Software video decode is used with --ndi so NDI can read pixel bytes (GPU NV12 has no CPU path here).");

    if (opt.NDIEnable && opt.LinuxDrmDmabufGl)
        Console.WriteLine("[ndi] DRM dma-buf decode + NDI branch routing is unsupported — omit --ndi or disable --drm-gl.");

    if (opt.NDIEnable && opt.WindowsD3d11SharedGl)
        Console.WriteLine("[ndi] D3D11 shared-handle NV12 decode + NDI branch routing is unsupported — omit --ndi or disable --d3d11-gl.");

    if (media.Win32Nv12SharedHandleOnlyActive)
        Console.WriteLine(
            "[win32 nv12] DXGI shared-handle export without libav D3D11 COM pointers on backing (" +
            (opt.WindowsD3d11GlSharedHandleOnly ? "--d3d11-gl-shared-handle-only" : "MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY / options") +
            "); SDL D3D11GlInteropDeviceHost supplies the D3D11 device for OpenSharedResource.");

    if (opt.NDIEnable)
        Console.WriteLine("[ndi] SDL mirrors local video; PortAudio still plays to the default device — use the HUD show/decoded counts if the window stays blank.");

    using CancellationTokenSource cts = new();
    VideoPlaybackSmokeSession.AttachConsoleCancelKeyPress(cts);
    session.AttachWindowCloseToCancel(cts);

    ndi?.WaitForFirstReceiverIfRequested(opt.NDIWaitFirstReceiverMs,
        Console.Error.WriteLine, Console.WriteLine);

    if (audioHost is not null)
    {
        // While PortAudio hasn't started, SinkSlavedRouterClock's WaitForCapacity() returns
        // unconditionally — the router would decode as fast as CPU allows, overfill the ring,
        // and Submit() would drop samples while mux audio Position still advanced
        // ("audio 7s ahead" in the HUD). Match PlaybackSmoke: prebuffer by reading the
        // shared decoder directly into the device ring, open the stream, then start the router.
        session.StartWithAudioPrefill();
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
            var tick = new SmokeHudTick(intervalSec, lastHudDisplayed, videoPlayer.DisplayedCount);
            lastHudDisplayed = videoPlayer.DisplayedCount;
            var snap = SmokeHud.Collect(tick, playClock, videoSource, videoPlayer, media, windowSink,
                audioHost, ndiAudioSinkId, videoRouter, ndiVideoOutputId, ndi);
            Console.Write("\r " + PlaybackHud.FormatLine(snap));
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
        session.Core.Audio?.Clock.Stop(CancellationToken.None);
}
finally
{
    if (videoPtsHook is not null)
        videoPlayer.FramePresentationTimePresented -= videoPtsHook;

    session.Dispose();
}

Console.WriteLine("done.");
return 0;

file static class PlaybackCli
{
    public static bool TryParse(string[] argv, out SmokeToolOptions opt, out string? errorText)
    {
        opt = default;
        errorText = null;

        bool ndi = false;
        string ndiName = SmokeDefaults.DefaultNDIOutputName;
        bool ndiClockVideo = false;
        bool ndiDisableWallPace = false;
        var ndiVideoPumpFrames = SmokeDefaults.DefaultNDIVideoPumpFrames;
        var ndiVideoTc = NDIVideoTimecodeMode.PresentationRelativeTicks;
        var ndiWaitFirstRxMs = 0;

        bool noHw = false;
        bool drmGl = false;
        bool d3d11Gl = false;
        bool d3d11GlZeroHost = false;
        bool d3d11GlSharedHandleOnly = false;
        int chunkSamples = SmokeDefaults.DefaultAudioChunkSamples;
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
                case "--d3d11-gl-shared-handle-only":
                    d3d11GlSharedHandleOnly = true;
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
                        ndiWaitFirstRxMs = Math.Clamp(nwr, 0, SmokeDefaults.MaxNDIWaitFirstReceiverMs);
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

        if (d3d11GlSharedHandleOnly && !d3d11Gl)
        {
            errorText = "--d3d11-gl-shared-handle-only requires --d3d11-gl";
            return false;
        }

        if (d3d11GlSharedHandleOnly && d3d11GlZeroHost)
        {
            errorText = "--d3d11-gl-shared-handle-only cannot be combined with --d3d11-gl-zero-host (handle-only export needs SDL D3D11GlInteropDeviceHost or a pre-bound negotiator device)";
            return false;
        }

        if (ndiWaitFirstRxMs > 0 && !ndi)
        {
            errorText = "--ndi-wait-first-receiver-ms requires --ndi";
            return false;
        }

        int? ndiPumpCap = !ndiPumpFromCli ? SmokeDefaults.DefaultNDIAudioSinkPumpCapacityChunks : (ndiPumpCliValue < 2 ? null : ndiPumpCliValue);

        opt = new SmokeToolOptions(positional[0], ndi, ndiName, noHw, OperatingSystem.IsLinux() && drmGl,
            OperatingSystem.IsWindows() && d3d11Gl, OperatingSystem.IsWindows() && d3d11Gl && d3d11GlZeroHost,
            OperatingSystem.IsWindows() && d3d11Gl && d3d11GlSharedHandleOnly, chunkSamples,
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
  --drm-gl            (Linux only) Prefer DRM PRIME EGL semi-planar (NV12 / P010 / P016) dma-bufs to GL (do not combine with --ndi).
  --d3d11-gl          (Windows only) Prefer D3D11 NV12 shared handles + GL upload (do not combine with --ndi).
  --d3d11-gl-zero-host  (Windows, with --d3d11-gl) Do not create SDL's fallback D3D11GlInteropDeviceHost; bind the GL uploader from libav's ID3D11Device on the first decoded frame (true zero-host; requires LibavD3D11DeviceComPtr on Win32 NV12 backing or negotiator borrow).
  --d3d11-gl-shared-handle-only  (Windows, with --d3d11-gl) Omit libav ID3D11Device/ID3D11Texture2D COM pointers on VideoWin32Nv12Backing (DXGI NT handle path only; incompatible with --d3d11-gl-zero-host). SDL D3D11GlInteropDeviceHost must create the D3D11 device used for OpenSharedResource. Lab: same with MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY=1 (or true) and --d3d11-gl (omit this flag).

  --chunk-samples=n   AudioRouter chunk span (default 960 ≈ 20 ms @ 48 kHz).
  --device-latency-ms=n  PortAudio suggested latency (ms) + larger TargetQueueSamples floor (JACK/ALSA-hw).
  --ndi-audio-aggregate=n  Fixed NDI audio packet size in samples/channel (0=off, default auto from video fps).
  --ndi-audio-pump-chunks=n  NDI audio only: per-sink SinkPump depth (default 24 if omitted; 0–1 = router default).
  --ndi-clock-video       Let the NDI SDK pace video (clockVideo:true); try with --ndi-disable-wall-pace.
  --ndi-disable-wall-pace Disable host wall throttle between NDI video submits (see NDIVideoSender).
  --ndi-video-pump-frames=n  NDI branch VideoSinkPump queue depth (default 8; was 4).
  --ndi-video-tc=pts|synth  Video timecode: pts = PresentationRelativeTicks (default); synth = SDK synthesize.
  --ndi-wait-first-receiver-ms=n  (With --ndi) Block up to n ms for the first NDI receiver (NDIlib_send_get_no_connections); 0=off (default).

Lab (NDI egress timeline soak, not this tool's runtime): RUN_NDI_EGRESS_SOAK=1, optional RUN_NDI_EGRESS_SOAK_ROUNDS=<n> (1k–10M), RUN_NDI_EGRESS_SOAK_STRESS=1 — NDIEgressPresentationTimelineTests. RUN_NDI_MUX_SOAK=1 — NDIEgressMuxPlayheadClockTests. RUN_NDI_MEMORY_PRESSURE=1, optional RUN_NDI_MEMORY_PRESSURE_ROUNDS=<n> (200–100k, or 200–2M with RUN_NDI_MEMORY_PRESSURE_LONG=1), optional RUN_NDI_MEMORY_PRESSURE_HEAP=1, optional RUN_NDI_MEMORY_PRESSURE_HEAP_STRICT=1 (requires HEAP=1) — NDIOutputLifecycleMemoryTests. RUN_MEDIA_SOAK=1, optional RUN_MEDIA_SOAK_ROUNDS=<n> (8–10000) — MediaContainerDecoderSoakTests.
""");
    }
}
