namespace S.Control;

public sealed record ControlDeviceProfileBehaviors
{
    /// <summary>Identifies an incoming OSC meter blob decoder (e.g. <c>x32</c>).</summary>
    public string? MeterBlobDecoder { get; init; }
}

public static class ControlDeviceProfileSeeding
{
    public static List<ControlPeriodicOSCSendConfig> CreateDefaultPeriodicOSCSends(ControlDeviceProfile? profile)
    {
        if (profile is null)
            return [];

        return profile.Tasks
            .Where(task => task.IsDefaultEnabled)
            .Select(task => new ControlPeriodicOSCSendConfig
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
            if (candidate.MIDIController != controller)
                continue;
            if (candidate.MIDIChannel is int midiChannel && midiChannel != channel)
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
            if (candidate.MIDINote != note)
                continue;
            if (candidate.MIDIChannel is int midiChannel && midiChannel != channel)
                continue;
            control = candidate;
            return true;
        }

        return false;
    }
}
