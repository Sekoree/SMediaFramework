namespace S.Media.NDI;

/// <summary>High-level receive connection state for <see cref="NDISource"/>.</summary>
public enum NDIConnectionState
{
    Opening,
    Connected,
    Disconnected,
    Disposed,
}
