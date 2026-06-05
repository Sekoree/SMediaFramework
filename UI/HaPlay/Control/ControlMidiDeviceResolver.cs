using HaPlay.Models;

namespace HaPlay.ControlGraph;

public enum ControlMidiPortDirection
{
    Input,
    Output,
}

/// <summary>Identifies one MIDI binding (a device instance plus a direction) for resolution selection lookup.</summary>
public readonly record struct ControlMidiResolutionKey(Guid DeviceInstanceId, ControlMidiPortDirection Direction);

/// <summary>A snapshot of the current MIDI input and output ports.</summary>
public sealed record ControlMidiPortCatalog(
    IReadOnlyList<ControlMidiPortInfo> Inputs,
    IReadOnlyList<ControlMidiPortInfo> Outputs);

/// <summary>
/// One enabled MIDI binding whose configured device could not be confidently matched to a current port
/// (<see cref="ControlDeviceMatchStatus.Ambiguous"/> or <see cref="ControlDeviceMatchStatus.Missing"/>),
/// carried to the fallback selection dialog so the user can pick the live port.
/// </summary>
public sealed record ControlMidiResolutionRequest(
    Guid DeviceInstanceId,
    string DeviceName,
    ControlMidiPortDirection Direction,
    ControlDeviceMatchStatus Status,
    string Message,
    IReadOnlyList<ControlMidiPortInfo> Candidates,
    IReadOnlyList<ControlMidiPortInfo> AvailablePorts);

/// <summary>
/// Pure helpers for the fallback device-selection flow: figure out which configured MIDI bindings need
/// the user to pick a current port, and write those picks back into the project config. No I/O, no
/// PortMidi, no UI — the caller supplies the live port catalog and the chosen ports.
/// </summary>
public static class ControlMidiDeviceResolver
{
    /// <summary>
    /// Builds one resolution request per enabled MIDI input/output binding that does not confidently match
    /// a single current port. Bindings that match, or that are not bound at all, are skipped.
    /// </summary>
    public static IReadOnlyList<ControlMidiResolutionRequest> BuildRequests(
        ControlSystemConfig config,
        IReadOnlyList<ControlMidiPortInfo> inputs,
        IReadOnlyList<ControlMidiPortInfo> outputs)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        var requests = new List<ControlMidiResolutionRequest>();
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.Midi && d.IsEnabled))
        {
            var name = string.IsNullOrWhiteSpace(device.Name) ? "(unnamed MIDI)" : device.Name;

            if (HasInputBinding(device.Binding))
                AddIfUnresolved(requests, device.Id, name, ControlMidiPortDirection.Input,
                    ControlDeviceMatcher.MatchMidiInput(device, inputs), inputs);

            if (HasOutputBinding(device.Binding))
                AddIfUnresolved(requests, device.Id, name, ControlMidiPortDirection.Output,
                    ControlDeviceMatcher.MatchMidiOutput(device, outputs), outputs);
        }

        return requests;
    }

    /// <summary>
    /// Returns a copy of <paramref name="config"/> with the chosen port id/name written into each selected
    /// MIDI binding. Selections for unknown devices are ignored; an empty map returns the original config.
    /// </summary>
    public static ControlSystemConfig ApplySelections(
        ControlSystemConfig config,
        IReadOnlyDictionary<ControlMidiResolutionKey, ControlMidiPortInfo> selections)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0)
            return config;

        var devices = config.Devices.ToList();
        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            if (device.Protocol != ControlDeviceProtocol.Midi)
                continue;

            var binding = device.Binding;
            if (selections.TryGetValue(new ControlMidiResolutionKey(device.Id, ControlMidiPortDirection.Input), out var input))
                binding = binding with { MidiInputDeviceId = input.Id, MidiInputDeviceName = input.Name };
            if (selections.TryGetValue(new ControlMidiResolutionKey(device.Id, ControlMidiPortDirection.Output), out var output))
                binding = binding with { MidiOutputDeviceId = output.Id, MidiOutputDeviceName = output.Name };

            if (!ReferenceEquals(binding, device.Binding))
                devices[i] = device with { Binding = binding };
        }

        return config with { Devices = devices };
    }

    private static void AddIfUnresolved(
        List<ControlMidiResolutionRequest> requests,
        Guid deviceInstanceId,
        string deviceName,
        ControlMidiPortDirection direction,
        ControlMidiPortMatch match,
        IReadOnlyList<ControlMidiPortInfo> availablePorts)
    {
        if (match.Status is not (ControlDeviceMatchStatus.Ambiguous or ControlDeviceMatchStatus.Missing))
            return;

        requests.Add(new ControlMidiResolutionRequest(
            deviceInstanceId,
            deviceName,
            direction,
            match.Status,
            match.Message,
            match.Candidates,
            availablePorts));
    }

    private static bool HasInputBinding(ControlDeviceBindingConfig binding) =>
        binding.MidiInputDeviceId.HasValue || !string.IsNullOrWhiteSpace(binding.MidiInputDeviceName);

    private static bool HasOutputBinding(ControlDeviceBindingConfig binding) =>
        binding.MidiOutputDeviceId.HasValue || !string.IsNullOrWhiteSpace(binding.MidiOutputDeviceName);
}
