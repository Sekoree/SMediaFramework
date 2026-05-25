namespace S.Media.NDI;

/// <summary>
/// Host-facing hints derived from <see cref="NDIMonitorReceiverPumpFusion"/> (HUD / logging / optional policy wiring).
/// This is <strong>not</strong> automatic NDI pacing — product-level policy remains host-owned. See <c>Doc/NDI-Terminology.md</c>.
/// </summary>
public readonly record struct NDIFusionPlaybackHints(
    bool ReviewAudioPumpIfNonZeroDrops,
    bool ReviewVideoPumpIfNonZeroDrops,
    bool ReviewVideoQueueIfSustainedDepth)
{
    /// <summary>True when any individual review flag is set — convenience for HUD one-liners.</summary>
    public bool AnyReviewSuggested =>
        ReviewAudioPumpIfNonZeroDrops || ReviewVideoPumpIfNonZeroDrops || ReviewVideoQueueIfSustainedDepth;

    /// <summary>
    /// Derives hints from one fused poll. <paramref name="videoQueueDepthHintThreshold"/> is compared against both
    /// current and max video pump queue depth when at least one receiver is connected.
    /// </summary>
    public static NDIFusionPlaybackHints FromSnapshot(in NDIMonitorReceiverPumpFusion fusion, int videoQueueDepthHintThreshold = 6)
    {
        var audio = fusion.NDIAudioPumpDropped > 0;
        var video = fusion.NDIVideoPumpDropped > 0;
        var depth = fusion.ReceiverConnectionCount > 0
                    && fusion.NDIVideoPumpCurrentQueuedDepth >= videoQueueDepthHintThreshold
                    && fusion.NDIVideoPumpMaxQueueDepth >= videoQueueDepthHintThreshold;
        return new NDIFusionPlaybackHints(audio, video, depth);
    }
}
