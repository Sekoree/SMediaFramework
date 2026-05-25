using System.Diagnostics.CodeAnalysis;
using HaPlay.Models;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI;

namespace HaPlay.Playback;

/// <summary>Resolves an NDI source and opens one combined <see cref="NDISource"/>.</summary>
internal static class NdiInputConnector
{
    public static bool TryConnectLive(
        NDIInputPlaylistItem item,
        [NotNullWhen(true)] out NDISource? receiver,
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

        NDIDiscoveredSource? match = null;
        foreach (var src in NDISource.Find(TimeSpan.FromSeconds(1)))
        {
            if (string.Equals(src.Name, item.SourceName, StringComparison.Ordinal))
            {
                match = src;
                break;
            }
        }

        if (match is null)
        {
            errorMessage = $"NDI source '{item.SourceName}' not currently visible on the network.";
            return false;
        }

        try
        {
            receiver = NDISource.Open(
                match.Value,
                new NDISourceOptions
                {
                    ReceiveAudio = wantAudio,
                    ReceiveVideo = wantVideo,
                    Bandwidth = ResolveBandwidth(wantAudio, wantVideo, item.LowBandwidth),
                });

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
    }

    private static NDIRecvBandwidth ResolveBandwidth(bool wantAudio, bool wantVideo, bool lowBandwidth)
    {
        if (wantAudio && !wantVideo)
            return NDIRecvBandwidth.AudioOnly;
        return lowBandwidth ? NDIRecvBandwidth.Lowest : NDIRecvBandwidth.Highest;
    }

    private static void CleanupReceiver(NDISource? receiver)
    {
        try { receiver?.Dispose(); } catch { /* best effort */ }
    }
}
