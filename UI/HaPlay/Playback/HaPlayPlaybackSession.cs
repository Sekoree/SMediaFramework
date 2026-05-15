using System.Threading;
using System.Diagnostics.CodeAnalysis;
using HaPlay.Models;
using HaPlay.ViewModels;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;
using S.Media.Playback;
using S.Media.PortAudio;
using S.Media.SDL3;

namespace HaPlay.Playback;

internal sealed class HaPlayPlaybackSession : IDisposable
{
    private readonly List<LogoFallbackVideoSink> _logoSinks = new();
    private readonly List<OutputLineViewModel> _acquiredCarriers = new();
    private readonly OutputManagementViewModel _outputs;
    private readonly List<PortAudioOutput> _portAudioOutputs = new();
    private readonly List<ResamplingAudioSink> _ndiAudioResamplers = new();
    private readonly List<NDIAudioSink> _ndiAudioSinks = new();
    private bool _disposed;

    private HaPlayPlaybackSession(MediaPlayer player, AvRouter router, OutputManagementViewModel outputs)
    {
        Player = player;
        Router = router;
        _outputs = outputs;
    }

    public MediaPlayer Player { get; }
    public AvRouter Router { get; }

    public IReadOnlyList<LogoFallbackVideoSink> LogoSinks => _logoSinks;

    public static bool TryCreate(
        string mediaPath,
        IReadOnlyList<OutputLineViewModel> selectedOutputs,
        OutputManagementViewModel outputs,
        [NotNullWhen(true)] out HaPlayPlaybackSession? session,
        out string? errorMessage)
    {
        session = null;
        errorMessage = null;
        // Caller must stop output previews on the UI thread before TryCreate (previews touch bound controls / SDL).

        var lines = selectedOutputs.ToList();
        var anyNDI = lines.Exists(static l => l.Definition is NDIOutputDefinition);
        var mpOpt = new MediaPlayerOpenOptions(
            TryHardwareAcceleration: !anyNDI,
            IncludeAudioRouter: true);

        // Pre-probe the container so we know HasVideo / HasAudio before we decide which carrier sides to
        // acquire. Without this we'd have to acquire both sides always, which would pause carrier video
        // during audio-only-source playback and flash NDI receivers to a stub format with no frames.
        MediaContainerDecoder? decoder;
        try
        {
            decoder = MediaContainerDecoder.Open(mediaPath, mpOpt.ToVideoDecoderOpenOptions());
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }

        var hasVideo = decoder.HasVideo;
        var hasAudio = decoder.HasAudio;

        // Acquire each NDI carrier with only the sides playback actually drives — keeps the other side's
        // black/silence flowing so receivers don't see resolution flicker or audio-format flicker.
        var ndiByDefinitionId = new Dictionary<Guid, NDIOutput>();
        var acquired = new List<OutputLineViewModel>();
        foreach (var line in lines)
        {
            if (line.Definition is not NDIOutputDefinition nd)
                continue;
            if (ndiByDefinitionId.ContainsKey(nd.Id))
                continue;

            var needsVideo = hasVideo && nd.StreamMode != NDIOutputStreamMode.AudioOnly;
            var needsAudio = hasAudio && nd.StreamMode != NDIOutputStreamMode.VideoOnly;
            if (!needsVideo && !needsAudio)
                continue; // nothing playback will wire — leave the carrier alone

            var ndi = outputs.TryAcquireNDICarrierForPlayback(line, needsVideo, needsAudio);
            if (ndi is null)
            {
                ReleaseAcquiredCarriers(outputs, acquired);
                decoder.Dispose();
                errorMessage = $"NDI output '{nd.DisplayName}' has no live carrier (was it just removed, or is another player using it?).";
                return false;
            }

            ndiByDefinitionId[nd.Id] = ndi;
            acquired.Add(line);
        }

        var videoChains = new List<(string Id, IVideoSink Sink)>();
        if (hasVideo)
        {
            foreach (var line in lines)
            {
                switch (line.Definition)
                {
                    case LocalVideoOutputDefinition lv when lv.Engine == VideoOutputEngine.SdlOpenGl:
                    {
                        var (w, h) = InitialSdlSize(lv);
                        var sdl = new SDL3GLVideoSink(lv.DisplayName, w, h);
                        var logo = new LogoFallbackVideoSink(sdl, disposeInnerOnDispose: true);
                        videoChains.Add(($"sdl_{lv.Id:N}", logo));
                        break;
                    }
                    case NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.AudioOnly:
                    {
                        if (!ndiByDefinitionId.TryGetValue(nd.Id, out var ndi))
                            continue;
                        var pump = new VideoSinkPump(ndi.VideoSink, maxQueuedFrames: 8, name: $"ndi-{nd.Id:N}", log: null,
                            disposeInnerOnDispose: false);
                        var logo = new LogoFallbackVideoSink(pump, disposeInnerOnDispose: true);
                        videoChains.Add(($"ndi_{nd.Id:N}", logo));
                        break;
                    }
                }
            }
        }

        var lead = videoChains.Count > 0 ? videoChains[0].Sink : null;

        if (!MediaPlayer.TryOpen(decoder, mpOpt, lead, disposeNegotiationLead: true,
                MediaPlayerDecoderOwnership.BundleDisposesDecoder, out var player, out errorMessage))
        {
            // BundleDisposesDecoder disposes the decoder for us on failure.
            ReleaseAcquiredCarriers(outputs, acquired);
            return false;
        }

        HaPlayPlaybackSession? pendingPlayback = null;
        try
        {
            var router = player.VideoRouter;
            var inputId = player.VideoRouterInputId;
            for (var i = 1; i < videoChains.Count; i++)
            {
                var (id, sink) = videoChains[i];
                var outId = router.AddOutput(sink, id, disposeSinkOnRouterDispose: true);
                if (!router.TryAddRoute(inputId, outId, out var routeErr))
                    throw new InvalidOperationException(routeErr ?? "TryAddRoute failed");
            }

            var av = player.Av;
            pendingPlayback = new HaPlayPlaybackSession(player, av, outputs);

            foreach (var sink in videoChains.Select(t => t.Sink).OfType<LogoFallbackVideoSink>())
                pendingPlayback._logoSinks.Add(sink);

            pendingPlayback._acquiredCarriers.AddRange(acquired);

            WireAudio(lines, ndiByDefinitionId, player, pendingPlayback);
            session = pendingPlayback;
            pendingPlayback = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            pendingPlayback?.DisposePartialBeforePlayerDispose();
            player.Dispose();
            ReleaseAcquiredCarriers(outputs, acquired);
            return false;
        }
    }

    private static void ReleaseAcquiredCarriers(OutputManagementViewModel outputs, List<OutputLineViewModel> acquired)
    {
        foreach (var line in acquired)
        {
            try { outputs.ReleaseNDICarrierForPlayback(line); }
            catch { /* best effort */ }
        }
    }

    private static void WireAudio(List<OutputLineViewModel> lines, Dictionary<Guid, NDIOutput> ndiByDefinitionId,
        MediaPlayer player, HaPlayPlaybackSession playback)
    {
        if (player.Audio is null || string.IsNullOrEmpty(player.AudioSourceId))
            return;

        var dec = player.Decoder.Audio.Format;
        var stereoFmt = new AudioFormat(dec.SampleRate, 2);
        var map = StereoDownmix(dec.Channels);
        const int chunk = 480;

        foreach (var line in lines)
        {
            switch (line.Definition)
            {
                case PortAudioOutputDefinition pa:
                {
                    var outDev = new PortAudioOutput(stereoFmt, pa.GlobalDeviceIndex, null, chunk,
                        ringCapacityFrames: dec.SampleRate);
                    playback._portAudioOutputs.Add(outDev);
                    var sinkId = player.Audio.AddOutput(outDev);
                    player.Audio.Connect(player.AudioSourceId!, sinkId, map);
                    break;
                }
                case NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.VideoOnly:
                {
                    if (!ndiByDefinitionId.TryGetValue(nd.Id, out var ndi))
                        continue;
                    // Match the carrier's audio format exactly (sampleRate from definition, 2 channels) so
                    // EnableAudio's idempotent return path hands back the carrier's existing sink.
                    var ndiAudioFmt = new AudioFormat(nd.AudioSampleRate, 2);
                    var ndiSink = ndi.EnableAudio(ndiAudioFmt);
                    playback._ndiAudioSinks.Add(ndiSink);
                    IAudioSink routerSink = ndiSink;
                    if (ndiAudioFmt.SampleRate != dec.SampleRate)
                    {
                        var resampler = new ResamplingAudioSink(ndiSink, stereoFmt);
                        playback._ndiAudioResamplers.Add(resampler);
                        routerSink = resampler;
                    }

                    var sinkId = player.Audio.AddOutput(routerSink);
                    player.Audio.Connect(player.AudioSourceId!, sinkId, map);
                    break;
                }
            }
        }
    }

    private static ChannelMap StereoDownmix(int sourceChannels) =>
        sourceChannels >= 2 ? ChannelMap.Identity(2) : new ChannelMap([0, 0]);

    private static (int W, int H) InitialSdlSize(LocalVideoOutputDefinition d)
    {
        if (d.SurfaceMode == VideoSurfaceMode.Windowed && d.WindowWidth is { } w && d.WindowHeight is { } h)
            return (w, h);
        return (1280, 720);
    }

    public void ApplyFallbackImage(string? path)
    {
        if (_logoSinks.Count == 0)
            return;
        if (string.IsNullOrWhiteSpace(path))
        {
            foreach (var l in _logoSinks)
                l.TrySetHoldTemplate(null);
            return;
        }

        var fmt = Player.Video.Format;
        var proto = FallbackImageLoader.TryBuildHoldCpuFrame(fmt, path);
        if (proto is null)
            return;
        try
        {
            foreach (var l in _logoSinks)
                l.TrySetHoldTemplate(FallbackImageLoader.CloneHoldTemplate(proto));
        }
        finally
        {
            proto.Dispose();
        }
    }

    /// <summary>
    /// Pushes several black frames through each logo branch so NDI/SDL receivers stabilize before <see cref="AvRouter.Play"/>.
    /// Must be called with the session paused and before the hold pump timer runs.
    /// </summary>
    /// <param name="holdFallbackShowsImage">When true, decoded pixels are not shown — priming is skipped.</param>
    /// <param name="frameCount">Ignored when <paramref name="holdFallbackShowsImage"/> is true.</param>
    /// <param name="pacingMs">Sleep between frames; ignored when <paramref name="holdFallbackShowsImage"/> is true.</param>
    public void PrimeVideoOutputsBeforePlay(bool holdFallbackShowsImage = false, int frameCount = 12, int pacingMs = 2)
    {
        if (holdFallbackShowsImage || _logoSinks.Count == 0)
            return;
        // Audio-only sources (no real video stream) negotiate to a stub format the sink doesn't care
        // about — priming black frames at the stub resolution would just glitch NDI receivers between
        // the carrier's real size and the stub size. Skip when there's no decodable video.
        if (!Player.Decoder.HasVideo)
            return;
        var fmt = Player.Video.Format;
        for (var i = 0; i < frameCount; i++)
        {
            var pt = TimeSpan.FromMilliseconds(pacingMs * i);
            using (var black = FallbackImageLoader.TrySolidCpuFrame(fmt, pt))
            {
                if (black is null)
                    return;
                foreach (var logo in _logoSinks)
                {
                    try
                    {
                        var dup = FallbackImageLoader.CloneHoldTemplate(black);
                        logo.SubmitBypassHold(dup);
                    }
                    catch
                    {
                        /* best effort */
                    }
                }
            }

            if (pacingMs > 0 && i < frameCount - 1)
                Thread.Sleep(pacingMs);
        }
    }

    public void SetHoldFallback(bool hold)
    {
        foreach (var l in _logoSinks)
            l.SetHoldFallback(hold);
    }

    /// <summary>
    /// Re-shows the most recent decoded frame on every logo branch at <paramref name="presentationTime"/>.
    /// Called after the user toggles hold-fallback off so single-frame sources (cover art) come back —
    /// no-op for sinks whose cache is empty or for GPU-backed frames that couldn't be CPU-cached.
    /// </summary>
    public void ResubmitLastCachedFramesAt(TimeSpan presentationTime)
    {
        foreach (var l in _logoSinks)
        {
            try { l.ResubmitLastCachedAt(presentationTime); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Pushes the hold template at <paramref name="presentationTime"/> on every logo sink (playback timer).</summary>
    public void PumpHoldFrames(TimeSpan presentationTime)
    {
        foreach (var l in _logoSinks)
        {
            try
            {
                l.SubmitTemplateFrame(presentationTime);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    public void StartAllPortAudio()
    {
        foreach (var o in _portAudioOutputs)
        {
            try { o.Start(); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Primes the video logo branches with a few black frames before <see cref="AvRouter.Play"/>
    /// so SDL / NDI sinks have a configured frame in flight at the negotiated format. NDI audio side no
    /// longer needs a silence warmup here — the persistent <c>NDIOutputPreviewRuntime</c> carrier keeps
    /// receivers locked continuously, so audio just starts at the next router chunk boundary.</summary>
    /// <param name="holdFallbackShowsImage">When true, skip black-frame priming (the hold image already paints the output).</param>
    public void PrepareOutputsBeforePlay(bool holdFallbackShowsImage) =>
        PrimeVideoOutputsBeforePlay(holdFallbackShowsImage);

    /// <summary>
    /// Releases sinks we own when <see cref="TryCreate"/> fails after partial <see cref="WireAudio"/> work.
    /// Does not dispose <see cref="Player"/> or NDI senders.
    /// </summary>
    internal void DisposePartialBeforePlayerDispose()
    {
        foreach (var r in _ndiAudioResamplers)
        {
            try
            {
                r.Dispose();
            }
            catch
            {
                /* best effort */
            }
        }

        _ndiAudioResamplers.Clear();

        foreach (var o in _portAudioOutputs)
        {
            try
            {
                o.Dispose();
            }
            catch
            {
                /* best effort */
            }
        }

        _portAudioOutputs.Clear();
        _ndiAudioSinks.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            // Player.Dispose tears down VideoRouter (and its outputs / pumps) and AudioRouter (and SinkPumps).
            // The NDIVideoSender / NDIAudioSink inside each NDIOutput survive — VideoSinkPump and SinkPump
            // are constructed with disposeInner=false so the underlying NDI sender stays alive for the
            // carrier to resume.
            Player.Dispose();
        }
        catch
        {
            /* best effort */
        }

        foreach (var r in _ndiAudioResamplers)
        {
            try { r.Dispose(); }
            catch { /* best effort */ }
        }

        _ndiAudioResamplers.Clear();

        foreach (var line in _acquiredCarriers)
        {
            try { _outputs.ReleaseNDICarrierForPlayback(line); }
            catch { /* best effort */ }
        }

        _acquiredCarriers.Clear();
        _ndiAudioSinks.Clear();
        _logoSinks.Clear();
        _portAudioOutputs.Clear();
    }
}
