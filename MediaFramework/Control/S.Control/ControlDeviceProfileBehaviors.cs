namespace S.Control;

public sealed record ControlDeviceProfileBehaviors
{
    public ControlProtocolMaintenanceBehavior? ProtocolMaintenance { get; init; }

    /// <summary>Identifies an incoming OSC meter blob decoder (e.g. <c>x32</c>).</summary>
    public string? MeterBlobDecoder { get; init; }
}

public sealed record ControlProtocolMaintenanceBehavior
{
    public int RenewIntervalMs { get; init; } = 8000;

    public List<string> MaintenanceAddresses { get; init; } =
    [
        "/xremote",
        "/subscribe",
        "/meters",
    ];
}

public static class ControlDeviceProfileSeeding
{
    public static List<ControlPeriodicOscSendConfig> CreateDefaultPeriodicOscSends(ControlDeviceProfile? profile)
    {
        if (profile is null)
            return [];

        return profile.Tasks
            .Where(task => task.IsDefaultEnabled)
            .Select(task => new ControlPeriodicOscSendConfig
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(task.DisplayName) ? task.Address : task.DisplayName,
                Address = task.Address,
                IntervalMs = task.IntervalMs,
                IsEnabled = true,
                Arguments = task.Arguments
                    .Select(argument => argument with { })
                    .ToList(),
            })
            .ToList();
    }
}

public static class ControlProfileProtocolBehavior
{
    public static bool HasProtocolMaintenance(ControlDeviceProfile? profile) =>
        profile?.Behaviors?.ProtocolMaintenance is not null
        || profile?.Tasks.Any(task => task.Kind == ControlDeviceTaskKind.ProtocolMaintenance) == true;

    public static bool SupportsMeterBlobDecoding(ControlDeviceProfile? profile) =>
        string.Equals(profile?.Behaviors?.MeterBlobDecoder, "x32", StringComparison.OrdinalIgnoreCase);

    public static int ResolveProtocolRenewIntervalMs(
        ControlDeviceProfile? profile,
        IEnumerable<ControlPeriodicOscSendConfig> sends)
    {
        var fromProfile = profile?.Behaviors?.ProtocolMaintenance?.RenewIntervalMs;
        if (fromProfile is > 0)
            return fromProfile.Value;

        return sends.Select(send => send.IntervalMs).DefaultIfEmpty(8000).Max();
    }

    public static bool IsMaintenanceAddress(ControlDeviceProfile? profile, string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var configured = profile?.Behaviors?.ProtocolMaintenance?.MaintenanceAddresses;
        if (configured is { Count: > 0 })
        {
            return configured.Any(entry => string.Equals(entry, address, StringComparison.OrdinalIgnoreCase));
        }

        return address is "/xremote" or "/subscribe" or "/meters";
    }

    public static bool UsesProtocolMaintenance(
        ControlDeviceProfile? profile,
        ControlDeviceInstanceConfig device,
        ControlPeriodicOscSendConfig send) =>
        device.Protocol == ControlDeviceProtocol.Osc
        && device.IsEnabled
        && send.IsEnabled
        && profile is not null
        && HasProtocolMaintenance(profile)
        && IsMaintenanceAddress(profile, send.Address);
}

public static class ControlProfileControlMatcher
{
    public static bool TryMatchControlChange(
        ControlDeviceProfile? profile,
        int channel,
        int controller,
        out ControlControlProfile? control)
    {
        control = null;
        if (profile is null)
            return false;

        foreach (var candidate in profile.Controls)
        {
            if (candidate.MidiController != controller)
                continue;
            if (candidate.MidiChannel is int midiChannel && midiChannel != channel)
                continue;
            control = candidate;
            return true;
        }

        return false;
    }

    public static bool TryMatchNote(
        ControlDeviceProfile? profile,
        int channel,
        int note,
        out ControlControlProfile? control)
    {
        control = null;
        if (profile is null)
            return false;

        foreach (var candidate in profile.Controls)
        {
            if (candidate.MidiNote != note)
                continue;
            if (candidate.MidiChannel is int midiChannel && midiChannel != channel)
                continue;
            control = candidate;
            return true;
        }

        return false;
    }
}
