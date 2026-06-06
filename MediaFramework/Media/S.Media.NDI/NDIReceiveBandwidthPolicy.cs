using NDILib;

namespace S.Media.NDI;

/// <summary>
/// Chooses an NDI receiver bandwidth mode from enabled stream types and an optional explicit override.
/// </summary>
public static class NDIReceiveBandwidthPolicy
{
    /// <summary>
    /// When <paramref name="configured"/> is not <see cref="NDIRecvBandwidth.Highest"/>, returns it unchanged.
    /// Otherwise picks a sensible default: audio-only receivers use <see cref="NDIRecvBandwidth.AudioOnly"/>,
    /// video-only receivers use <see cref="NDIRecvBandwidth.Lowest"/>, and A/V receivers stay on highest.
    /// </summary>
    public static NDIRecvBandwidth Resolve(
        bool receiveAudio,
        bool receiveVideo,
        NDIRecvBandwidth configured = NDIRecvBandwidth.Highest)
    {
        if (configured != NDIRecvBandwidth.Highest)
            return configured;

        if (receiveAudio && !receiveVideo)
            return NDIRecvBandwidth.AudioOnly;

        if (receiveVideo && !receiveAudio)
            return NDIRecvBandwidth.Lowest;

        return NDIRecvBandwidth.Highest;
    }
}
