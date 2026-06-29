using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>The user-edited values returned by the periodic OSC send dialog.</summary>
public sealed record PeriodicSendEditValues(string Name, string Address, int IntervalMs, bool IsEnabled);

/// <summary>
/// Add/edit dialog for a device's periodic OSC send (e.g. the X32 <c>/xremote</c> keep-alive). Edits the
/// display name, OSC address, interval, and enabled state; validation gates Save.
/// </summary>
public sealed partial class PeriodicSendDialogViewModel : ViewModelBase
{
    public PeriodicSendDialogViewModel(string title, string name, string address, int intervalMs, bool isEnabled)
    {
        Title = title;
        _name = name;
        _address = address;
        _intervalMsText = intervalMs.ToString(CultureInfo.InvariantCulture);
        _isEnabled = isEnabled;
    }

    public string Title { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _address;

    [ObservableProperty]
    private string _intervalMsText;

    [ObservableProperty]
    private bool _isEnabled;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Address)
        && int.TryParse(IntervalMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
        && ms > 0;

    partial void OnAddressChanged(string value) => OnPropertyChanged(nameof(IsValid));

    partial void OnIntervalMsTextChanged(string value) => OnPropertyChanged(nameof(IsValid));

    public PeriodicSendEditValues BuildValues()
    {
        int.TryParse(IntervalMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms);
        var address = Address.Trim();
        var name = string.IsNullOrWhiteSpace(Name) ? address : Name.Trim();
        return new PeriodicSendEditValues(name, address, ms, IsEnabled);
    }
}
