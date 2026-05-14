using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.Playback;
using S.Media.PortAudio;
using S.Media.SDL3;

namespace VideoPlaybackSmoke;

/// <summary>Interval window for estimating displayed FPS between HUD ticks.</summary>
public readonly record struct SmokeHudTick(
    double IntervalSeconds,
    long DisplayedCountStart,
    long DisplayedCountEnd);

/// <summary>Collects decoder/player/router counters for <see cref="PlaybackHud.FormatLine"/>.</summary>
public static class SmokeHud
{
    public static string FormatClock(TimeSpan t) => PlaybackHud.FormatClock(t);

    public static PlaybackHudSnapshot Collect(
        in SmokeHudTick tick,
        IMediaClock playClock,
        IVideoSource videoSource,
        VideoPlayer videoPlayer,
        MediaContainerDecoder media,
        IVideoSink windowPresentationSink,
        MediaContainerPlaybackHost? audioHost,
        string? ndiAudioSinkId,
        VideoRouter videoRouter,
        string? ndiVideoOutputId,
        NDIOutput? ndi)
    {
        var dShown = tick.DisplayedCountEnd - tick.DisplayedCountStart;
        var vFps = tick.IntervalSeconds > 1e-6 ? dShown / tick.IntervalSeconds : 0.0;
        var nomFps = videoSource.Format.FrameRate.ToDouble();
        var nomFpsStr = nomFps > 0 && !double.IsNaN(nomFps) ? $"{nomFps:0.##}Hz" : "?Hz";

        var aHeard = audioHost is not null
            ? TimeSpan.FromSeconds(audioHost.MainOutput.PlayedSamples / (double)audioHost.MainOutput.Format.SampleRate)
            : TimeSpan.Zero;
        var aDeckDec = (media.Audio as ISeekableSource)?.Position ?? TimeSpan.Zero;

        long pumpDr = 0, paUnd = 0, paDr = 0, ndiDr = 0;
        if (audioHost is not null)
        {
            pumpDr = audioHost.Player.Router.GetPumpStats(audioHost.PrimarySinkId).Dropped;
            paUnd = audioHost.MainOutput.UnderrunSamples;
            paDr = audioHost.MainOutput.DroppedSamples;
            if (ndiAudioSinkId is not null)
                ndiDr = audioHost.Player.Router.GetPumpStats(ndiAudioSinkId).Dropped;
        }

        long ndiVidDr = 0;
        int ndiVidQ = 0;
        long ndiVidSub = 0;
        int ndiVidMaxQ = 0;
        if (ndiVideoOutputId is not null
            && videoRouter.TryGetVideoSinkPumpMetrics(ndiVideoOutputId, out var vpMetrics))
        {
            ndiVidDr = vpMetrics.DroppedFrames;
            ndiVidQ = vpMetrics.CurrentQueuedDepth;
            ndiVidSub = vpMetrics.SubmittedFrames;
            ndiVidMaxQ = vpMetrics.MaxQueueDepth;
        }

        var ndiMonTail = "";
        if (ndi is not null)
        {
            var fus = ndi.TryPollMonitorReceiverPumpFusion(0, false, ndiVidDr, ndiVidSub, ndiVidMaxQ, ndiVidQ,
                ndiDr);
            ndiMonTail =
                $"  ndiRx{fus.ReceiverConnectionCount} P{fus.ReceiverTally.OnProgram}V{fus.ReceiverTally.OnPreview} tallyΔ{(fus.TallyChangedInThisPoll ? 1 : 0)}";
        }

        var vPts = (videoSource as ISeekableSource)?.Position ?? TimeSpan.Zero;

        var glDroppedNewer = windowPresentationSink is SDL3GLVideoSink gl ? gl.DroppedNewer : 0L;

        return new PlaybackHudSnapshot(
            playClock.CurrentPosition,
            vPts,
            aHeard,
            aDeckDec,
            videoPlayer.DisplayedCount,
            videoPlayer.DecodedCount,
            vFps,
            nomFpsStr,
            videoPlayer.DroppedLate,
            videoPlayer.DroppedDrain,
            glDroppedNewer,
            ndiVidDr,
            ndiVidQ,
            paUnd,
            paDr,
            pumpDr,
            ndiDr,
            ndiMonTail);
    }
}
