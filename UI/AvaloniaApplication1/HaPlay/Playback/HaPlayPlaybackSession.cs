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
    private readonly Dictionary<Guid, NDIOutput> _ndiByDefinitionId = new();
    private readonly List<PortAudioOutput> _portAudioOutputs = new();
    private readonly List<ResamplingAudioSink> _ndiAudioResamplers = new();
    private readonly List<NDIAudioSink> _ndiAudioSinks = new();
    private bool _disposed;

    private HaPlayPlaybackSession(MediaPlayer player, AvRouter router)
    {
        Player = player;
        Router = router;
    }

    public MediaPlayer Player { get; }
    public AvRouter Router { get; }

    public IReadOnlyList<LogoFallbackVideoSink> LogoSinks => _logoSinks;

    public static bool TryCreate(
        string mediaPath,
        IReadOnlyList<OutputLineViewModel> selectedOutputs,
        OutputManagementViewModel _,
        [NotNullWhen(true)] out HaPlayPlaybackSession? session,
        out string? errorMessage)
    {
        session = null;
        errorMessage = null;
        // Caller must stop output previews on the UI thread before TryCreate (previews touch bound controls / SDL).

        var lines = selectedOutputs.ToList();
        var anyNdi = lines.Exists(static l => l.Definition is NdiOutputDefinition);
        var mpOpt = new MediaPlayerOpenOptions(
            TryHardwareAcceleration: !anyNdi,
            IncludeAudioRouter: true);

        var ndiById = new Dictionary<Guid, NDIOutput>();
        foreach (var line in lines)
        {
            if (line.Definition is not NdiOutputDefinition nd)
                continue;
            if (ndiById.ContainsKey(nd.Id))
                continue;
            ndiById[nd.Id] = CreateNdiOutput(nd);
        }

        var videoChains = new List<(string Id, IVideoSink Sink)>();
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
                case NdiOutputDefinition nd when nd.StreamMode != NdiOutputStreamMode.AudioOnly:
                {
                    var ndi = ndiById[nd.Id];
                    var pump = new VideoSinkPump(ndi.VideoSink, maxQueuedFrames: 8, name: $"ndi-{nd.Id:N}", log: null,
                        disposeInnerOnDispose: false);
                    var logo = new LogoFallbackVideoSink(pump, disposeInnerOnDispose: true);
                    videoChains.Add(($"ndi_{nd.Id:N}", logo));
                    break;
                }
            }
        }

        var lead = videoChains.Count > 0 ? videoChains[0].Sink : null;

        if (!MediaPlayer.TryOpen(mediaPath, mpOpt, lead, disposeNegotiationLead: true, out var player, out errorMessage))
        {
            foreach (var ndi in ndiById.Values)
            {
                try { ndi.Dispose(); }
                catch { /* best effort */ }
            }

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
            pendingPlayback = new HaPlayPlaybackSession(player, av);

            foreach (var sink in videoChains.Select(t => t.Sink).OfType<LogoFallbackVideoSink>())
                pendingPlayback._logoSinks.Add(sink);

            foreach (var kv in ndiById)
                pendingPlayback._ndiByDefinitionId[kv.Key] = kv.Value;

            WireAudio(lines, player, pendingPlayback);
            session = pendingPlayback;
            pendingPlayback = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            pendingPlayback?.DisposePartialBeforePlayerDispose();
            player.Dispose();
            foreach (var ndi in ndiById.Values)
            {
                try { ndi.Dispose(); }
                catch { /* best effort */ }
            }

            return false;
        }
    }

    private static NDIOutput CreateNdiOutput(NdiOutputDefinition nd)
    {
        var mode = nd.StreamMode;
        var clockV = mode != NdiOutputStreamMode.AudioOnly;
        var clockA = mode != NdiOutputStreamMode.VideoOnly;
        var tc = mode == NdiOutputStreamMode.VideoAndAudio
            ? NDIVideoTimecodeMode.PresentationRelativeTicks
            : NDIVideoTimecodeMode.Synthesize;
        var groups = string.IsNullOrWhiteSpace(nd.Groups) ? null : nd.Groups;
        return new NDIOutput(nd.SourceName, groups, clockV, clockA, null, tc);
    }

    private static void WireAudio(List<OutputLineViewModel> lines, MediaPlayer player, HaPlayPlaybackSession playback)
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
                case NdiOutputDefinition nd when nd.StreamMode != NdiOutputStreamMode.VideoOnly:
                {
                    if (!playback._ndiByDefinitionId.TryGetValue(nd.Id, out var ndi))
                        continue;
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

    /// <summary>Combines video priming and (optionally) NDI audio warmup — call right before <see cref="AvRouter.Play"/>.</summary>
    /// <param name="holdFallbackShowsImage">When true, skip black-frame priming (the hold image already paints the output).</param>
    /// <param name="ndiAudioLeadIn">
    /// How much silence to push into NDI audio sinks first. Set to a non-zero value (e.g. 500 ms) when receivers
    /// may not be locked on yet (initial Play after Load, after Stop, on playlist advance). Use <see cref="TimeSpan.Zero"/>
    /// for in-playback transitions (seek, loop wrap) where extra silence would be audible at the new position.
    /// </param>
    public void PrepareOutputsBeforePlay(bool holdFallbackShowsImage, TimeSpan ndiAudioLeadIn)
    {
        PrimeVideoOutputsBeforePlay(holdFallbackShowsImage);
        if (ndiAudioLeadIn > TimeSpan.Zero)
            WarmupNdiBeforePlay(ndiAudioLeadIn);
    }

    /// <summary>
    /// Pre-rolls every NDI audio sink with <paramref name="leadIn"/> of silence and pushes extra primer frames
    /// through the video chains. Most NDI receivers buffer 200–500 ms before output and don't lock onto a source
    /// until they see the first audio packet, so the first chunks of real audio are lost to discovery / buffer fill.
    /// Sending silence ahead of <see cref="AvRouter.Play"/> announces the format, lets receivers fill their buffers,
    /// and avoids the missing-first-second symptom on real audio.
    /// </summary>
    /// <param name="leadIn">How much silence to send. ~600 ms covers most receiver buffer depths.</param>
    public void WarmupNdiBeforePlay(TimeSpan leadIn)
    {
        if (leadIn <= TimeSpan.Zero) return;

        foreach (var sink in _ndiAudioSinks)
        {
            var sampleRate = sink.Format.SampleRate;
            var channels = sink.Format.Channels;
            if (sampleRate <= 0 || channels <= 0) continue;

            // 10 ms silence chunks — small enough to land in receiver buffers smoothly, large enough that we
            // submit ~60 packets for 600 ms (cheap, but enough for discovery + buffer fill on slow receivers).
            var samplesPerChunk = Math.Max(1, sampleRate / 100);
            var totalChunks = Math.Max(1, (int)(leadIn.TotalMilliseconds / 10));
            var buffer = new float[samplesPerChunk * channels];

            for (var i = 0; i < totalChunks; i++)
            {
                try { sink.Submit(buffer); }
                catch { /* best effort — receiver will catch up on real audio */ }
            }
        }
    }

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
            Player.Dispose();
        }
        catch
        {
            /* best effort */
        }

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

        foreach (var ndi in _ndiByDefinitionId.Values)
        {
            try { ndi.Dispose(); }
            catch { /* best effort */ }
        }

        _ndiByDefinitionId.Clear();
        _ndiAudioSinks.Clear();
        _logoSinks.Clear();
        _portAudioOutputs.Clear();
    }
}
