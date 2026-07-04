using CommunityToolkit.Mvvm.ComponentModel;
using S.Control;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>A selectable MIDI device profile (or the generic "no profile" option) in the device dialog.</summary>
public sealed record MIDIDeviceProfileOption(string ProfileId, string Display);

/// <summary>The user-edited values returned by the MIDI device dialog.</summary>
public sealed record MIDIDeviceEditValues(string ProfileId, string? Alias, bool IsEnabled);

/// <summary>
/// Edit dialog for a MIDI control device. MIDI ports are bound from the MIDI Devices view, so this only
/// edits the parts a script cares about: the <b>alias</b> (the short <c>deviceKey</c> scripts use instead
/// of the often-long OS port name), the assigned device <b>profile</b> (e.g. the BCF2000 profile that turns
/// on 14-bit CC pairing), and the enabled state. The bound port name is shown read-only for context.
/// </summary>
public sealed partial class MIDIDeviceDialogViewModel : ViewModelBase
{
    public MIDIDeviceDialogViewModel(
        string title,
        string deviceName,
        string? profileId,
        string? alias,
        bool isEnabled,
        IReadOnlyList<ControlDeviceProfile> midiProfiles)
    {
        ArgumentNullException.ThrowIfNull(midiProfiles);

        Title = title;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "(unbound MIDI device)" : deviceName;
        _alias = alias ?? string.Empty;
        _isEnabled = isEnabled;

        ProfileOptions =
        [
            new MIDIDeviceProfileOption("generic-midi", "(generic MIDI — no profile)"),
            .. midiProfiles
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(p => new MIDIDeviceProfileOption(p.Id, $"{p.DisplayName} ({p.Id})")),
        ];
        SelectedProfile =
            ProfileOptions.FirstOrDefault(o => string.Equals(o.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            ?? ProfileOptions[0];
    }

    public string Title { get; }

    /// <summary>The bound MIDI port name, shown read-only.</summary>
    public string DeviceName { get; }

    public IReadOnlyList<MIDIDeviceProfileOption> ProfileOptions { get; }

    [ObservableProperty]
    private MIDIDeviceProfileOption _selectedProfile;

    [ObservableProperty]
    private string _alias;

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>Alias is optional (blank = scripts fall back to the device name / profile id), so Save is always allowed.</summary>
    public bool IsValid => true;

    public MIDIDeviceEditValues BuildValues() =>
        new(
            SelectedProfile.ProfileId,
            string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim(),
            IsEnabled);
}
