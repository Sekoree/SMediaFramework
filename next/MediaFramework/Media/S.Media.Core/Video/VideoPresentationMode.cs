namespace S.Media.Core.Video;

/// <summary>How <see cref="VideoPlayer"/> selects frames from its presentation queue.</summary>
public enum VideoPresentationMode
{
    /// <summary>PTS vs playhead scheduling (file playback and NDI clock sync).</summary>
    Scheduled,

    /// <summary>Always show the newest decoded frame (NDI Monitor style, minimal latency).</summary>
    LatestOnTick,
}
