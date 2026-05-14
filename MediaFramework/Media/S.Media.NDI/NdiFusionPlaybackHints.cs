namespace S.Media.NDI;

/// <summary>
/// Host-facing hints derived from <see cref="NdiMonitorReceiverPumpFusion"/> (HUD / logging / optional policy wiring).
/// This is <strong>not</strong> automatic NDI pacing — product-level policy remains host-owned (**§Tier F** row **26** **Open** tail).
/// </summary>
public readonly record struct NdiFusionPlaybackHints(
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
    public static NdiFusionPlaybackHints FromSnapshot(in NdiMonitorReceiverPumpFusion fusion, int videoQueueDepthHintThreshold = 6)
    {
        var audio = fusion.NdiAudioPumpDropped > 0;
        var video = fusion.NdiVideoPumpDropped > 0;
        var depth = fusion.ReceiverConnectionCount > 0
                    && fusion.NdiVideoPumpCurrentQueuedDepth >= videoQueueDepthHintThreshold
                    && fusion.NdiVideoPumpMaxQueueDepth >= videoQueueDepthHintThreshold;
        return new NdiFusionPlaybackHints(audio, video, depth);
    }
}
