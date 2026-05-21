using System.Diagnostics.CodeAnalysis;
using HaPlay.Models;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;

namespace HaPlay.Playback;

/// <summary>Resolves an NDI source and opens <see cref="NDIAudioReceiver"/> / <see cref="NDIVideoReceiver"/> (§6.11).</summary>
internal static class NdiInputConnector
{
    public static bool TryConnectAudio(
        NDIInputPlaylistItem item,
        [NotNullWhen(true)] out NDIAudioReceiver? receiver,
        out AudioFormat format,
        out string? errorMessage) =>
        TryConnectReceiver(
            item,
            rejectWhen: static i => i.VideoOnly,
            rejectMessage: "NDI input is video-only.",
            waitForAudio: true,
            waitForVideo: false,
            openAudio: true,
            openVideo: false,
            out receiver,
            out _,
            out format,
            out _,
            out errorMessage);

    public static bool TryConnectVideo(
        NDIInputPlaylistItem item,
        [NotNullWhen(true)] out NDIVideoReceiver? receiver,
        out string? errorMessage) =>
        TryConnectReceiver(
            item,
            rejectWhen: static i => i.AudioOnly,
            rejectMessage: "NDI input is audio-only.",
            waitForAudio: false,
            waitForVideo: true,
            openAudio: false,
            openVideo: true,
            out _,
            out receiver,
            out _,
            out _,
            out errorMessage);

    private delegate bool ItemReject(NDIInputPlaylistItem item);

    private static bool TryConnectReceiver(
        NDIInputPlaylistItem item,
        ItemReject rejectWhen,
        string rejectMessage,
        bool waitForAudio,
        bool waitForVideo,
        bool openAudio,
        bool openVideo,
        out NDIAudioReceiver? audioReceiver,
        out NDIVideoReceiver? videoReceiver,
        out AudioFormat audioFormat,
        out VideoFormat videoFormat,
        out string? errorMessage)
    {
        audioReceiver = null;
        videoReceiver = null;
        audioFormat = default;
        videoFormat = default;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(item.SourceName))
        {
            errorMessage = "NDI input has no source name.";
            return false;
        }

        if (rejectWhen(item))
        {
            errorMessage = rejectMessage;
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

            if (openAudio)
                audioReceiver = new NDIAudioReceiver(match.Value);
            if (openVideo)
                videoReceiver = new NDIVideoReceiver(match.Value);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                var audioReady = !waitForAudio || audioReceiver is { IsConnected: true };
                var videoReady = !waitForVideo || videoReceiver is { IsConnected: true };
                if (audioReady && videoReady)
                    break;
                Thread.Sleep(50);
            }

            if (waitForAudio && audioReceiver is not { IsConnected: true })
            {
                errorMessage = $"NDI source '{item.SourceName}' resolved but delivered no audio in 2 s.";
                CleanupReceivers(audioReceiver, videoReceiver);
                return false;
            }

            if (waitForVideo && videoReceiver is not { IsConnected: true })
            {
                errorMessage = $"NDI source '{item.SourceName}' resolved but delivered no video in 2 s.";
                CleanupReceivers(audioReceiver, videoReceiver);
                return false;
            }

            if (audioReceiver is not null)
                audioFormat = audioReceiver.Format;
            if (videoReceiver is not null)
                videoFormat = videoReceiver.Format;

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            CleanupReceivers(audioReceiver, videoReceiver);
            return false;
        }
        finally
        {
            try { finder?.Dispose(); } catch { /* best effort */ }
        }
    }

    private static void CleanupReceivers(NDIAudioReceiver? audio, NDIVideoReceiver? video)
    {
        try { audio?.Dispose(); } catch { /* best effort */ }
        try { video?.Dispose(); } catch { /* best effort */ }
    }
}
