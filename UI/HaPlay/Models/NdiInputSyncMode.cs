namespace HaPlay.Models;

/// <summary>How live NDI inputs are timed relative to audio outputs.</summary>
public enum NdiInputSyncMode
{
    /// <summary>NDI SDK frame sync + ingest clock — lip-synced A/V (default).</summary>
    NdiFrameSync,

    /// <summary>Minimal buffering; video shows the newest frame (NDI Monitor style).</summary>
    LowLatency,
}
