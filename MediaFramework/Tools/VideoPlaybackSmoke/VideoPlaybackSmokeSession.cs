using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;
using S.Media.Playback;
using S.Media.PortAudio;
using S.Media.SDL3;

namespace VideoPlaybackSmoke;

/// <summary>
/// <c>VideoPlaybackSmoke</c> graph: <see cref="S.Media.Playback.MediaPlayer"/> (mux + router + audio router),
/// SDL window, optional NDI, optional PortAudio via <see cref="PortAudioPlaybackHost"/>.
/// </summary>
public sealed class VideoPlaybackSmokeSession : IDisposable
{
    private bool _disposed;
    private EventHandler? _sdlCloseForCancel;

    private VideoPlaybackSmokeSession(
        S.Media.Playback.MediaPlayer core,
        SDL3GLVideoOutput glWindowOutput,
        NDIOutput? ndi,
        string? ndiVideoOutputId,
        PortAudioPlaybackHost? audioHost,
        NDIAudioAggregatingOutput? ndiAudioAgg,
        IAudioOutput? ndiMirrorPrefill,
        string? ndiAudioOutputId,
        bool win32Nv12SharedHandleOnlyRequested)
    {
        Core = core;
        GlWindowOutput = glWindowOutput;
        NDI = ndi;
        NDIVideoRouterInputId = core.VideoRouterInputId;
        NDIVideoOutputId = ndiVideoOutputId;
        AudioHost = audioHost;
        NDIAudioAggregatingOutput = ndiAudioAgg;
        NDIMirrorPrefill = ndiMirrorPrefill;
        NDIAudioOutputId = ndiAudioOutputId;
        Win32Nv12SharedHandleOnlyRequested = win32Nv12SharedHandleOnlyRequested;
    }

    public S.Media.Playback.MediaPlayer Core { get; }

    public MediaContainerDecoder Media => Core.Decoder;

    public IVideoOutput WindowOutput => GlWindowOutput;

    public SDL3GLVideoOutput GlWindowOutput { get; }

    public IVideoOutput? NDIProgramVideoOutput => NDI?.Video;

    public VideoRouter VideoRouter => Core.VideoRouter;

    public NDIOutput? NDI { get; }

    public string NDIVideoRouterInputId { get; }

    public string? NDIVideoOutputId { get; }

    public PortAudioPlaybackHost? AudioHost { get; }

    public MediaClock? FreerunClock => Core.FreerunClock;

    public IMediaClock PlayClock => Core.PlayClock;

    public VideoPlayer VideoPlayer => Core.Video;

    public MediaContainerPlaybackBundle Bundle => Core.Bundle;

    public MediaContainerSession Session => Core.Session;

    public NDIAudioAggregatingOutput? NDIAudioAggregatingOutput { get; }

    public IAudioOutput? NDIMirrorPrefill { get; }

    public string? NDIAudioOutputId { get; }

    public bool Win32Nv12SharedHandleOnlyRequested { get; }

    public void AttachWindowCloseToCancel(CancellationTokenSource cts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cts);
        if (_sdlCloseForCancel is not null)
            throw new InvalidOperationException(nameof(AttachWindowCloseToCancel) + " may only be called once per session.");
        _sdlCloseForCancel = (_, _) => cts.Cancel();
        GlWindowOutput.CloseRequested += _sdlCloseForCancel;
    }

    public static void AttachConsoleCancelKeyPress(CancellationTokenSource cts) =>
        S.Media.Playback.MediaPlayer.AttachConsoleCancelKeyPress(cts);

    public void StartWithAudioPrefill(TimeSpan? prefillDuration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (AudioHost is null)
            throw new InvalidOperationException("PortAudio path is not active; use AV routing without this helper.");
        var prefill = prefillDuration ?? SmokeDefaults.AudioPrefillDuration;
        AudioHost.PrefillMainOutputDirectFromDecoder(prefill, NDIMirrorPrefill);
        AudioHost.StartHardwareOutput();
        Session.Play();
    }

    public static bool TryCreate(
        string mediaPath,
        in SmokePlaybackOptions playback,
        Action<string>? onAudioWireFailedMessage,
        [NotNullWhen(true)] out VideoPlaybackSmokeSession? session,
        out string? errorMessage) =>
        TryCreate(playback.ToToolOptions(mediaPath), onAudioWireFailedMessage, out session, out errorMessage);

    public static bool TryCreate(
        string mediaPath,
        in SmokePlaybackOptions playback,
        in SmokePresentationOptions presentation,
        Action<string>? onAudioWireFailedMessage,
        [NotNullWhen(true)] out VideoPlaybackSmokeSession? session,
        out string? errorMessage) =>
        TryCreate(playback.ToToolOptions(mediaPath), presentation, onAudioWireFailedMessage, out session, out errorMessage);

    public static bool TryCreate(
        in SmokeToolOptions opt,
        Action<string>? onAudioWireFailedMessage,
        [NotNullWhen(true)] out VideoPlaybackSmokeSession? session,
        out string? errorMessage) =>
        TryCreate(opt, SmokePresentationOptions.Default, onAudioWireFailedMessage, out session, out errorMessage);

    public static bool TryCreate(
        in SmokeToolOptions opt,
        in SmokePresentationOptions presentation,
        Action<string>? onAudioWireFailedMessage,
        [NotNullWhen(true)] out VideoPlaybackSmokeSession? session,
        out string? errorMessage)
    {
        session = null;
        errorMessage = null;

        FFmpegRuntime.EnsureInitialized();

        if (string.IsNullOrWhiteSpace(opt.MediaPath))
        {
            errorMessage = "media path is required.";
            return false;
        }

        if (!File.Exists(opt.MediaPath))
        {
            errorMessage = $"file not found: {opt.MediaPath}";
            return false;
        }

        if (opt.WindowsD3d11GlSharedHandleOnly && opt.WindowsD3d11ZeroHostGl)
        {
            errorMessage =
                "Win32 NV12 shared-handle-only export conflicts with zero-host GL (handle-only needs SDL D3D11GlInteropDeviceHost or a pre-bound negotiator device).";
            return false;
        }

        MediaContainerDecoder? media = null;
        SDL3GLVideoOutput? sdlGl = null;
        S.Media.Playback.MediaPlayer? core = null;
        NDIOutput? ndi = null;
        string? ndiVideoOutputId = null;
        PortAudioPlaybackHost? audioHost = null;
        NDIAudioAggregatingOutput? ndiAudioAgg = null;
        IAudioOutput? ndiMirrorPrefill = null;
        string? ndiAudioOutputId = null;

        void FailDispose()
        {
            ndiAudioAgg?.Dispose();
            ndiAudioAgg = null;
            core?.Dispose();
            core = null;
            audioHost?.Dispose();
            audioHost = null;
            ndi?.Dispose();
            ndi = null;
            media?.Dispose();
            media = null;
            if (core is null && sdlGl is not null)
            {
                try { sdlGl.Dispose(); }
                catch { /* best-effort */ }
                sdlGl = null;
            }
        }

        try
        {
            var videoDecoderOptions = new VideoDecoderOpenOptions
            {
                TryHardwareAcceleration = !opt.NoHardwareDecode && !opt.NDIEnable,
                RetainDmabufForGl = opt.LinuxDrmDmabufGl,
                RetainD3D11SharedHandleForGl = opt.WindowsD3d11SharedGl,
                Win32Nv12SharedHandleOnlyExport = opt.WindowsD3d11GlSharedHandleOnly,
            };
            var win32Nv12HandleOnlyRequested =
                VideoDecoderOpenOptions.IsWin32Nv12SharedHandleOnlyRequested(videoDecoderOptions);

            media = MediaContainerDecoder.Open(opt.MediaPath, videoDecoderOptions);
            media.SeekPresentation(TimeSpan.Zero);

            var videoSource = media.Video;
            var windowTitle = string.IsNullOrWhiteSpace(presentation.WindowTitle)
                ? Path.GetFileName(opt.MediaPath)
                : presentation.WindowTitle;
            var capW = presentation.MaxSdlWindowWidth <= 0
                ? SmokeDefaults.MaxSdlWindowWidth
                : presentation.MaxSdlWindowWidth;
            var capH = presentation.MaxSdlWindowHeight <= 0
                ? SmokeDefaults.MaxSdlWindowHeight
                : presentation.MaxSdlWindowHeight;
            sdlGl = new SDL3GLVideoOutput(
                title: windowTitle!,
                initialWidth: Math.Min(videoSource.Format.Width, capW),
                initialHeight: Math.Min(videoSource.Format.Height, capH),
                createFallbackD3D11InteropDeviceForWin32Nv12: win32Nv12HandleOnlyRequested
                    || !(opt.WindowsD3d11SharedGl && opt.WindowsD3d11ZeroHostGl));

            var mpOpt = new MediaPlayerOpenOptions(
                TryHardwareAcceleration: !opt.NoHardwareDecode && !opt.NDIEnable,
                RetainDmabufForGl: opt.LinuxDrmDmabufGl,
                RetainD3D11SharedHandleForGl: opt.WindowsD3d11SharedGl,
                Win32Nv12SharedHandleOnlyExport: opt.WindowsD3d11GlSharedHandleOnly,
                WindowsD3d11ZeroHostGl: opt.WindowsD3d11ZeroHostGl,
                AudioChunkSamples: opt.AudioChunkSamples,
                IncludeAudioRouter: true);

            if (!S.Media.Playback.MediaPlayer.Open(media)
                    .WithOptions(mpOpt)
                    .WithVideoLead(sdlGl, disposeOnPlayerDispose: true)
                    .WithDecoderOwnership(MediaPlayerDecoderOwnership.BundleDisposesDecoder)
                    .TryBuild(out core, out errorMessage))
            {
                media = null;
                sdlGl = null;
                FailDispose();
                session = null;
                return false;
            }

            media = null;

            if (opt.NDIEnable)
            {
                var wallPace = opt.NDIDisableWallPace ? (TimeSpan?)null : VideoFormatPacing.PaceBelowFramePeriod(videoSource.Format);
                ndi = new NDIOutput(opt.NDIName, clockVideo: opt.NDIClockVideo, clockAudio: true,
                    minimumVideoSubmitSpacing: wallPace,
                    videoTimecodeMode: opt.NDIVideoTimecodeMode);

                ndiVideoOutputId = core!.VideoRouter.AddOutput(ndi.Video, "ndi", disposeOutputOnRouterDispose: true,
                    asyncPump: new VideoOutputPumpAttachOptions(opt.NDIVideoPumpFrames, "ndi-video", null,
                        DisposeInnerOutputWhenPumpDisposes: false));
                if (!core.VideoRouter.TryAddRoute(core.VideoRouterInputId, ndiVideoOutputId, out var routeErr))
                    throw new InvalidOperationException(routeErr ?? "VideoRouter.TryAddRoute(ndi) failed");
            }

            audioHost = PortAudioPlaybackHost.TryWirePortAudioMainForRouter(
                core!.Decoder,
                core.AudioRouter!,
                core.AudioClock!,
                core.AudioSourceId!,
                opt.AudioChunkSamples,
                opt.DeviceLatencyMs,
                onAudioWireFailedMessage,
                PortAudioPlaybackHostPlayerOwnership.CallerDisposesPlayer);

            if (ndi is not null && audioHost is not null)
            {
                IAudioOutput ndAudio = ndi.EnableAudio(audioHost.AudioFormat);
                var agg = opt.NDIAudioAggregateSamples;
                if (agg < 0)
                {
                    var fps = videoSource.Format.FrameRate.ToDouble();
                    agg = fps > 0 && !double.IsNaN(fps)
                        ? (int)Math.Clamp(audioHost.AudioFormat.SampleRate / fps, 960, 8192)
                        : 2000;
                }

                if (agg > 0)
                {
                    ndiAudioAgg = new NDIAudioAggregatingOutput(ndAudio, agg);
                    ndAudio = ndiAudioAgg;
                }

                ndiAudioOutputId = audioHost.Router.AddOutput(ndAudio, pumpCapacityChunks: opt.NDIAudioPumpCapacityChunks);

                audioHost.Router.Connect(audioHost.SourceId, ndiAudioOutputId,
                    ChannelMap.Identity(Math.Min(2, audioHost.AudioFormat.Channels)));

                ndiMirrorPrefill = ndAudio;
            }

            session = new VideoPlaybackSmokeSession(
                core, sdlGl, ndi, ndiVideoOutputId, audioHost,
                ndiAudioAgg, ndiMirrorPrefill, ndiAudioOutputId, win32Nv12HandleOnlyRequested);

            core = null;
            sdlGl = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            ndiAudioAgg?.Dispose();
            ndiAudioAgg = null;
            FailDispose();
            session = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sdlCloseForCancel is not null)
        {
            GlWindowOutput.CloseRequested -= _sdlCloseForCancel;
            _sdlCloseForCancel = null;
        }

        NDIAudioAggregatingOutput?.Dispose();
        Core.Dispose();
        AudioHost?.Dispose();
        NDI?.Dispose();
    }
}
