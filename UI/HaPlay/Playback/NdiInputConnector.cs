using System.Diagnostics.CodeAnalysis;
using HaPlay.Models;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI;

namespace HaPlay.Playback;

/// <summary>Resolves an NDI source and opens one combined <see cref="NDILiveReceiver"/> (§6.11).</summary>
internal static class NdiInputConnector
{
    public static bool TryConnectLive(
        NDIInputPlaylistItem item,
        [NotNullWhen(true)] out NDILiveReceiver? receiver,
        out AudioFormat audioFormat,
        out VideoFormat videoFormat,
        out string? errorMessage)
    {
        receiver = null;
        audioFormat = default;
        videoFormat = default;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(item.SourceName))
        {
            errorMessage = "NDI input has no source name.";
            return false;
        }

        var wantAudio = !item.VideoOnly;
        var wantVideo = !item.AudioOnly;
        if (!wantAudio && !wantVideo)
        {
            errorMessage = "NDI input has neither audio nor video enabled.";
            return false;
        }

        NDIFinder? finder = null;
        try
        {
            var rc = NDIFinder.Create(out finder, new NDIFinderSettings { ShowLocalSources = true });
            if (rc != 0 || finder is null)
            {
                errorMessage = $"NDI finder unavailable (rc={rc}).";
                return false;
            }

            NDIDiscoveredSource? match = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                finder.WaitForSources(250);
                foreach (var src in finder.GetCurrentSources())
                {
                    if (string.Equals(src.Name, item.SourceName, StringComparison.Ordinal))
                    {
                        match = src;
                        break;
                    }
                }

                if (match is not null)
                    break;
            }

            if (match is null)
            {
                errorMessage = $"NDI source '{item.SourceName}' not currently visible on the network.";
                return false;
            }

            receiver = new NDILiveReceiver(
                match.Value,
                receiveAudio: wantAudio,
                receiveVideo: wantVideo,
                bandwidth: ResolveBandwidth(wantAudio, wantVideo, item.LowBandwidth));

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                var audioReady = !wantAudio || receiver.IsAudioConnected;
                var videoReady = !wantVideo || receiver.IsVideoConnected;
                if (audioReady && videoReady)
                    break;
                Thread.Sleep(50);
            }

            if (wantAudio && !receiver.IsAudioConnected)
            {
                errorMessage = $"NDI source '{item.SourceName}' resolved but delivered no audio in 2 s.";
                CleanupReceiver(receiver);
                receiver = null;
                return false;
            }

            if (wantVideo && !receiver.IsVideoConnected)
            {
                errorMessage = $"NDI source '{item.SourceName}' resolved but delivered no video in 2 s.";
                CleanupReceiver(receiver);
                receiver = null;
                return false;
            }

            if (wantAudio)
                audioFormat = receiver.AudioFormat;
            if (wantVideo)
                videoFormat = receiver.VideoFormat;

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            CleanupReceiver(receiver);
            receiver = null;
            return false;
        }
        finally
        {
            try { finder?.Dispose(); } catch { /* best effort */ }
        }
    }

    private static NDIRecvBandwidth ResolveBandwidth(bool wantAudio, bool wantVideo, bool lowBandwidth)
    {
        if (wantAudio && !wantVideo)
            return NDIRecvBandwidth.AudioOnly;
        return lowBandwidth ? NDIRecvBandwidth.Lowest : NDIRecvBandwidth.Highest;
    }

    private static void CleanupReceiver(NDILiveReceiver? receiver)
    {
        try { receiver?.Dispose(); } catch { /* best effort */ }
    }
}
