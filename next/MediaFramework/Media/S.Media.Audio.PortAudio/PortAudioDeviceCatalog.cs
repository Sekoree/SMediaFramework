namespace S.Media.Audio.PortAudio;

/// <summary>One installed PortAudio host API (ALSA, WASAPI, …).</summary>
public readonly record struct PortAudioHostApiEntry(int Index, string Name, int TypeId, int DeviceCount);

/// <summary>One physical output device with a global PortAudio device index.</summary>
public readonly record struct PortAudioOutputDeviceEntry(
    int GlobalDeviceIndex,
    int HostApiIndex,
    string Name,
    int MaxOutputChannels,
    double DefaultSampleRate,
    bool IsDefault);

/// <summary>One physical input (capture) device with a global PortAudio device index.</summary>
public readonly record struct PortAudioInputDeviceEntry(
    int GlobalDeviceIndex,
    int HostApiIndex,
    string Name,
    int MaxInputChannels,
    double DefaultSampleRate,
    double DefaultLowInputLatency,
    bool IsDefault);

/// <summary>
/// Enumerates host APIs and output devices using the same <see cref="PortAudioRuntime"/> ref-count
/// as <see cref="PortAudioOutput"/>.
/// </summary>
public static class PortAudioDeviceCatalog
{
    /// <summary>Returns host APIs in ascending index order.</summary>
    public static IReadOnlyList<PortAudioHostApiEntry> EnumerateHostApis()
    {
        PortAudioRuntime.Acquire();
        try
        {
            var n = Native.Pa_GetHostApiCount();
            if (n <= 0)
                return [];

            var list = new List<PortAudioHostApiEntry>(n);
            for (var i = 0; i < n; i++)
            {
                var maybe = Native.Pa_GetHostApiInfo(i);
                if (!maybe.HasValue)
                    continue;
                var api = maybe.Value;
                list.Add(new PortAudioHostApiEntry(i, api.Name ?? $"Host API {i}", (int)api.type, api.deviceCount));
            }

            return list;
        }
        finally
        {
            PortAudioRuntime.Release();
        }
    }

    /// <summary>
    /// Returns output-capable devices. When <paramref name="hostApiIndex"/> is set, only devices
    /// belonging to that host API are returned.
    /// </summary>
    public static IReadOnlyList<PortAudioOutputDeviceEntry> EnumerateOutputDevices(int? hostApiIndex = null)
    {
        PortAudioRuntime.Acquire();
        try
        {
            var count = Native.Pa_GetDeviceCount();
            if (count <= 0)
                return [];

            var defaultOutput = Native.Pa_GetDefaultOutputDevice();
            var list = new List<PortAudioOutputDeviceEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var maybe = Native.Pa_GetDeviceInfo(i);
                if (!maybe.HasValue)
                    continue;
                var dev = maybe.Value;
                if (dev.maxOutputChannels <= 0)
                    continue;
                if (hostApiIndex is { } h && dev.hostApi != h)
                    continue;

                list.Add(new PortAudioOutputDeviceEntry(
                    i,
                    dev.hostApi,
                    dev.Name ?? $"Device {i}",
                    dev.maxOutputChannels,
                    dev.defaultSampleRate,
                    i == defaultOutput));
            }

            return list;
        }
        finally
        {
            PortAudioRuntime.Release();
        }
    }

    /// <summary>
    /// Returns input-capable (capture) devices. When <paramref name="hostApiIndex"/> is set, only
    /// devices belonging to that host API are returned. Mirrors <see cref="EnumerateOutputDevices"/>
    /// but filters by <c>maxInputChannels &gt; 0</c> and carries <c>defaultLowInputLatency</c> for the
    /// suggested-latency hint passed to <see cref="PortAudioInput"/>.
    /// </summary>
    public static IReadOnlyList<PortAudioInputDeviceEntry> EnumerateInputDevices(int? hostApiIndex = null)
    {
        PortAudioRuntime.Acquire();
        try
        {
            var count = Native.Pa_GetDeviceCount();
            if (count <= 0)
                return [];

            var defaultInput = Native.Pa_GetDefaultInputDevice();
            var list = new List<PortAudioInputDeviceEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var maybe = Native.Pa_GetDeviceInfo(i);
                if (!maybe.HasValue)
                    continue;
                var dev = maybe.Value;
                if (dev.maxInputChannels <= 0)
                    continue;
                if (hostApiIndex is { } h && dev.hostApi != h)
                    continue;

                list.Add(new PortAudioInputDeviceEntry(
                    i,
                    dev.hostApi,
                    dev.Name ?? $"Device {i}",
                    dev.maxInputChannels,
                    dev.defaultSampleRate,
                    dev.defaultLowInputLatency,
                    i == defaultInput));
            }

            return list;
        }
        finally
        {
            PortAudioRuntime.Release();
        }
    }
}
