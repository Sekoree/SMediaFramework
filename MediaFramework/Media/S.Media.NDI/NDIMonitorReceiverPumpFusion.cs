using NDILib;

namespace S.Media.NDI;

/// <summary>
/// One correlated sample of NDI sender receiver feedback (NDI Monitor and other receivers) plus optional
/// host <c>VideoRouter</c> / <c>AudioRouter</c> pump counters - for HUDs and field diagnostics beside raw
/// <see cref="S.Media.Core.Video.VideoPlayer"/> / <c>PumpPressure</c> signals.
/// </summary>
/// <remarks>
/// The NDI SDK does not guarantee atomicity across <c>NDIlib_send_get_no_connections</c>,
/// <c>NDIlib_send_get_tally</c>, and <c>NDIlib_send_capture</c>; treat this struct as a best-effort snapshot
/// for operators correlating tally/program state with local drop counters. See <c>Doc/NDI-Terminology.md</c>.
/// </remarks>
public readonly record struct NDIMonitorReceiverPumpFusion(
    int ReceiverConnectionCount,
    NDITally ReceiverTally,
    bool TallyChangedInThisPoll,
    bool UpstreamMetadataFrameDrained,
    long NDIVideoPumpDropped,
    long NDIVideoPumpSubmitted,
    int NDIVideoPumpMaxQueueDepth,
    int NDIVideoPumpCurrentQueuedDepth,
    long NDIAudioPumpDropped)
{
    /// <summary>Aggregate tally reports on-program from at least one connected receiver.</summary>
    public bool OnProgram => ReceiverTally.OnProgram != 0;

    /// <summary>Aggregate tally reports on-preview from at least one connected receiver.</summary>
    public bool OnPreview => ReceiverTally.OnPreview != 0;
}