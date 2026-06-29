using System.Diagnostics.CodeAnalysis;
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
                    // Operator's manual audio-latency override (null = framework default); set via the NDI
                    // input dialog's probe or override field.
                    AudioMinBufferedDuration = item.AudioMinBufferedDurationMs is { } ms
                        ? TimeSpan.FromMilliseconds(ms)
                        : null,
                });

            // Wait for the enabled stream(s) to deliver a format up front (NDISource encapsulates the
            // poll — see P2-15) so the format is known before the router/session wires the source.
            if (!receiver.WaitForStreams(TimeSpan.FromSeconds(2)))
            {
                var missing = wantAudio && !receiver.IsAudioConnected ? "audio" : "video";
                errorMessage = $"NDI source '{item.SourceName}' resolved but delivered no {missing} in 2 s.";
                CleanupReceiver(receiver);
                receiver = null;
                return false;
            }

            if (wantAudio && !receiver.TryGetAudioFormat(out audioFormat))
            {
                errorMessage = $"NDI source '{item.SourceName}' did not expose its audio format.";
                CleanupReceiver(receiver);
                receiver = null;
                return false;
            }
            if (wantVideo && !receiver.TryGetVideoFormat(out videoFormat))
            {
                errorMessage = $"NDI source '{item.SourceName}' did not expose its video format.";
                CleanupReceiver(receiver);
                receiver = null;
                return false;
            }

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
        if (lowBandwidth)
            return NDIRecvBandwidth.Lowest;

        return NDIReceiveBandwidthPolicy.Resolve(wantAudio, wantVideo);
    }

    private static void CleanupReceiver(NDISource? receiver)
    {
        try { receiver?.Dispose(); } catch { /* best effort */ }
    }
}
