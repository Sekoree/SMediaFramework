using S.Media.Core.Audio;

namespace S.Media.Session;

/// <summary>
/// SESSION-01: the short-lived audio-output device enumeration cache extracted from <see cref="ShowSession"/>.
/// Enumerating backend output devices is expensive (PortAudio re-scans the host APIs; ALSA setup makes it worse)
/// and the clip-spec builder runs on every fire / warm / voice - so a burst of those must enumerate once, not
/// once per call. The cache is a stable invariant on its own: given a backend it returns the device list for up
/// to <see cref="Ttl"/> and resolves a device's nominal sample rate. It is thread-safe because the fire path
/// builds specs OFF the session dispatcher (NXT-24).
/// </summary>
internal sealed class AudioOutputDeviceCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly IAudioBackend? _backend;
    private readonly object _gate = new();
    private IReadOnlyList<AudioDeviceInfo>? _cached;
    private long _cachedAtMs;

    public AudioOutputDeviceCache(IAudioBackend? backend) => _backend = backend;

    /// <summary>The backend's output devices, cached for <see cref="Ttl"/>. Device hot-plug is still picked up on
    /// the next refresh after the TTL. Returns an empty list when there is no backend.</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices()
    {
        if (_backend is null)
            return [];

        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_cached is { } cached && now - _cachedAtMs < (long)Ttl.TotalMilliseconds)
                return cached;
        }

        var devices = _backend.EnumerateOutputDevices();
        lock (_gate)
        {
            _cached = devices;
            _cachedAtMs = now;
        }

        return devices;
    }

    /// <summary>Returns the hardware/backend nominal rate for a device, or null when there is no backend or no
    /// usable rate. JACK devices expose their fixed graph rate here; opening PortAudio at the media's source rate
    /// would fail for 44.1 kHz media on a 48 kHz JACK graph.</summary>
    public int? ResolveBackendSampleRate(string? deviceId)
    {
        if (_backend is null)
            return null;

        var devices = EnumerateOutputDevices();
        var device = !string.IsNullOrWhiteSpace(deviceId)
            ? devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal))
            : devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
        return device is { DefaultSampleRate: > 0 }
            ? checked((int)Math.Round(device.DefaultSampleRate))
            : null;
    }
}
