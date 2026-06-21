namespace S.Media.Core.Audio;

/// <summary>
/// One discoverable audio device exposed by an <see cref="IAudioBackend"/>. <see cref="Id"/> is an opaque,
/// backend-specific handle (stable within a process) that you pass back to
/// <see cref="IAudioBackend.CreateOutput"/> / <see cref="IAudioBackend.CreateInput"/>; pass <c>null</c>
/// there for the system default device.
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    int MaxChannels,
    double DefaultSampleRate,
    bool IsDefault);

/// <summary>
/// Backend-neutral knobs for opening a device. Leave a value <c>null</c>/<c>0</c> to take the backend's
/// own default.
/// </summary>
/// <param name="SuggestedLatencySeconds">Requested device latency (the backend may round to what it supports).</param>
/// <param name="FramesPerBuffer">Preferred callback buffer size in frames; <c>0</c> = backend default.</param>
/// <param name="RingCapacityFrames">Producer/consumer ring capacity in frames; <c>0</c> = backend default.</param>
public sealed record AudioBackendOptions(
    double? SuggestedLatencySeconds = null,
    int FramesPerBuffer = 0,
    int RingCapacityFrames = 0);

/// <summary>
/// A pluggable audio host backend (PortAudio, miniaudio, …): device discovery plus opening
/// <see cref="IAudioOutput"/> / <see cref="IAudioSource"/> on a device. This is the <strong>only</strong>
/// layer a new backend must implement — the frame-level graph (<see cref="AudioRouter"/>) is already
/// backend-neutral, seeing only <see cref="IAudioOutput"/> / <see cref="IAudioSource"/> (and their optional
/// capability interfaces like <see cref="IClockedOutput"/>), never a concrete backend type. A new backend is
/// therefore a peer of PortAudio, not a translation of it. Register implementations with
/// <see cref="AudioBackends"/>.
/// </summary>
public interface IAudioBackend
{
    /// <summary>Stable backend name, e.g. <c>"PortAudio"</c> / <c>"miniaudio"</c>. Used to select a backend.</summary>
    string Name { get; }

    /// <summary>The currently available output (playback) devices.</summary>
    IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices();

    /// <summary>The currently available input (capture) devices.</summary>
    IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices();

    /// <summary>
    /// Opens an output on the device with <paramref name="deviceId"/> (an <see cref="AudioDeviceInfo.Id"/>),
    /// or the system default when <paramref name="deviceId"/> is <c>null</c>/empty. The returned output is
    /// ready to register on an <see cref="AudioRouter"/>; it may also implement <see cref="IClockedOutput"/> /
    /// <see cref="IPlaybackClock"/> etc. The caller owns it and disposes it.
    /// </summary>
    IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null);

    /// <summary>
    /// Opens a capture input on the device with <paramref name="deviceId"/>, or the system default when
    /// <paramref name="deviceId"/> is <c>null</c>/empty. The caller owns it and disposes it.
    /// </summary>
    IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null);
}
