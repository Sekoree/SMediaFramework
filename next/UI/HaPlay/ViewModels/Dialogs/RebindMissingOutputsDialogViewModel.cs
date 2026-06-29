using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels.Dialogs;

public sealed partial class RebindMissingOutputRowViewModel : ObservableObject
{
    public RebindMissingOutputRowViewModel(string missingDisplayName, IReadOnlyList<string> availableOutputs)
    {
        MissingDisplayName = missingDisplayName;
        foreach (var name in availableOutputs.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            ReplacementOptions.Add(name);
        SelectedReplacement = ReplacementOptions.FirstOrDefault();
    }

    public string MissingDisplayName { get; }

    public ObservableCollection<string> ReplacementOptions { get; } = new();

    [ObservableProperty]
    private string? _selectedReplacement;

    public bool HasReplacement => !string.IsNullOrWhiteSpace(SelectedReplacement);
}

public partial class RebindMissingOutputsDialogViewModel : ViewModelBase
{
    public RebindMissingOutputsDialogViewModel(
        IReadOnlyList<string> missingDisplayNames,
        IReadOnlyList<string> availableOutputs)
    {
        foreach (var missing in missingDisplayNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))
            Rows.Add(new RebindMissingOutputRowViewModel(missing, availableOutputs));
    }

    public ObservableCollection<RebindMissingOutputRowViewModel> Rows { get; } = new();

    public IReadOnlyDictionary<string, string> BuildReplacementMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            if (!row.HasReplacement || row.SelectedReplacement is null)
                continue;
            map[row.MissingDisplayName] = row.SelectedReplacement;
        }
        return map;
    }
}
