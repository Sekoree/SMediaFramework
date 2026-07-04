using NDILib;

namespace S.Media.NDI;

/// <summary>
/// Chooses an NDI receiver bandwidth mode from enabled stream types and an optional explicit override.
/// </summary>
public static class NDIReceiveBandwidthPolicy
{
    /// <summary>
    /// When <paramref name="configured"/> is not <see cref="NDIRecvBandwidth.Highest"/>, returns it unchanged.
    /// Otherwise picks a sensible default: audio-only receivers use <see cref="NDIRecvBandwidth.AudioOnly"/>
    /// (no video bandwidth at all); everything that receives video — <em>including video-only</em> — gets full
    /// resolution. A low-res proxy (<see cref="NDIRecvBandwidth.Lowest"/>) is opt-in via an explicit
    /// <paramref name="configured"/> value: a video-only open in a playback framework means "play this source
    /// without its audio" (full quality), not "give me a thumbnail". Defaulting video-only to the proxy made
    /// NDI playback receive a 640×360 stream and upscale it.
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

        return NDIRecvBandwidth.Highest;
    }
}
