using NDILib;
using S.Media.NDI.Clock;

namespace S.Media.NDI;

/// <summary>Options for <see cref="NDISource.Open"/>.</summary>
public sealed class NDISourceOptions
{
    public static NDISourceOptions Default { get; } = new();

    public bool ReceiveAudio { get; init; } = true;

    public bool ReceiveVideo { get; init; } = true;

    public string? ReceiverName { get; init; }

    public TimeSpan AudioRingCapacityDuration { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan? AudioMinBufferedDuration { get; init; }

    public int MaxQueuedVideoFrames { get; init; } = 8;

    public NDIRecvBandwidth Bandwidth { get; init; } = NDIRecvBandwidth.Highest;

    public NDIRecvColorFormat ColorFormat { get; init; } = NDIRecvColorFormat.BgrxBgra;

    public NDIIngestPlaybackClock? IngestClock { get; init; }
}
