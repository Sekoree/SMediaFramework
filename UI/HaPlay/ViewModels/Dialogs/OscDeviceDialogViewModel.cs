using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.ControlGraph;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>A selectable OSC device profile (or the "raw OSC" no-profile option) in the device dialog.</summary>
public sealed record OscDeviceProfileOption(string? ProfileId, string Display);

/// <summary>The user-edited values returned by the OSC device dialog.</summary>
public sealed record OscDeviceEditValues(
    string Name,
    string ProfileId,
    string? Host,
    int Port,
    string? Alias,
    int? LocalPort,
    bool IsEnabled);

/// <summary>
/// Add/edit dialog for an OSC device instance (e.g. an X32). Lets the user pick a profile, set the remote
/// host/port, a script alias, an optional fixed local port, and the enabled state. Replies are received on
/// the client's own socket, so there is no separate listener to choose. Validation gates Save so the caller
/// always gets a usable <see cref="OscDeviceEditValues"/>.
/// </summary>
public sealed partial class OscDeviceDialogViewModel : ViewModelBase
{
    public OscDeviceDialogViewModel(
        string title,
        string name,
        string? profileId,
        string host,
        int port,
        string? alias,
        int? localPort,
        bool isEnabled,
        IReadOnlyList<ControlDeviceProfile> oscProfiles)
    {
        ArgumentNullException.ThrowIfNull(oscProfiles);

        Title = title;
        _name = name;
        _host = host;
        _portText = port.ToString(CultureInfo.InvariantCulture);
        _alias = alias ?? string.Empty;
        _localPortText = localPort is { } lp ? lp.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _isEnabled = isEnabled;

        ProfileOptions =
        [
            new OscDeviceProfileOption(null, "(raw OSC — no profile)"),
            .. oscProfiles
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(p => new OscDeviceProfileOption(p.Id, $"{p.DisplayName} ({p.Id})")),
        ];
        SelectedProfile = ProfileOptions.FirstOrDefault(o => o.ProfileId == profileId) ?? ProfileOptions[0];
    }

    public string Title { get; }

    public IReadOnlyList<OscDeviceProfileOption> ProfileOptions { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private OscDeviceProfileOption _selectedProfile;

    [ObservableProperty]
    private string _host;

    [ObservableProperty]
    private string _portText;

    [ObservableProperty]
    private string _alias;

    [ObservableProperty]
    private string _localPortText;

    [ObservableProperty]
    private bool _isEnabled;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
        && port is > 0 and <= 65535
        && IsLocalPortValid;

    private bool IsLocalPortValid =>
        string.IsNullOrWhiteSpace(LocalPortText)
        || (int.TryParse(LocalPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lp) && lp is >= 0 and <= 65535);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsValid));

    partial void OnPortTextChanged(string value) => OnPropertyChanged(nameof(IsValid));

    partial void OnLocalPortTextChanged(string value) => OnPropertyChanged(nameof(IsValid));

    public OscDeviceEditValues BuildValues()
    {
        int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port);
        int? localPort = string.IsNullOrWhiteSpace(LocalPortText)
            ? null
            : int.TryParse(LocalPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lp) ? lp : null;

        return new OscDeviceEditValues(
            Name.Trim(),
            SelectedProfile.ProfileId ?? string.Empty,
            string.IsNullOrWhiteSpace(Host) ? null : Host.Trim(),
            port,
            string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim(),
            localPort,
            IsEnabled);
    }
}
