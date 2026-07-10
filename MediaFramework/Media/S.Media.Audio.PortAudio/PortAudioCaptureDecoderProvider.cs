using System.Globalization;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Media.Audio.PortAudio;

/// <summary>
/// Opens <c>padev:</c> URIs as live PortAudio capture sources (D2) - a microphone / line-in / loopback input
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
        var descriptor = ParseDescriptor(uri);
        var (deviceId, format) = _backend is PortAudioBackend
            ? ResolveCatalogDevice(
                descriptor,
                PortAudioDeviceCatalog.EnumerateInputDevices(),
                PortAudioDeviceCatalog.EnumerateHostApis())
            : ResolveDevice(descriptor, _backend.EnumerateInputDevices());
        return _backend.CreateInput(deviceId, format,
            new AudioBackendOptions(SuggestedLatencySeconds: descriptor.SuggestedLatencySeconds));
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

    /// <summary>Lowercased scheme up to the first ':' - <c>true</c> only for an exact <c>padev</c> scheme.</summary>
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
        => ParseDescriptor(uri).DeviceName;

    internal sealed record CaptureDescriptor(
        string DeviceName,
        string? HostApiName = null,
        int? HostApiIndex = null,
        int? GlobalDeviceIndex = null,
        int? Channels = null,
        int? SampleRate = null,
        double? SuggestedLatencySeconds = null);

    internal static CaptureDescriptor ParseDescriptor(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var colon = uri.IndexOf(':');
        if (colon < 0)
            return new CaptureDescriptor(string.Empty);
        var rest = uri[(colon + 1)..];
        if (rest.StartsWith("//", StringComparison.Ordinal))
            rest = rest[2..];
        var queryAt = rest.IndexOf('?');
        var name = Uri.UnescapeDataString(queryAt >= 0 ? rest[..queryAt] : rest).Trim();
        var values = queryAt >= 0 ? ParseQuery(rest[(queryAt + 1)..]) : new Dictionary<string, string>();
        return new CaptureDescriptor(
            name,
            Text(values, "hostApiName"),
            Int(values, "hostApiIndex"),
            Int(values, "globalDeviceIndex"),
            Int(values, "channels", min: 1),
            Int(values, "sampleRate", min: 8000, max: 192000),
            Double(values, "latency", min: 0));
    }

    /// <summary>Pure: resolves a requested device name against an enumerated input-device list to a backend device
    /// id + capture format. An empty name selects the system default (null id, 48 kHz stereo); a non-empty name
    /// must match a device (case-insensitive) or this throws so the cue faults visibly rather than silently
    /// capturing the wrong input. The format follows the device's default sample rate and up to two channels.</summary>
    internal static (string? DeviceId, AudioFormat Format) ResolveDevice(
        string deviceName, IReadOnlyList<AudioDeviceInfo> devices)
        => ResolveDevice(new CaptureDescriptor(deviceName), devices);

    internal static (string? DeviceId, AudioFormat Format) ResolveDevice(
        CaptureDescriptor descriptor, IReadOnlyList<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (string.IsNullOrEmpty(descriptor.DeviceName))
        {
            var def = devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
            return (null, FormatFor(def, descriptor));
        }

        var matchingNames = devices.Where(d =>
            string.Equals(d.Name, descriptor.DeviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var globalId = descriptor.GlobalDeviceIndex?.ToString(CultureInfo.InvariantCulture);
        var match = (globalId is not null
                         ? matchingNames.FirstOrDefault(d => string.Equals(d.Id, globalId, StringComparison.Ordinal))
                         : null)
                    ?? matchingNames.FirstOrDefault()
                    ?? (globalId is not null
                        ? devices.FirstOrDefault(d => string.Equals(d.Id, globalId, StringComparison.Ordinal))
                        : null)
                    ?? throw new ArgumentException(
                        $"no PortAudio input device named '{descriptor.DeviceName}' (available: {DescribeDevices(devices)}).",
                        nameof(descriptor));
        return (match.Id, FormatFor(match, descriptor));
    }

    /// <summary>PortAudio-specific resolver: host API name is the stable discriminator when several APIs expose
    /// the same device name; the saved global index is only a final fallback because it can change across boots.</summary>
    internal static (string? DeviceId, AudioFormat Format) ResolveCatalogDevice(
        CaptureDescriptor descriptor,
        IReadOnlyList<PortAudioInputDeviceEntry> devices,
        IReadOnlyList<PortAudioHostApiEntry> hostApis)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(hostApis);
        var hostByName = !string.IsNullOrWhiteSpace(descriptor.HostApiName)
            ? hostApis.Where(h => string.Equals(h.Name, descriptor.HostApiName, StringComparison.OrdinalIgnoreCase))
                .Cast<PortAudioHostApiEntry?>().FirstOrDefault()
            : null;
        var hostIndex = hostByName?.Index ?? descriptor.HostApiIndex;

        PortAudioInputDeviceEntry? match = devices.Where(d =>
                string.Equals(d.Name, descriptor.DeviceName, StringComparison.OrdinalIgnoreCase)
                && (hostIndex is null || d.HostApiIndex == hostIndex))
            .Cast<PortAudioInputDeviceEntry?>().FirstOrDefault();
        if (match is null && descriptor.GlobalDeviceIndex is { } global)
            match = devices.Where(d => d.GlobalDeviceIndex == global)
                .Cast<PortAudioInputDeviceEntry?>().FirstOrDefault();
        if (match is null && string.IsNullOrEmpty(descriptor.DeviceName))
            match = devices.Where(d => d.IsDefault).Cast<PortAudioInputDeviceEntry?>().FirstOrDefault()
                    ?? devices.Cast<PortAudioInputDeviceEntry?>().FirstOrDefault();
        if (match is null)
            throw new ArgumentException(
                $"no PortAudio input device named '{descriptor.DeviceName}' on the configured host API.",
                nameof(descriptor));

        var selected = match.Value;
        var channels = descriptor.Channels ?? Math.Clamp(selected.MaxInputChannels, 1, 2);
        if (channels > selected.MaxInputChannels)
            throw new ArgumentException(
                $"PortAudio input '{selected.Name}' has {selected.MaxInputChannels} channels, but {channels} were requested.");
        var rate = descriptor.SampleRate
                   ?? (selected.DefaultSampleRate > 0 ? (int)Math.Round(selected.DefaultSampleRate) : 48_000);
        return (selected.GlobalDeviceIndex.ToString(CultureInfo.InvariantCulture), new AudioFormat(rate, channels));
    }

    private static AudioFormat FormatFor(AudioDeviceInfo? device, CaptureDescriptor? descriptor = null)
    {
        var rate = descriptor?.SampleRate
                   ?? (device is { DefaultSampleRate: > 0 } ? (int)Math.Round(device.DefaultSampleRate) : 48_000);
        var channels = descriptor?.Channels
                       ?? (device is not null ? Math.Clamp(device.MaxChannels, 1, 2) : 2);
        if (device is not null && channels > device.MaxChannels)
            throw new ArgumentException(
                $"PortAudio input '{device.Name}' has {device.MaxChannels} channels, but {channels} were requested.");
        return new AudioFormat(rate, channels);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = Uri.UnescapeDataString(equals >= 0 ? part[..equals] : part);
            values[key] = Uri.UnescapeDataString(equals >= 0 ? part[(equals + 1)..] : string.Empty);
        }
        return values;
    }

    private static string? Text(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int? Int(IReadOnlyDictionary<string, string> values, string key, int? min = null, int? max = null)
    {
        if (!values.TryGetValue(key, out var text)) return null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || min is { } lo && value < lo || max is { } hi && value > hi)
            throw new ArgumentException($"PortAudio option '{key}' is invalid.");
        return value;
    }

    private static double? Double(IReadOnlyDictionary<string, string> values, string key, double? min = null)
    {
        if (!values.TryGetValue(key, out var text)) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || min is { } lo && value < lo)
            throw new ArgumentException($"PortAudio option '{key}' is invalid.");
        return value;
    }

    private static string DescribeDevices(IReadOnlyList<AudioDeviceInfo> devices) =>
        devices.Count == 0 ? "none" : string.Join(", ", devices.Select(d => $"'{d.Name}'"));
}
