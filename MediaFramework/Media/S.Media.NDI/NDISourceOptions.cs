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

    /// <summary>
    /// Present received video at the sender's <strong>absolute</strong> egress timecode instead of the default
    /// first-frame-relative timeline. For multi-receiver wall sync: every receiver of one sender resolves a
    /// frame to the same time, so - driven by a shared/synced reference clock - they present in lock-step.
    /// Default <c>false</c> (smooth single-receiver playback rebased to play start). Cross-receiver alignment
    /// also requires the receivers' clocks to share a reference (PTP / genlock); the framework supplies the
    /// absolute timeline, the time reference is a deployment concern. See <c>Doc/HaPlay-MultiOutput-Sync.md</c>.
    /// </summary>
    public bool PresentVideoByAbsoluteTimecode { get; init; }
}
