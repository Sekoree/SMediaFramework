using System.Diagnostics.CodeAnalysis;
using HaPlay.Models;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI.Audio;
using S.Media.NDI.Clock;
using S.Media.NDI.Video;

namespace HaPlay.Playback;

/// <summary>Resolves an NDI source and opens <see cref="NDIAudioReceiver"/> / <see cref="NDIVideoReceiver"/> (§6.11).</summary>
internal static class NdiInputConnector
{
    private static readonly TimeSpan PushRingNdiClock = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PushRingLowLatency = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan PushReadLatencyNdiClock = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan PushReadLatencyLowLatency = TimeSpan.FromMilliseconds(20);
    private const int PushVideoQueueNdiClock = 6;
    private const int PushVideoQueueLowLatency = 2;

    public static bool TryConnectAudio(
        NDIInputPlaylistItem item,
        NdiInputSyncMode syncMode,
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
            syncMode,
            out receiver,
            out _,
            out format,
            out _,
            out errorMessage);

    public static bool TryConnectVideo(
        NDIInputPlaylistItem item,
        NdiInputSyncMode syncMode,
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
            syncMode,
            out _,
            out receiver,
            out _,
            out _,
            out errorMessage);

    /// <summary>
    /// Opens audio and/or video on one NDI connection with a single <see cref="NDIIngestPlaybackClock"/>.
    /// Both sync modes use push receivers; timing is applied at playout (PortAudio master + buffer trim).
    /// </summary>
    public static bool TryConnect(
        NDIInputPlaylistItem item,
        NdiInputSyncMode syncMode,
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

        if (!openAudio && !openVideo)
        {
            errorMessage = "At least one of audio or video must be requested.";
            return false;
        }

        if (openAudio && item.VideoOnly)
        {
            errorMessage = "NDI input is video-only.";
            return false;
        }

        if (openVideo && item.AudioOnly)
        {
            errorMessage = "NDI input is audio-only.";
            return false;
        }

        return TryConnectReceiver(
            item,
            rejectWhen: static _ => false,
            rejectMessage: "",
            waitForAudio: openAudio,
            waitForVideo: openVideo,
            openAudio: openAudio,
            openVideo: openVideo,
            syncMode,
            out audioReceiver,
            out videoReceiver,
            out audioFormat,
            out videoFormat,
            out errorMessage);
    }

    private delegate bool ItemReject(NDIInputPlaylistItem item);

    private static bool TryConnectReceiver(
        NDIInputPlaylistItem item,
        ItemReject rejectWhen,
        string rejectMessage,
        bool waitForAudio,
        bool waitForVideo,
        bool openAudio,
        bool openVideo,
        NdiInputSyncMode syncMode,
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

        if (!TryResolveSource(item.SourceName, out var match, out errorMessage))
            return false;

        var lowLatency = syncMode == NdiInputSyncMode.LowLatency;
        var ring = lowLatency ? PushRingLowLatency : PushRingNdiClock;
        var readLatency = lowLatency ? PushReadLatencyLowLatency : PushReadLatencyNdiClock;
        var videoQueue = lowLatency ? PushVideoQueueLowLatency : PushVideoQueueNdiClock;

        try
        {
            NDIIngestPlaybackClock? ingestClock = openAudio || openVideo ? new NDIIngestPlaybackClock() : null;
            if (openAudio)
            {
                audioReceiver = new NDIAudioReceiver(
                    match.Value,
                    ingestClock: ingestClock,
                    ringCapacityDuration: ring,
                    maxReadLatency: readLatency);
            }

            if (openVideo)
            {
                videoReceiver = new NDIVideoReceiver(
                    match.Value,
                    ingestClock: ingestClock,
                    maxQueuedFrames: videoQueue,
                    wallClockPresentation: ingestClock is not null);
            }

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
    }

    private static bool TryResolveSource(string sourceName, out NDIDiscoveredSource? match, out string? errorMessage)
    {
        match = null;
        errorMessage = null;
        NDIFinder? finder = null;
        try
        {
            var rc = NDIFinder.Create(out finder, new NDIFinderSettings { ShowLocalSources = true });
            if (rc != 0 || finder is null)
            {
                errorMessage = $"NDI finder unavailable (rc={rc}).";
                return false;
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                finder.WaitForSources(250);
                foreach (var src in finder.GetCurrentSources())
                {
                    if (string.Equals(src.Name, sourceName, StringComparison.Ordinal))
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
                errorMessage = $"NDI source '{sourceName}' not currently visible on the network.";
                return false;
            }

            return true;
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
