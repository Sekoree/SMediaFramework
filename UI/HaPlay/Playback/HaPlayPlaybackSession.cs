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
    /// <summary>Phase A (§4.3.3, §9.6) — per-line wiring metadata so <see cref="TryAddOutput"/> /
    /// <see cref="TryRemoveOutput"/> can unwind exactly what they wired without disturbing the
    /// original-output path. Populated for both originally-routed lines (so removal of an original
    /// output works) and lines added mid-session.</summary>
    private readonly Dictionary<OutputLineViewModel, LineWiring> _lineWiring = new();
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

    /// <summary>
    /// Phase B (§3.6) — true when this session has acquired any side of <paramref name="line"/>'s
    /// runtime (audio sink, video sink, or NDI carrier). Used by the Edit-while-in-use confirm flow to
    /// decide whether a reconfigure will glitch live playback.
    /// </summary>
    public bool HasWiredLine(OutputLineViewModel line) =>
        _lineWiring.ContainsKey(line)
        || _acquiredCarriers.Contains(line)
        || _acquiredLocalVideoLines.Contains(line)
        || _acquiredPortAudioLines.Contains(line);

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

        var videoChains = new List<(OutputLineViewModel Line, string Id, LogoFallbackVideoSink Sink)>();
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
                        videoChains.Add((line, $"{prefix}_{lv.Id:N}", logo));
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
                        videoChains.Add((line, $"ndi_{nd.Id:N}", logo));
                        Trace.LogDebug("TryCreate: NDI video '{Name}' wired as videoChain[{Idx}] (pumpCap=8)",
                            nd.DisplayName, videoChains.Count - 1);
                        break;
                    }
                }
            }
        }

        Trace.LogDebug("TryCreate: videoChains={Count} lead=(discard)", videoChains.Count);

        if (!MediaPlayer.TryOpen(decoder, mpOpt, videoNegotiationLead: null, disposeNegotiationLead: true,
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
            pendingPlayback = new HaPlayPlaybackSession(player, player.Session, outputs);

            foreach (var (line, id, sink) in videoChains)
            {
                var outId = router.AddOutput(sink, id, disposeSinkOnRouterDispose: true);
                if (!router.TryAddRoute(inputId, outId, out var routeErr))
                    throw new InvalidOperationException(routeErr ?? "TryAddRoute failed");

                var wiring = pendingPlayback.GetOrCreateLineWiring(line);
                wiring.VideoOutputId = outId;
                wiring.LogoSink = sink;
                wiring.AcquiredKind = line.Definition is NDIOutputDefinition ? AcquireKind.NDI : AcquireKind.LocalVideo;
            }

            var isSingleFrameVideo = decoder.HasVideo && decoder.VideoIsAttachedPicture;
            foreach (var sink in videoChains.Select(t => t.Sink))
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
                    var wiring = playback.GetOrCreateLineWiring(line);
                    wiring.AudioSinkId = sinkId;
                    wiring.SinkChannelCount = 2;
                    wiring.Resampler = routerSink is ResamplingAudioSink ? (ResamplingAudioSink)routerSink : wiring.Resampler;
                    wiring.AcquiredKind = AcquireKind.PortAudio;
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
                    var wiring = playback.GetOrCreateLineWiring(line);
                    wiring.AudioSinkId = sinkId;
                    wiring.SinkChannelCount = 2;
                    wiring.Resampler = routerSink is ResamplingAudioSink ? (ResamplingAudioSink)routerSink : wiring.Resampler;
                    wiring.AcquiredKind = AcquireKind.NDI;
                    break;
                }
            }
        }
    }

    private static ChannelMap StereoDownmix(int sourceChannels) =>
        sourceChannels >= 2 ? ChannelMap.Identity(2) : new ChannelMap([0, 0]);

    /// <summary>
    /// Phase C (§4.3.4) — build the channel map for a given mix mode against a stereo sink.
    /// Mono sources fold all non-silence modes to the existing mono-dup downmix; their L/R
    /// distinction is meaningless. Future N×M cell grid replaces this preset table.
    /// </summary>
    internal static ChannelMap MixModeToChannelMap(AudioRouteMixMode mode, int sourceChannels)
    {
        if (sourceChannels < 2)
            return mode == AudioRouteMixMode.Silence ? new ChannelMap([-1, -1]) : new ChannelMap([0, 0]);
        return mode switch
        {
            AudioRouteMixMode.Swap => new ChannelMap([1, 0]),
            AudioRouteMixMode.MonoLeft => new ChannelMap([0, 0]),
            AudioRouteMixMode.MonoRight => new ChannelMap([1, 1]),
            AudioRouteMixMode.Silence => new ChannelMap([-1, -1]),
            _ => ChannelMap.Identity(2),
        };
    }

    /// <summary>
    /// Phase C (§4.3.4) — reconfigure the channel map of an already-wired audio route without tearing
    /// the route down. Calls <see cref="AudioPlayer.Connect"/> which replaces the existing route's
    /// <c>ChannelMap</c> in-place via <c>AudioRouter.AddRoute</c>. Caller must re-apply gain immediately
    /// after — <c>AddRoute</c> resets the route's current gain to the supplied value (default 1.0),
    /// so without a follow-up <see cref="TrySetOutputGain"/> the level would jump.
    /// </summary>
    public bool TrySetOutputChannelMap(OutputLineViewModel line, AudioRouteMixMode mode, float gain,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (!_lineWiring.TryGetValue(line, out var wiring) || wiring.AudioSinkId is null)
            return true;
        if (Player.Audio is null || string.IsNullOrEmpty(Player.AudioSourceId))
            return true;

        // When the matrix path owns this line's routes, the legacy single-route mix-mode reroute is a no-op.
        // The caller should be using TrySetOutputMatrix instead — silently honour both paths so VMs that
        // still poke MixMode (e.g. via "preset" buttons) don't fight the matrix.
        if (wiring.CellRouteIds.Count > 0)
            return true;

        try
        {
            var srcChannels = Player.Decoder.Audio?.Format.Channels ?? 2;
            var map = MixModeToChannelMap(mode, srcChannels);
            Player.Audio.Connect(Player.AudioSourceId!, wiring.AudioSinkId, map, gain);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Phase C (§4.3.4) — install per-cell routing. Replaces any cell routes that were previously
    /// registered for <paramref name="line"/> (idempotent rebuild). Each non-muted, above-floor cell becomes
    /// one router route via <see cref="AudioRouter.AddRoute(string,string,string,ChannelMap,float)"/> with a
    /// stable id so subsequent gain rides via <see cref="TrySetOutputMatrixCompoundGain"/> only touch
    /// <see cref="AudioRouter.SetRouteGainById"/> (click-free per-cell fades).
    /// </summary>
    /// <param name="cells">The intended matrix layout for this line. An empty / all-muted list leaves the
    /// line silent (all per-cell routes removed; the legacy single route, if any, is also dropped).</param>
    /// <param name="compoundEnvelope">Master × per-output linear gain applied on top of each cell's own gain.</param>
    public bool TrySetOutputMatrix(OutputLineViewModel line,
        IReadOnlyList<AudioMatrixCellConfig> cells, float compoundEnvelope, out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (!_lineWiring.TryGetValue(line, out var wiring) || wiring.AudioSinkId is null)
            return true; // video-only line
        if (Player.Audio is null || string.IsNullOrEmpty(Player.AudioSourceId))
            return true;

        try
        {
            var router = Player.Audio.Router;
            // First the legacy single route (if WireAudio's initial Connect installed one). Otherwise the
            // run loop would add the cell contributions *on top of* the original identity route.
            try { router.RemoveRoute(Player.AudioSourceId!, wiring.AudioSinkId); }
            catch { /* tolerate "no route" — back-compat removal sweeps */ }

            // Then any cell routes from a previous matrix push.
            foreach (var rid in wiring.CellRouteIds)
            {
                try { router.RemoveRouteById(rid); }
                catch { /* best effort — racing teardown */ }
            }
            wiring.CellRouteIds.Clear();
            wiring.Cells.Clear();

            var srcChannels = Player.Decoder.Audio?.Format.Channels ?? 0;
            // WireAudio currently downmixes / resamples every audio route to stereo (see ResamplingAudioSink
            // wrapping below). Cache that as our cell-grid row count until per-output channel-aware sinks land.
            wiring.SinkChannelCount = wiring.SinkChannelCount > 0 ? wiring.SinkChannelCount : 2;

            foreach (var cell in cells)
            {
                if (cell.Muted) continue;
                if (cell.GainDb <= AudioMatrixDefaults.MutedFloorDb) continue;
                if (cell.InputChannel < 0 || cell.InputChannel >= srcChannels) continue;
                if (cell.OutputChannel < 0 || cell.OutputChannel >= wiring.SinkChannelCount) continue;

                var map = BuildSingleCellMap(cell.InputChannel, cell.OutputChannel, wiring.SinkChannelCount);
                var cellLinear = (float)Math.Pow(10.0, cell.GainDb / 20.0);
                var routeGain = compoundEnvelope * cellLinear;
                var routeId = $"cell:{wiring.AudioSinkId}:{cell.InputChannel}>{cell.OutputChannel}";
                router.AddRoute(Player.AudioSourceId!, wiring.AudioSinkId, routeId, map, routeGain);
                wiring.CellRouteIds.Add(routeId);
                wiring.Cells.Add(cell);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Phase C (§4.3.4) — click-free gain ride across every cell route belonging to <paramref name="line"/>.
    /// Each cell route ends up at <c>compoundEnvelope × cell.linearGain</c> via
    /// <see cref="AudioRouter.SetRouteGainById"/>. No-op when the line has no cell routes installed (caller
    /// should fall back to <see cref="TrySetOutputGain"/> in that case).
    /// </summary>
    public bool TrySetOutputMatrixCompoundGain(OutputLineViewModel line, float compoundEnvelope,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (!_lineWiring.TryGetValue(line, out var wiring) || wiring.CellRouteIds.Count == 0)
            return false; // no matrix; caller picks the legacy gain path
        if (Player.Audio is null)
            return true;

        var router = Player.Audio.Router;
        for (var i = 0; i < wiring.CellRouteIds.Count; i++)
        {
            var cell = wiring.Cells[i];
            var cellLinear = (float)Math.Pow(10.0, cell.GainDb / 20.0);
            try { router.SetRouteGainById(wiring.CellRouteIds[i], compoundEnvelope * cellLinear); }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        return true;
    }

    /// <summary>Phase C (§4.3.4) — build a one-cell ChannelMap that routes <paramref name="inputChannel"/>
    /// to <paramref name="outputChannel"/> and silences every other output channel.</summary>
    internal static ChannelMap BuildSingleCellMap(int inputChannel, int outputChannel, int sinkChannels)
    {
        var arr = new int[sinkChannels];
        for (var i = 0; i < sinkChannels; i++)
            arr[i] = ChannelMap.Silence;
        arr[outputChannel] = inputChannel;
        return new ChannelMap(arr);
    }


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
    /// Phase A (§4.3.3, §9.6) — wires a new output into a running session without teardown. Mirrors the
    /// per-line work <see cref="TryCreate"/> does at session open: acquires the underlying runtime,
    /// registers an audio sink / video output on the router, adds the appropriate routes, and (for video)
    /// primes the new branch with a black frame so it doesn't appear at the next paint.
    /// </summary>
    /// <remarks>
    /// <para>Returns <c>false</c> when the line's runtime can't be acquired (e.g. another session holds it)
    /// or when <paramref name="line"/> is already wired. The session state stays consistent on failure —
    /// any partial acquisition is rolled back before returning.</para>
    /// <para>Idempotency is enforced via <c>_lineWiring</c>: a second TryAddOutput for the same line
    /// fails with "already added". The Phase B caller can call <see cref="TryRemoveOutput"/> first to
    /// rebuild.</para>
    /// </remarks>
    public bool TryAddOutput(OutputLineViewModel line, out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (_lineWiring.ContainsKey(line))
        {
            errorMessage = $"Output '{line.Definition.DisplayName}' is already wired to this session.";
            return false;
        }

        var hasVideo = Player.Decoder.HasVideo;
        var hasAudio = Player.Decoder.HasAudio;
        var wiring = new LineWiring();

        try
        {
            switch (line.Definition)
            {
                case PortAudioOutputDefinition pa:
                    if (!hasAudio)
                        return Reject(out errorMessage,
                            $"PortAudio output '{pa.DisplayName}' has no audio side to wire (source has no audio).");
                    if (!TryWirePortAudio(line, pa, wiring, out errorMessage))
                        return false;
                    break;

                case LocalVideoOutputDefinition lv:
                    if (!hasVideo)
                        return Reject(out errorMessage,
                            $"Local video output '{lv.DisplayName}' has no video side to wire (source has no video).");
                    if (!TryWireLocalVideo(line, lv, wiring, out errorMessage))
                        return false;
                    break;

                case NDIOutputDefinition nd:
                    if (!TryWireNDI(line, nd, hasVideo, hasAudio, wiring, out errorMessage))
                        return false;
                    break;

                default:
                    return Reject(out errorMessage,
                        $"Unknown output kind for '{line.Definition.DisplayName}'.");
            }

            _lineWiring[line] = wiring;
            Trace.LogInformation("TryAddOutput: '{Name}' wired (audioSink={AS} videoOut={VO})",
                line.Definition.DisplayName, wiring.AudioSinkId ?? "(none)", wiring.VideoOutputId ?? "(none)");
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "TryAddOutput: '{Name}' threw; rolling back partial wiring",
                line.Definition.DisplayName);
            UnwireLineFromRouters(wiring);
            ReleaseRuntimeForLine(line);
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool Reject(out string? errorMessage, string message)
    {
        errorMessage = message;
        return false;
    }

    private bool TryWirePortAudio(OutputLineViewModel line, PortAudioOutputDefinition pa, LineWiring wiring,
        out string? errorMessage)
    {
        errorMessage = null;
        if (Player.Audio is null || string.IsNullOrEmpty(Player.AudioSourceId))
        {
            errorMessage = "Session has no audio router — cannot add audio output.";
            return false;
        }

        var outDev = _outputs.TryAcquirePortAudioForPlayback(line);
        if (outDev is null)
        {
            errorMessage = $"PortAudio '{pa.DisplayName}' is unavailable (another session may hold it).";
            return false;
        }

        _acquiredPortAudioLines.Add(line);
        wiring.AcquiredKind = AcquireKind.PortAudio;

        var dec = Player.Decoder.Audio.Format;
        var needsResample = outDev.Format.SampleRate != dec.SampleRate || outDev.Format.Channels != 2;
        IAudioSink routerSink = outDev;
        if (needsResample)
        {
            var stereoFmt = new AudioFormat(dec.SampleRate, 2);
            var resampler = new ResamplingAudioSink(outDev, stereoFmt);
            _portAudioResamplers.Add(resampler);
            routerSink = resampler;
            wiring.Resampler = resampler;
        }

        var sinkId = Player.Audio.AddOutput(routerSink);
        Player.Audio.Connect(Player.AudioSourceId!, sinkId, StereoDownmix(dec.Channels));
        wiring.AudioSinkId = sinkId;
        wiring.SinkChannelCount = 2;
        return true;
    }

    public bool TrySetOutputGain(OutputLineViewModel line, float gain, out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (!_lineWiring.TryGetValue(line, out var wiring) || wiring.AudioSinkId is null)
            return true; // video-only lines have no audio route to adjust

        if (Player.Audio is null || string.IsNullOrEmpty(Player.AudioSourceId))
            return true;

        try
        {
            Player.Audio.SetVolume(Player.AudioSourceId!, wiring.AudioSinkId, gain);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private bool TryWireLocalVideo(OutputLineViewModel line, LocalVideoOutputDefinition lv, LineWiring wiring,
        out string? errorMessage)
    {
        errorMessage = null;
        var sink = _outputs.TryAcquireLocalVideoSinkForPlayback(line);
        if (sink is null)
        {
            errorMessage = $"Local video '{lv.DisplayName}' is unavailable (preview not running or already held).";
            return false;
        }

        _acquiredLocalVideoLines.Add(line);
        wiring.AcquiredKind = AcquireKind.LocalVideo;

        var prefix = lv.Engine == VideoOutputEngine.SdlOpenGl ? "sdl" : "ava";
        var logo = new LogoFallbackVideoSink(sink, disposeInnerOnDispose: false);
        var outputId = Player.VideoRouter.AddOutput(logo, $"{prefix}_{lv.Id:N}_hot", disposeSinkOnRouterDispose: true);
        if (!Player.VideoRouter.TryAddRoute(Player.VideoRouterInputId, outputId, out var routeErr))
        {
            Player.VideoRouter.RemoveOutput(outputId);
            _acquiredLocalVideoLines.Remove(line);
            try { _outputs.ReleaseLocalVideoSinkForPlayback(line); }
            catch { /* best effort */ }
            errorMessage = routeErr ?? "VideoRouter.TryAddRoute failed.";
            return false;
        }

        wiring.VideoOutputId = outputId;
        wiring.LogoSink = logo;
        _logoSinks.Add(logo);
        // Keep single-frame source mode in sync with the existing branches so attached-pic sources don't
        // show garbage on the newly added branch.
        logo.SetSingleFrameSourceMode(Player.Decoder.HasVideo && Player.Decoder.VideoIsAttachedPicture);
        return true;
    }

    private bool TryWireNDI(OutputLineViewModel line, NDIOutputDefinition nd, bool hasVideo, bool hasAudio,
        LineWiring wiring, out string? errorMessage)
    {
        errorMessage = null;
        var needsVideo = hasVideo && nd.StreamMode != NDIOutputStreamMode.AudioOnly;
        var needsAudio = hasAudio && nd.StreamMode != NDIOutputStreamMode.VideoOnly;
        if (!needsVideo && !needsAudio)
        {
            errorMessage = $"NDI '{nd.DisplayName}' stream mode and source have no overlap.";
            return false;
        }

        var ndi = _outputs.TryAcquireNDICarrierForPlayback(line, needsVideo, needsAudio);
        if (ndi is null)
        {
            errorMessage = $"NDI carrier '{nd.DisplayName}' is unavailable.";
            return false;
        }

        _acquiredCarriers.Add(line);
        wiring.AcquiredKind = AcquireKind.NDI;

        if (needsVideo)
        {
            var pump = new VideoSinkPump(ndi.VideoSink, maxQueuedFrames: 8, name: $"ndi-{nd.Id:N}-hot", log: null,
                disposeInnerOnDispose: false);
            var logo = new LogoFallbackVideoSink(pump, disposeInnerOnDispose: true);
            var outputId = Player.VideoRouter.AddOutput(logo, $"ndi_{nd.Id:N}_hot", disposeSinkOnRouterDispose: true);
            if (!Player.VideoRouter.TryAddRoute(Player.VideoRouterInputId, outputId, out var routeErr))
            {
                Player.VideoRouter.RemoveOutput(outputId);
                _acquiredCarriers.Remove(line);
                try { _outputs.ReleaseNDICarrierForPlayback(line); }
                catch { /* best effort */ }
                errorMessage = routeErr ?? "VideoRouter.TryAddRoute failed (NDI).";
                return false;
            }

            wiring.VideoOutputId = outputId;
            wiring.LogoSink = logo;
            _logoSinks.Add(logo);
            logo.SetSingleFrameSourceMode(Player.Decoder.HasVideo && Player.Decoder.VideoIsAttachedPicture);
        }

        if (needsAudio && Player.Audio is not null && !string.IsNullOrEmpty(Player.AudioSourceId))
        {
            var dec = Player.Decoder.Audio.Format;
            var ndiAudioFmt = new AudioFormat(nd.AudioSampleRate, 2);
            var ndiSink = ndi.EnableAudio(ndiAudioFmt);
            _ndiAudioSinks.Add(ndiSink);

            IAudioSink routerSink = ndiSink;
            if (ndiAudioFmt.SampleRate != dec.SampleRate)
            {
                var stereoFmt = new AudioFormat(dec.SampleRate, 2);
                var resampler = new ResamplingAudioSink(ndiSink, stereoFmt);
                _ndiAudioResamplers.Add(resampler);
                routerSink = resampler;
                wiring.Resampler = resampler;
            }

            var sinkId = Player.Audio.AddOutput(routerSink);
            Player.Audio.Connect(Player.AudioSourceId!, sinkId, StereoDownmix(dec.Channels));
            wiring.AudioSinkId = sinkId;
            wiring.SinkChannelCount = 2;
        }

        return true;
    }

    /// <summary>
    /// Phase A (§9.6) — removes a previously-wired output from the running session. Mirror of
    /// <see cref="TryAddOutput"/>. Returns <c>false</c> when the line isn't currently wired.
    /// </summary>
    public bool TryRemoveOutput(OutputLineViewModel line, out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (!_lineWiring.Remove(line, out var wiring))
        {
            errorMessage = $"Output '{line.Definition.DisplayName}' is not currently wired to this session.";
            return false;
        }

        UnwireLineFromRouters(wiring);
        ReleaseRuntimeForLine(line);

        // Drop the line's logo sink from the hold-image pump list so the next PumpHoldFrames doesn't
        // hit a sink whose underlying output we just released. (Router.RemoveOutput already disposed
        // the wrapping VideoSinkPump for NDI; for local video the LogoFallbackVideoSink wrapped a sink
        // we don't own.)
        if (wiring.LogoSink is not null)
            _logoSinks.Remove(wiring.LogoSink);

        Trace.LogInformation("TryRemoveOutput: '{Name}' unwired", line.Definition.DisplayName);
        return true;
    }

    private void UnwireLineFromRouters(LineWiring wiring)
    {
        if (wiring.AudioSinkId is { } audioSinkId && Player.Audio is not null)
        {
            try { Player.Audio.RemoveOutput(audioSinkId); }
            catch (Exception ex) { Trace.LogWarning(ex, "UnwireLineFromRouters: AudioPlayer.RemoveOutput({Id})", audioSinkId); }
        }

        if (wiring.VideoOutputId is { } videoOutputId)
        {
            try { Player.VideoRouter.RemoveOutput(videoOutputId); }
            catch (Exception ex) { Trace.LogWarning(ex, "UnwireLineFromRouters: VideoRouter.RemoveOutput({Id})", videoOutputId); }
        }

        if (wiring.Resampler is not null)
        {
            // Resampler is in _portAudioResamplers or _ndiAudioResamplers; drop the reference so Dispose
            // doesn't double-free, then dispose it locally so its internal swr_ctx is released promptly.
            _portAudioResamplers.Remove(wiring.Resampler);
            _ndiAudioResamplers.Remove(wiring.Resampler);
            try { wiring.Resampler.Dispose(); }
            catch { /* best effort */ }
        }
    }

    private void ReleaseRuntimeForLine(OutputLineViewModel line)
    {
        switch (line.Definition)
        {
            case PortAudioOutputDefinition:
                if (_acquiredPortAudioLines.Remove(line))
                {
                    try { _outputs.ReleasePortAudioForPlayback(line); }
                    catch { /* best effort */ }
                }
                break;
            case LocalVideoOutputDefinition:
                if (_acquiredLocalVideoLines.Remove(line))
                {
                    try { _outputs.ReleaseLocalVideoSinkForPlayback(line); }
                    catch { /* best effort */ }
                }
                break;
            case NDIOutputDefinition:
                if (_acquiredCarriers.Remove(line))
                {
                    try { _outputs.ReleaseNDICarrierForPlayback(line); }
                    catch { /* best effort */ }
                }
                break;
        }
    }

    private sealed class LineWiring
    {
        public string? AudioSinkId { get; set; }
        public string? VideoOutputId { get; set; }
        public LogoFallbackVideoSink? LogoSink { get; set; }
        public ResamplingAudioSink? Resampler { get; set; }
        public AcquireKind AcquiredKind { get; set; }

        /// <summary>Phase C (§4.3.4) — when the per-cell matrix is in use, the list of router route ids
        /// installed for each non-zero cell. Empty when the line is using the single-route mix-mode path.</summary>
        public List<string> CellRouteIds { get; } = new();
        /// <summary>Phase C (§4.3.4) — cached cell configs so master/per-output gain rides can recompute
        /// the per-route gain via <see cref="AudioRouter.SetRouteGainById"/> without re-adding routes.</summary>
        public List<AudioMatrixCellConfig> Cells { get; } = new();
        /// <summary>Sink channel count cached at first wiring (needed for building per-cell ChannelMaps without
        /// re-querying the router each time).</summary>
        public int SinkChannelCount { get; set; }
    }

    private enum AcquireKind { None, PortAudio, LocalVideo, NDI }

    private LineWiring GetOrCreateLineWiring(OutputLineViewModel line)
    {
        if (!_lineWiring.TryGetValue(line, out var wiring))
        {
            wiring = new LineWiring();
            _lineWiring[line] = wiring;
        }

        return wiring;
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
