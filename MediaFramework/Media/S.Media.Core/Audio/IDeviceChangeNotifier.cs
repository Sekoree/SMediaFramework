namespace S.Media.Core.Audio;

/// <summary>
/// Optional backend capability (D6 / OQ9): a backend that can <em>natively</em> signal device
/// add/remove/reroute implements this so the host doesn't have to poll. Backends without native
/// notifications (PortAudio, PortMIDI) omit it and a shared coalescing poller services them; either way
/// the host surfaces a single uniform "devices changed" signal. This is the dynamic-device seam - the
/// capability set itself stays frozen (D6); only the device <em>list</em> moves.
/// </summary>
/// <remarks>
/// Validated backends (OQ9): miniaudio (<c>ma_device_notification_type</c>, incl. reroute) and NDI
/// (<c>find_wait_for_sources</c>) raise native events; PortAudio (device list fixed until
/// <c>Pa_Terminate</c>/<c>Pa_Initialize</c>) and PortMIDI are polled.
/// </remarks>
public interface IDeviceChangeNotifier
{
    /// <summary>True when this backend raises <see cref="DevicesChanged"/> natively (no polling needed).</summary>
    bool SupportsDeviceChangeNotifications { get; }

    /// <summary>Raised after the backend's device list changes. Re-enumerate via the backend on this signal.</summary>
    event Action? DevicesChanged;
}
