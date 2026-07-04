using System.Globalization;

namespace S.Media.Audio.PortAudio;

/// <summary>
/// <see cref="IAudioBackend"/> over PortAudio — a thin adapter over <see cref="PortAudioDeviceCatalog"/> and
/// the <see cref="PortAudioOutput"/> / <see cref="PortAudioInput"/> constructors (no behaviour change). A
/// device <see cref="AudioDeviceInfo.Id"/> is the global PortAudio device index as an invariant string;
/// <c>null</c>/empty selects the system default device. Registered by
/// <see cref="MediaFrameworkRuntimePortAudioExtensions.UsePortAudio"/>.
/// </summary>
public sealed class PortAudioBackend : IAudioBackend
{
    public string Name => "PortAudio";

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
        PortAudioDeviceCatalog.EnumerateOutputDevices()
            .Select(d => new AudioDeviceInfo(
                DeviceId(d.GlobalDeviceIndex), d.Name, d.MaxOutputChannels, d.DefaultSampleRate, d.IsDefault))
            .ToArray();

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() =>
        PortAudioDeviceCatalog.EnumerateInputDevices()
            .Select(d => new AudioDeviceInfo(
                DeviceId(d.GlobalDeviceIndex), d.Name, d.MaxInputChannels, d.DefaultSampleRate, d.IsDefault))
            .ToArray();

    public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        var opt = options ?? new AudioBackendOptions();
        var output = new PortAudioOutput(format, ParseDeviceId(deviceId), opt.SuggestedLatencySeconds,
            opt.FramesPerBuffer, RingCapacityFrames(opt));
        return Started(output);
    }

    public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        var opt = options ?? new AudioBackendOptions();
        var input = new PortAudioInput(format, ParseDeviceId(deviceId), opt.SuggestedLatencySeconds,
            opt.FramesPerBuffer, RingCapacityFrames(opt));
        try
        {
            input.Start();
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    // IAudioBackend.CreateOutput/Input return a ready (started) device; on a start failure the half-open
    // device is disposed rather than leaked.
    private static PortAudioOutput Started(PortAudioOutput output)
    {
        try
        {
            output.Start();
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static string DeviceId(int globalDeviceIndex) =>
        globalDeviceIndex.ToString(CultureInfo.InvariantCulture);

    private static int? ParseDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null; // system default
        if (int.TryParse(deviceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return index;
        throw new ArgumentException($"invalid PortAudio device id '{deviceId}' (expected a global device index).",
            nameof(deviceId));
    }

    // PortAudioOutput/Input require ringCapacityFrames >= 64; map the 0 = "default" sentinel to their default.
    private static int RingCapacityFrames(AudioBackendOptions opt) =>
        opt.RingCapacityFrames > 0 ? opt.RingCapacityFrames : 16384;
}
