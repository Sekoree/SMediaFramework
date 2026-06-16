using System.Threading;
using System.Diagnostics.CodeAnalysis;
using HaPlay.ViewModels;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Playback;
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

internal sealed partial class HaPlayPlaybackSession
{
    /// <summary>
    /// Phase A (§4.3.3, §9.6) — wires a new output into a running session without teardown. Mirrors the
    /// per-line work <see cref="TryCreate"/> does at session open: acquires the underlying runtime,
    /// registers an audio output / video output on the router, adds the appropriate routes, and (for video)
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

        var hasVideo = IsLive ? LiveHasVideo : Player.Decoder.HasVideo;
        var hasAudio = IsLive ? LiveHasAudio : Player.Decoder.HasAudio;
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
            Trace.LogInformation("TryAddOutput: '{Name}' wired (audioOut={AS} videoOut={VO})",
                line.Definition.DisplayName, wiring.AudioOutputId ?? "(none)", wiring.VideoOutputId ?? "(none)");
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
        if (Player.AudioRouter is null || string.IsNullOrEmpty(Player.AudioSourceId))
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
        wiring.PortAudioOutput = outDev;
        wiring.PortAudioUnderrunBaseline = outDev.UnderrunSamples;

        if (!TryGetSourceAudioFormat(out var dec))
        {
            _acquiredPortAudioLines.Remove(line);
            wiring.PortAudioOutput = null;
            try { _outputs.ReleasePortAudioForPlayback(line); } catch { /* best effort */ }
            errorMessage = "Source audio format is unavailable.";
            return false;
        }
        var sinkChannels = GetOutputChannelCount(pa);
        var targetFmt = new AudioFormat(dec.SampleRate, sinkChannels);
        var map = DefaultChannelMap(dec.Channels, sinkChannels);
        var needsResample = outDev.Format.SampleRate != dec.SampleRate || outDev.Format.Channels != sinkChannels;
        IAudioOutput routerSink = outDev;
        if (needsResample)
        {
            var resampler = ResamplingAudioOutput.Wrap(outDev, targetFmt);
            _portAudioResamplers.Add(resampler);
            routerSink = resampler;
            wiring.Resampler = resampler;
        }

        var outputId = Player.AudioRouter!.AddOutput(routerSink);
        Player.AudioRouter!.Connect(Player.AudioSourceId!, outputId, map);
        wiring.AudioOutputId = outputId;
        wiring.SinkChannelCount = sinkChannels;
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

        if (!_lineWiring.TryGetValue(line, out var wiring) || wiring.AudioOutputId is null)
            return true; // video-only lines have no audio route to adjust

        if (Player.AudioRouter is null || string.IsNullOrEmpty(Player.AudioSourceId))
            return true;

        try
        {
            Player.AudioRouter!.SetRouteGain(Player.AudioSourceId!, wiring.AudioOutputId, gain);
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
        var output = _outputs.TryAcquireLocalVideoOutputForPlayback(line);
        if (output is null)
        {
            errorMessage = $"Local video '{lv.DisplayName}' is unavailable (preview not running or already held).";
            return false;
        }

        _acquiredLocalVideoLines.Add(line);
        wiring.AcquiredKind = AcquireKind.LocalVideo;

        var prefix = lv.Engine == VideoOutputEngine.SdlOpenGl ? "sdl" : "ava";
        var logo = new LogoFallbackVideoOutput(output, disposeInnerOnDispose: false);
        var outputId = Player.VideoRouter.AddOutput(logo, $"{prefix}_{lv.Id:N}_hot", disposeOutputOnRouterDispose: true);
        if (!Player.VideoRouter.TryAddRoute(Player.VideoRouterInputId, outputId, out var routeErr))
        {
            Player.VideoRouter.RemoveOutput(outputId);
            _acquiredLocalVideoLines.Remove(line);
            try { _outputs.ReleaseLocalVideoOutputForPlayback(line); }
            catch { /* best effort */ }
            errorMessage = routeErr ?? "VideoRouter.TryAddRoute failed.";
            return false;
        }

        wiring.VideoOutputId = outputId;
        wiring.LogoOutput = logo;
        _logoSinks.Add(logo);
        // Keep single-frame source mode in sync with the existing branches so attached-pic sources don't
        // show garbage on the newly added branch.
        logo.SetSingleFrameSourceMode(!IsLive && Player.Decoder.HasVideo && Player.Decoder.VideoIsAttachedPicture);
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
            var lockedSink = WrapWithNDILockIfNeeded(ndi.Video, nd, $"ndi-{nd.Id:N}-hot");
            var pump = new VideoOutputPump(lockedSink, maxQueuedFrames: 8, name: $"ndi-{nd.Id:N}-hot", log: null,
                disposeInnerOnDispose: !ReferenceEquals(lockedSink, ndi.Video));
            var logo = new LogoFallbackVideoOutput(pump, disposeInnerOnDispose: true);
            var outputId = Player.VideoRouter.AddOutput(logo, $"ndi_{nd.Id:N}_hot", disposeOutputOnRouterDispose: true);
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
            wiring.LogoOutput = logo;
            _logoSinks.Add(logo);
            logo.SetSingleFrameSourceMode(!IsLive && Player.Decoder.HasVideo && Player.Decoder.VideoIsAttachedPicture);
        }

        if (needsAudio && Player.AudioRouter is not null && !string.IsNullOrEmpty(Player.AudioSourceId))
        {
            if (!TryGetSourceAudioFormat(out var dec))
            {
                errorMessage = "Source audio format is unavailable.";
                return false;
            }
            var sinkChannels = GetOutputChannelCount(nd);
            var ndiAudioFmt = new AudioFormat(nd.AudioSampleRate, sinkChannels);
            var targetFmt = new AudioFormat(dec.SampleRate, sinkChannels);
            var map = DefaultChannelMap(dec.Channels, sinkChannels);
            var ndiOutput = ndi.EnableAudio(ndiAudioFmt);
            _ndiAudioOutputs.Add(ndiOutput);

            IAudioOutput routerSink = ndiOutput;
            if (ndiAudioFmt.SampleRate != dec.SampleRate)
            {
                var resampler = ResamplingAudioOutput.Wrap(ndiOutput, targetFmt);
                _ndiAudioResamplers.Add(resampler);
                routerSink = resampler;
                wiring.Resampler = resampler;
            }

            var outputId = Player.AudioRouter!.AddOutput(routerSink);
            Player.AudioRouter!.Connect(Player.AudioSourceId!, outputId, map);
            wiring.AudioOutputId = outputId;
            wiring.SinkChannelCount = sinkChannels;
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

        // Drop the line's logo output from the hold-image pump list so the next PumpHoldFrames doesn't
        // hit a output whose underlying output we just released. (Router.RemoveOutput already disposed
        // the wrapping VideoOutputPump for NDI; for local video the LogoFallbackVideoOutput wrapped a output
        // we don't own.)
        if (wiring.LogoOutput is not null)
            _logoSinks.Remove(wiring.LogoOutput);

        Trace.LogInformation("TryRemoveOutput: '{Name}' unwired", line.Definition.DisplayName);
        return true;
    }

    private void UnwireLineFromRouters(LineWiring wiring)
    {
        if (wiring.AudioOutputId is { } audioSinkId && Player.AudioRouter is not null)
        {
            try { Player.AudioRouter!.RemoveOutput(audioSinkId); }
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

        RestorePortAudioTargetQueueIfNeeded(wiring);
    }

    /// <summary>Phase C.5 — undo the live-session <c>TargetQueueSamples</c> override so the next
    /// (possibly file-based) acquirer of the persistent PortAudio runtime sees the original target.</summary>
    private static void RestorePortAudioTargetQueueIfNeeded(LineWiring wiring)
    {
        if (wiring.PortAudioForTargetRestore is { } pa && wiring.PreviousPortAudioTargetQueue is { } prev)
        {
            try { pa.TargetQueueSamples = prev; }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "RestorePortAudioTargetQueueIfNeeded: TargetQueueSamples reset to {Prev} threw", prev);
            }

            wiring.PortAudioForTargetRestore = null;
            wiring.PreviousPortAudioTargetQueue = null;
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
                    try { _outputs.ReleaseLocalVideoOutputForPlayback(line); }
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
        public string? AudioOutputId { get; set; }
        public string? VideoOutputId { get; set; }
        public LogoFallbackVideoOutput? LogoOutput { get; set; }
        public ResamplingAudioOutput? Resampler { get; set; }
        public AcquireKind AcquiredKind { get; set; }

        /// <summary>Phase C.5 — live sessions lower the wrapped PortAudio's <c>TargetQueueSamples</c>
        /// to avoid the startup chunk-burst that would overflow the per-output pump. Held here so we can
        /// restore the original target when the wiring is torn down (the persistent runtime keeps the
        /// stream and would otherwise hand the modified target to the next session).</summary>
        public PortAudioOutput? PortAudioForTargetRestore { get; set; }

        /// <summary>Previous <c>TargetQueueSamples</c> recorded before the live-session override —
        /// paired with <see cref="PortAudioForTargetRestore"/>. <c>null</c> when no override was applied.</summary>
        public int? PreviousPortAudioTargetQueue { get; set; }

        /// <summary>Phase C (§4.3.4) — when the per-cell matrix is in use, the list of router route ids
        /// installed for each non-zero cell. Empty when the line is using the single-route mix-mode path.</summary>
        /// <summary>Phase C (§4.3.4) — cached cell configs so master/per-output gain rides can recompute
        /// the per-route gain via <see cref="AudioRouter.SetRouteGainById"/> without re-adding routes.</summary>
        public List<AudioMatrixCellConfig> Cells { get; } = new();
        /// <summary>Output channel count cached at first wiring (needed for building per-cell ChannelMaps without
        /// re-querying the router each time).</summary>
        public int SinkChannelCount { get; set; }

        public PortAudioOutput? PortAudioOutput { get; set; }
        public long PortAudioUnderrunBaseline { get; set; }
        public long VideoSubmittedBaseline { get; set; }
        public long VideoDroppedBaseline { get; set; }
        public long AudioEnqueuedBaseline { get; set; }
        public long AudioProcessedBaseline { get; set; }
        public long AudioDroppedBaseline { get; set; }
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
}
