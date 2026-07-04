using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>The user-edited values returned by the OSC listener dialog.</summary>
public sealed record OSCListenerEditValues(string Name, int LocalPort, bool IsEnabled);

/// <summary>
/// Add/edit dialog for an app-level OSC listener — an inbound UDP port that external OSC control sources
/// send to (device replies use the client socket and need no listener). Edits the name, local port, and
/// enabled state; validation gates Save.
/// </summary>
public sealed partial class OSCListenerDialogViewModel : ViewModelBase
{
    public OSCListenerDialogViewModel(string title, string name, int localPort, bool isEnabled)
    {
        Title = title;
        _name = name;
        _localPortText = localPort.ToString(CultureInfo.InvariantCulture);
        _isEnabled = isEnabled;
    }

    public string Title { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _localPortText;

    [ObservableProperty]
    private bool _isEnabled;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && int.TryParse(LocalPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
        && port is > 0 and <= 65535;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsValid));

    partial void OnLocalPortTextChanged(string value) => OnPropertyChanged(nameof(IsValid));

    public OSCListenerEditValues BuildValues()
    {
        int.TryParse(LocalPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port);
        return new OSCListenerEditValues(Name.Trim(), port, IsEnabled);
    }
}
