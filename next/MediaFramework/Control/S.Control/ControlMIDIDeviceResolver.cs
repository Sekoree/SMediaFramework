
namespace S.Control;

public enum ControlMIDIPortDirection
{
    Input,
    Output,
}

/// <summary>Identifies one MIDI binding (a device instance plus a direction) for resolution selection lookup.</summary>
public readonly record struct ControlMIDIResolutionKey(Guid DeviceInstanceId, ControlMIDIPortDirection Direction);

/// <summary>A snapshot of the current MIDI input and output ports.</summary>
public sealed record ControlMIDIPortCatalog(
    IReadOnlyList<ControlMIDIPortInfo> Inputs,
    IReadOnlyList<ControlMIDIPortInfo> Outputs);

/// <summary>
/// One enabled MIDI binding whose configured device could not be confidently matched to a current port
/// (<see cref="ControlDeviceMatchStatus.Ambiguous"/> or <see cref="ControlDeviceMatchStatus.Missing"/>),
/// carried to the fallback selection dialog so the user can pick the live port.
/// </summary>
public sealed record ControlMIDIResolutionRequest(
    Guid DeviceInstanceId,
    string DeviceName,
    ControlMIDIPortDirection Direction,
    ControlDeviceMatchStatus Status,
    string Message,
    IReadOnlyList<ControlMIDIPortInfo> Candidates,
    IReadOnlyList<ControlMIDIPortInfo> AvailablePorts);

/// <summary>
/// Pure helpers for the fallback device-selection flow: figure out which configured MIDI bindings need
/// the user to pick a current port, and write those picks back into the project config. No I/O, no
/// PortMIDI, no UI — the caller supplies the live port catalog and the chosen ports.
/// </summary>
public static class ControlMIDIDeviceResolver
{
    /// <summary>
    /// Builds one resolution request per enabled MIDI input/output binding that does not confidently match
    /// a single current port. Bindings that match, or that are not bound at all, are skipped.
    /// </summary>
    public static IReadOnlyList<ControlMIDIResolutionRequest> BuildRequests(
        ControlSystemConfig config,
        IReadOnlyList<ControlMIDIPortInfo> inputs,
        IReadOnlyList<ControlMIDIPortInfo> outputs)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        var requests = new List<ControlMIDIResolutionRequest>();
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.MIDI && d.IsEnabled))
        {
            var name = string.IsNullOrWhiteSpace(device.Name) ? "(unnamed MIDI)" : device.Name;

            if (HasInputBinding(device.Binding))
                AddIfUnresolved(requests, device.Id, name, ControlMIDIPortDirection.Input,
                    ControlDeviceMatcher.MatchMIDIInput(device, inputs), inputs);

            if (HasOutputBinding(device.Binding))
                AddIfUnresolved(requests, device.Id, name, ControlMIDIPortDirection.Output,
                    ControlDeviceMatcher.MatchMIDIOutput(device, outputs), outputs);
        }

        return requests;
    }

    /// <summary>
    /// Returns a copy of <paramref name="config"/> with the chosen port id/name written into each selected
    /// MIDI binding. Selections for unknown devices are ignored; an empty map returns the original config.
    /// </summary>
    public static ControlSystemConfig ApplySelections(
        ControlSystemConfig config,
        IReadOnlyDictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo> selections)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0)
            return config;

        var devices = config.Devices.ToList();
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            if (device.Protocol != ControlDeviceProtocol.MIDI)
                continue;

            var binding = device.Binding;
            if (selections.TryGetValue(new ControlMIDIResolutionKey(device.Id, ControlMIDIPortDirection.Input), out var input))
                binding = binding with { MIDIInputDeviceId = input.Id, MIDIInputDeviceName = input.Name };
            if (selections.TryGetValue(new ControlMIDIResolutionKey(device.Id, ControlMIDIPortDirection.Output), out var output))
                binding = binding with { MIDIOutputDeviceId = output.Id, MIDIOutputDeviceName = output.Name };

            if (!ReferenceEquals(binding, device.Binding))
                devices[i] = device with { Binding = binding };
        }

        return config with { Devices = devices };
    }

    private static void AddIfUnresolved(
        List<ControlMIDIResolutionRequest> requests,
        Guid deviceInstanceId,
        string deviceName,
        ControlMIDIPortDirection direction,
        ControlMIDIPortMatch match,
        IReadOnlyList<ControlMIDIPortInfo> availablePorts)
    {
        if (match.Status is not (ControlDeviceMatchStatus.Ambiguous or ControlDeviceMatchStatus.Missing))
            return;

        requests.Add(new ControlMIDIResolutionRequest(
            deviceInstanceId,
            deviceName,
            direction,
            match.Status,
            match.Message,
            match.Candidates,
            availablePorts));
    }

    private static bool HasInputBinding(ControlDeviceBindingConfig binding) =>
        binding.MIDIInputDeviceId.HasValue || !string.IsNullOrWhiteSpace(binding.MIDIInputDeviceName);

    private static bool HasOutputBinding(ControlDeviceBindingConfig binding) =>
        binding.MIDIOutputDeviceId.HasValue || !string.IsNullOrWhiteSpace(binding.MIDIOutputDeviceName);
}
