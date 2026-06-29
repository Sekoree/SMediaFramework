using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>The user-edited values returned by the control layer dialog.</summary>
public sealed record LayerEditValues(string Name, int Priority, bool IsActive);

/// <summary>
/// Add/edit dialog for a control layer. Layers are mutually exclusive at runtime; the dialog edits the
/// display name, priority (higher wins when seeding the active layer), and whether this layer should be
/// the active one. Validation gates Save.
/// </summary>
public sealed partial class LayerDialogViewModel : ViewModelBase
{
    public LayerDialogViewModel(string title, string name, int priority, bool isActive)
    {
        Title = title;
        _name = name;
        _priorityText = priority.ToString(CultureInfo.InvariantCulture);
        _isActive = isActive;
    }

    public string Title { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _priorityText;

    [ObservableProperty]
    private bool _isActive;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && int.TryParse(PriorityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(IsValid));

    partial void OnPriorityTextChanged(string value) => OnPropertyChanged(nameof(IsValid));

    public LayerEditValues BuildValues()
    {
        int.TryParse(PriorityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority);
        return new LayerEditValues(Name.Trim(), priority, IsActive);
    }
}
