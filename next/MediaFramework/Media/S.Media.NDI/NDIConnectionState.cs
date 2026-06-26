namespace S.Media.NDI;

/// <summary>High-level receive connection state for <see cref="NDISource"/>.</summary>
public enum NDIConnectionState
{
    Opening,
    Connected,
    Disconnected,
    /// <summary>
    /// Dispose requested capture shutdown, but the capture thread did not exit within the join cap.
    /// Native receiver/runtime state is intentionally retained to avoid freeing resources under a live thread.
    /// </summary>
    Stuck,
    Disposed,
}
