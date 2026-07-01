using System.Globalization;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Media.Audio.PortAudio;

/// <summary>
/// Opens <c>padev:</c> URIs as live PortAudio capture sources (D2) — a microphone / line-in / loopback input
/// device as a media source, so a cue or deck can play a live capture the same way it plays a file. The host
/// addresses a device by name: <c>padev://&lt;device-name&gt;</c> (or <c>padev:&lt;device-name&gt;</c>); an
/// empty name selects the system default input. Audio-only and live (no seek/duration); the capture streams at
/// the device's default sample rate and up to two channels, which the per-cue routing matrix then maps.
/// </summary>
/// <remarks>
/// This closes the NXT-06 cutover gap where the ShowSession cue path mapped <c>PortAudioInputPlaylistItem</c> to
/// <c>padev://</c> but no registry provider could open it (only NDI input had one). Registered by
/// <see cref="PortAudioModule"/> alongside the audio backend, reusing the same backend instance for device
/// enumeration + capture-stream creation.
/// </remarks>
internal sealed class PortAudioCaptureDecoderProvider : IMediaDecoderProvider
{
    internal const string Scheme = "padev";

    private readonly IAudioBackend _backend;

    public PortAudioCaptureDecoderProvider(IAudioBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public string Name => "PortAudioCapture";

    public double Probe(string uri, MediaKind kind) =>
        kind == MediaKind.Audio && IsPaDevScheme(uri) ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) =>
        throw new NotSupportedException($"'{uri}' is a PortAudio capture (audio-only) source; it has no video track.");

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options)
    {
        var deviceName = ParseDeviceName(uri);
        var devices = _backend.EnumerateInputDevices();
        var (deviceId, format) = ResolveDevice(deviceName, devices);
        return _backend.CreateInput(deviceId, format);
    }

    /// <summary>Live capture: a single audio track, no duration, not seekable. Overrides the default bridge so the
    /// result is flagged live (the bridge assumes a file).</summary>
    public async ValueTask<MediaOpenResult> OpenAsync(
        MediaOpenRequest request,
        IProgress<MediaPrepareProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Audio is null)
            throw new ArgumentException("a PortAudio capture source has only an audio track; request audio.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.Run(() =>
        {
            progress?.Report(new MediaPrepareProgress("opening", Message: request.Uri));
            var audio = OpenAudio(request.Uri, request.Audio);
            return new MediaOpenResult(
                Name, video: null, audio: audio, duration: TimeSpan.Zero, isLive: true, canSeek: false,
                disposeAsync: () =>
                {
                    (audio as IDisposable)?.Dispose();
                    return ValueTask.CompletedTask;
                });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lowercased scheme up to the first ':' — <c>true</c> only for an exact <c>padev</c> scheme.</summary>
    internal static bool IsPaDevScheme(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return false;
        var colon = uri.IndexOf(':');
        return colon > 0 && uri.AsSpan(0, colon).Equals(Scheme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The device name from <c>padev:&lt;name&gt;</c> / <c>padev://&lt;name&gt;</c> (URL-decoded; empty
    /// when none, meaning the system default input).</summary>
    internal static string ParseDeviceName(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var colon = uri.IndexOf(':');
        if (colon < 0)
            return string.Empty;
        var rest = uri[(colon + 1)..];
        if (rest.StartsWith("//", StringComparison.Ordinal))
            rest = rest[2..];
        return Uri.UnescapeDataString(rest).Trim();
    }

    /// <summary>Pure: resolves a requested device name against an enumerated input-device list to a backend device
    /// id + capture format. An empty name selects the system default (null id, 48 kHz stereo); a non-empty name
    /// must match a device (case-insensitive) or this throws so the cue faults visibly rather than silently
    /// capturing the wrong input. The format follows the device's default sample rate and up to two channels.</summary>
    internal static (string? DeviceId, AudioFormat Format) ResolveDevice(
        string deviceName, IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (string.IsNullOrEmpty(deviceName))
        {
            var def = devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
            return (null, FormatFor(def));
        }

        var match = devices.FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(
                        $"no PortAudio input device named '{deviceName}' (available: {DescribeDevices(devices)}).",
                        nameof(deviceName));
        return (match.Id, FormatFor(match));
    }

    private static AudioFormat FormatFor(AudioDeviceInfo? device)
    {
        var rate = device is { DefaultSampleRate: > 0 } ? (int)Math.Round(device.DefaultSampleRate) : 48_000;
        var channels = device is not null ? Math.Clamp(device.MaxChannels, 1, 2) : 2;
        return new AudioFormat(rate, channels);
    }

    private static string DescribeDevices(IReadOnlyList<AudioDeviceInfo> devices) =>
        devices.Count == 0 ? "none" : string.Join(", ", devices.Select(d => $"'{d.Name}'"));
}
