using System.Threading;
using System.Diagnostics.CodeAnalysis;
using HaPlay.Models;
using HaPlay.ViewModels;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;
using S.Media.Playback;
using S.Media.PortAudio;

namespace HaPlay.Playback;

internal sealed class HaPlayPlaybackSession : IDisposable
{
    private readonly List<LogoFallbackVideoSink> _logoSinks = new();
    private readonly List<OutputLineViewModel> _acquiredCarriers = new();
    private readonly List<OutputLineViewModel> _acquiredLocalVideoLines = new();
    private readonly List<OutputLineViewModel> _acquiredPortAudioLines = new();
    private readonly OutputManagementViewModel _outputs;
    private readonly List<ResamplingAudioSink> _portAudioResamplers = new();
    private readonly List<ResamplingAudioSink> _ndiAudioResamplers = new();
    private readonly List<NDIAudioSink> _ndiAudioSinks = new();
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.HaPlayPlaybackSession");

    private HaPlayPlaybackSession(MediaPlayer player, MediaContainerSession router, OutputManagementViewModel outputs)
    {
        Player = player;
        Router = router;
        _outputs = outputs;
    }

    public MediaPlayer Player { get; }
    public MediaContainerSession Router { get; }

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
        Trace.LogInformation("TryCreate: path={Path} selectedOutputs={Count} ([{Kinds}]) anyNDI={AnyNDI}",
            mediaPath, lines.Count,
            string.Join(",", lines.Select(l => l.Definition.GetType().Name)),
            anyNDI);
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
            Trace.LogError(ex, $"TryCreate: decoder open failed for {mediaPath}");
            errorMessage = ex.Message;
            return false;
        }

        var hasVideo = decoder.HasVideo;
        var hasAudio = decoder.HasAudio;
        Trace.LogDebug("TryCreate: decoder opened (hasVideo={V} hasAudio={A} attachedPic={Attached})",
            hasVideo, hasAudio, hasVideo && decoder.VideoIsAttachedPicture);

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

            Trace.LogTrace("TryCreate: acquiring NDI carrier '{Name}' (needsVideo={V} needsAudio={A})",
                nd.DisplayName, needsVideo, needsAudio);
            var ndi = outputs.TryAcquireNDICarrierForPlayback(line, needsVideo, needsAudio);
            if (ndi is null)
            {
                Trace.LogWarning("TryCreate: NDI carrier '{Name}' unavailable — aborting", nd.DisplayName);
                ReleaseAcquiredCarriers(outputs, acquired);
                decoder.Dispose();
                errorMessage = $"NDI output '{nd.DisplayName}' has no live carrier (was it just removed, or is another player using it?).";
                return false;
            }

            ndiByDefinitionId[nd.Id] = ndi;
            acquired.Add(line);
            Trace.LogDebug("TryCreate: NDI carrier '{Name}' acquired", nd.DisplayName);
        }

        var videoChains = new List<(string Id, IVideoSink Sink)>();
        var acquiredLocalLines = new List<OutputLineViewModel>();
        if (hasVideo)
        {
            foreach (var line in lines)
            {
                switch (line.Definition)
                {
                    case LocalVideoOutputDefinition lv:
                    {
                        // Persistent window: acquire the sink from the preview runtime so a single window
                        // serves successive media without being recreated. disposeInner=false keeps the
                        // sink alive when the router tears down its wrappers; Dispose releases the line.
                        var sink = outputs.TryAcquireLocalVideoSinkForPlayback(line);
                        if (sink is null)
                        {
                            Trace.LogWarning("TryCreate: local video '{Name}' ({Engine}) returned null on acquire — skipping",
                                lv.DisplayName, lv.Engine);
                            continue;
                        }
                        acquiredLocalLines.Add(line);
                        var prefix = lv.Engine == VideoOutputEngine.SdlOpenGl ? "sdl" : "ava";
                        var logo = new LogoFallbackVideoSink(sink, disposeInnerOnDispose: false);
                        videoChains.Add(($"{prefix}_{lv.Id:N}", logo));
                        Trace.LogDebug("TryCreate: local video '{Name}' ({Engine}) wired as videoChain[{Idx}]",
                            lv.DisplayName, lv.Engine, videoChains.Count - 1);
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
                        Trace.LogDebug("TryCreate: NDI video '{Name}' wired as videoChain[{Idx}] (pumpCap=8)",
                            nd.DisplayName, videoChains.Count - 1);
                        break;
                    }
                }
            }
        }

        var lead = videoChains.Count > 0 ? videoChains[0].Sink : null;
        Trace.LogDebug("TryCreate: videoChains={Count} lead={Lead}",
            videoChains.Count, lead is null ? "(discard)" : videoChains[0].Id);

        if (!MediaPlayer.TryOpen(decoder, mpOpt, lead, disposeNegotiationLead: true,
                MediaPlayerDecoderOwnership.BundleDisposesDecoder, out var player, out errorMessage))
        {
            // BundleDisposesDecoder disposes the decoder for us on failure.
            Trace.LogWarning("TryCreate: MediaPlayer.TryOpen failed: {Error}", errorMessage ?? "(no message)");
            ReleaseAcquiredLocalVideoLines(outputs, acquiredLocalLines);
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

            var containerSession = player.Session;
            pendingPlayback = new HaPlayPlaybackSession(player, containerSession, outputs);

            var isSingleFrameVideo = decoder.HasVideo && decoder.VideoIsAttachedPicture;
            foreach (var sink in videoChains.Select(t => t.Sink).OfType<LogoFallbackVideoSink>())
            {
                sink.SetSingleFrameSourceMode(isSingleFrameVideo);
                pendingPlayback._logoSinks.Add(sink);
            }

            pendingPlayback._acquiredCarriers.AddRange(acquired);
            pendingPlayback._acquiredLocalVideoLines.AddRange(acquiredLocalLines);

            WireAudio(lines, ndiByDefinitionId, player, pendingPlayback, outputs);
            Trace.LogInformation("TryCreate: success — videoChains={V} portAudio={Pa} ndiAudio={Na} audioSrc={Src}",
                videoChains.Count, pendingPlayback._acquiredPortAudioLines.Count,
                pendingPlayback._ndiAudioSinks.Count, player.AudioSourceId ?? "(none)");
            session = pendingPlayback;
            pendingPlayback = null;
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "TryCreate: post-open wiring failed, tearing down");
            errorMessage = ex.Message;
            pendingPlayback?.DisposePartialBeforePlayerDispose();
            player.Dispose();
            ReleaseAcquiredLocalVideoLines(outputs, acquiredLocalLines);
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

    private static void ReleaseAcquiredLocalVideoLines(OutputManagementViewModel outputs, List<OutputLineViewModel> acquired)
    {
        foreach (var line in acquired)
        {
            try { outputs.ReleaseLocalVideoSinkForPlayback(line); }
            catch { /* best effort */ }
        }
    }

    private static void WireAudio(List<OutputLineViewModel> lines, Dictionary<Guid, NDIOutput> ndiByDefinitionId,
        MediaPlayer player, HaPlayPlaybackSession playback, OutputManagementViewModel outputs)
    {
        if (player.Audio is null || string.IsNullOrEmpty(player.AudioSourceId))
        {
            Trace.LogDebug("WireAudio: skipped (no audio router or no audio source)");
            return;
        }

        var dec = player.Decoder.Audio.Format;
        var stereoFmt = new AudioFormat(dec.SampleRate, 2);
        var map = StereoDownmix(dec.Channels);
        Trace.LogDebug("WireAudio: decoder={Dec} downmixTo={Stereo} sourceChannels={Ch}",
            dec, stereoFmt, dec.Channels);

        foreach (var line in lines)
        {
            switch (line.Definition)
            {
                case PortAudioOutputDefinition pa:
                {
                    // Acquire the persistent PortAudio output owned by OutputManagementViewModel —
                    // the stream stays open across sessions so receivers don't see Pa_OpenStream cost
                    // on every track change. Different decoder sample rates flow through a per-session
                    // ResamplingAudioSink (the wrapper drops IClockedSink/IPlaybackClock, so the router
                    // pacing falls back to its wall clock — acceptable for our chunk sizes).
                    var outDev = outputs.TryAcquirePortAudioForPlayback(line);
                    if (outDev is null)
                    {
                        Trace.LogWarning("WireAudio: PortAudio '{Name}' returned null on acquire — skipping",
                            pa.DisplayName);
                        continue;
                    }
                    playback._acquiredPortAudioLines.Add(line);

                    IAudioSink routerSink = outDev;
                    var needsResample = outDev.Format.SampleRate != dec.SampleRate || outDev.Format.Channels != stereoFmt.Channels;
                    if (needsResample)
                    {
                        var resampler = new ResamplingAudioSink(outDev, stereoFmt);
                        playback._portAudioResamplers.Add(resampler);
                        routerSink = resampler;
                        Trace.LogDebug("WireAudio: PortAudio '{Name}' wrapped in ResamplingAudioSink (decoder={Dec} hardware={Hw}) — router will NOT slave to PortAudio",
                            pa.DisplayName, dec, outDev.Format);
                    }
                    else
                    {
                        Trace.LogDebug("WireAudio: PortAudio '{Name}' wired direct ({Fmt}) — eligible to slave router",
                            pa.DisplayName, outDev.Format);
                    }

                    var sinkId = player.Audio.AddOutput(routerSink);
                    player.Audio.Connect(player.AudioSourceId!, sinkId, map);
                    break;
                }
                case NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.VideoOnly:
                {
                    if (!ndiByDefinitionId.TryGetValue(nd.Id, out var ndi))
                    {
                        Trace.LogTrace("WireAudio: NDI '{Name}' not in acquired carrier set — skipping audio side",
                            nd.DisplayName);
                        continue;
                    }
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
                        Trace.LogDebug("WireAudio: NDI '{Name}' wrapped in ResamplingAudioSink (decoder={Dec} ndi={Ndi})",
                            nd.DisplayName, dec, ndiAudioFmt);
                    }
                    else
                    {
                        Trace.LogDebug("WireAudio: NDI '{Name}' wired direct ({Fmt})", nd.DisplayName, ndiAudioFmt);
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


    public void ApplyFallbackImage(string? path)
    {
        if (_logoSinks.Count == 0)
            return;
        if (string.IsNullOrWhiteSpace(path))
        {
            foreach (var l in _logoSinks)
            {
                l.TrySetHoldTemplate(null);
                l.ApplyImageOverrideFormat(null);
            }
            RestoreLocalVideoWindowSizes();
            return;
        }

        // Phase 3 — load the image at its native dimensions and use it both as the output format
        // and the template. The output is resized to the image (not the image scaled into the output).
        var fr = Player.Video.Format.FrameRate;
        var proto = FallbackImageLoader.TryBuildHoldFrameAtImageSize(path, fr);
        if (proto is null)
            return;
        try
        {
            foreach (var l in _logoSinks)
            {
                l.ApplyImageOverrideFormat(proto.Format);
                l.TrySetHoldTemplate(FallbackImageLoader.CloneHoldTemplate(proto));
            }

            ApplyLocalVideoWindowSizes(proto.Format.Width, proto.Format.Height);
        }
        finally
        {
            proto.Dispose();
        }
    }

    private void ApplyLocalVideoWindowSizes(int width, int height)
    {
        foreach (var line in _acquiredLocalVideoLines)
        {
            try { _outputs.ApplyHoldImageWindowSize(line, width, height); }
            catch { /* best effort */ }
        }
    }

    private void RestoreLocalVideoWindowSizes()
    {
        foreach (var line in _acquiredLocalVideoLines)
        {
            try { _outputs.ApplyHoldImageWindowSize(line, null, null); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Pushes several black frames through the video router so NDI/SDL receivers stabilize before
    /// <see cref="MediaContainerSession.Play"/>. Frames flow through the router's converters so each
    /// branch sees its configured pixel format (NDI gets post-conversion Uyvy/NV12/etc.; Avalonia
    /// keeps the source format). Must be called with the session paused and before the hold pump timer runs.
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
        var input = Player.VideoInputSink;
        for (var i = 0; i < frameCount; i++)
        {
            var pt = TimeSpan.FromMilliseconds(pacingMs * i);
            var black = FallbackImageLoader.TrySolidCpuFrame(fmt, pt);
            if (black is null)
                return;
            try
            {
                // Router takes ownership of the frame whether SubmitLocked succeeds or throws an
                // InvalidOperationException; only shutdown-race ObjectDisposedExceptions can leave
                // the frame undisposed, so defensively clean up in the catch.
                input.Submit(black);
            }
            catch
            {
                try { black.Dispose(); } catch { /* best effort */ }
            }

            if (pacingMs > 0 && i < frameCount - 1)
                Thread.Sleep(pacingMs);
        }
    }

    public void SetHoldFallback(bool hold)
    {
        foreach (var l in _logoSinks)
            l.SetHoldFallback(hold);
        if (!hold)
        {
            // Drop the image-format override so decoded frames resume at the decoder's negotiated size.
            foreach (var l in _logoSinks)
            {
                try { l.ApplyImageOverrideFormat(null); }
                catch { /* best effort */ }
            }

            // Restore each local video window to its user-defined size so the decoded image fills it again.
            RestoreLocalVideoWindowSizes();
        }
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
        // PortAudio streams are owned by OutputManagementViewModel's persistent runtimes and stay
        // running across sessions. Nothing to start here per-session — kept as a no-op so the
        // MediaContainerSession.Play(startHardware: ...) callback signature is unchanged.
    }

    private int _primedOnce;

    /// <summary>Primes the video logo branches with a few black frames before <see cref="MediaContainerSession.Play"/>
    /// so SDL / NDI sinks have a configured frame in flight at the negotiated format. NDI audio side no
    /// longer needs a silence warmup here — the persistent <c>NDIOutputPreviewRuntime</c> carrier keeps
    /// receivers locked continuously, so audio just starts at the next router chunk boundary.
    ///
    /// Phase 2A: priming runs ONCE per session (the first Play after open). Subsequent Play / Seek / loop-wrap
    /// calls skip it — the sink already has a configured frame in flight and the NDI carrier keeps emitting.
    /// Saves the per-click ~24 ms wall the previous always-prime path imposed.</summary>
    /// <param name="holdFallbackShowsImage">When true, skip black-frame priming (the hold image already paints the output).</param>
    public void PrepareOutputsBeforePlay(bool holdFallbackShowsImage)
    {
        if (Interlocked.CompareExchange(ref _primedOnce, 1, 0) != 0)
            return;
        PrimeVideoOutputsBeforePlay(holdFallbackShowsImage);
    }

    /// <summary>
    /// Releases sinks we own when <see cref="TryCreate"/> fails after partial <see cref="WireAudio"/> work.
    /// Does not dispose <see cref="Player"/> or NDI senders.
    /// </summary>
    internal void DisposePartialBeforePlayerDispose()
    {
        foreach (var r in _ndiAudioResamplers)
        {
            try { r.Dispose(); }
            catch { /* best effort */ }
        }

        _ndiAudioResamplers.Clear();

        foreach (var r in _portAudioResamplers)
        {
            try { r.Dispose(); }
            catch { /* best effort */ }
        }

        _portAudioResamplers.Clear();

        foreach (var line in _acquiredPortAudioLines)
        {
            try { _outputs.ReleasePortAudioForPlayback(line); }
            catch { /* best effort */ }
        }

        _acquiredPortAudioLines.Clear();
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

        foreach (var r in _portAudioResamplers)
        {
            try { r.Dispose(); }
            catch { /* best effort */ }
        }

        _portAudioResamplers.Clear();

        foreach (var line in _acquiredPortAudioLines)
        {
            try { _outputs.ReleasePortAudioForPlayback(line); }
            catch { /* best effort */ }
        }

        _acquiredPortAudioLines.Clear();

        foreach (var line in _acquiredCarriers)
        {
            try { _outputs.ReleaseNDICarrierForPlayback(line); }
            catch { /* best effort */ }
        }

        _acquiredCarriers.Clear();

        // Reset the persistent local-video sinks back to idle preview so the windows stay alive but
        // stop showing the just-played media's last frame.
        foreach (var line in _acquiredLocalVideoLines)
        {
            try { _outputs.ReleaseLocalVideoSinkForPlayback(line); }
            catch { /* best effort */ }
        }

        _acquiredLocalVideoLines.Clear();
        _ndiAudioSinks.Clear();
        _logoSinks.Clear();
    }
}
