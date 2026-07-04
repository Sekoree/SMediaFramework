using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Players;

/// <summary>One-call operational snapshot from <see cref="MediaPlayer.GetMetrics"/>.</summary>
public sealed record MediaPlayerMetrics(
    MediaClockMetricsSnapshot Clock,
    VideoPlayerMetricsSnapshot? Video,
    AudioRouterMetricsSnapshot? AudioRouter,
    IReadOnlyList<VideoOutputPumpMetricsEntry> VideoOutputs,
    IReadOnlyList<AudioOutputPumpMetricsEntry> AudioOutputs,
    PortAudioMetricsSnapshot? PortAudio,
    NDIIngestMetricsSnapshot? NDI);

public sealed record MediaClockMetricsSnapshot(
    TimeSpan CurrentPosition,
    string MasterTypeName);

public sealed record VideoPlayerMetricsSnapshot(
    long DecodedCount,
    long DisplayedCount,
    long DroppedLate,
    long DroppedDrain);

public sealed record AudioRouterMetricsSnapshot(
    long ChunksProduced,
    long TotalEnqueued,
    long TotalProcessed,
    long TotalDropped,
    int OutputCount);

public sealed record VideoOutputPumpMetricsEntry(
    string OutputId,
    VideoOutputPumpMetrics Metrics);

public sealed record AudioOutputPumpMetricsEntry(
    string OutputId,
    AudioRouter.OutputPumpStats Stats);

public sealed record PortAudioMetricsSnapshot(
    long PlayedSamples,
    long UnderrunSamples,
    long DroppedSamples);

public sealed record NDIIngestMetricsSnapshot(
    long AudioOverflowFloats,
    long VideoOverflowFrames);
