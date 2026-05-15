using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels;

/// <summary>
/// Per-player wrapper around a shared <see cref="OutputLineViewModel"/>. Each player keeps its own checkbox state
/// so two players can route to overlapping subsets of outputs (e.g. main → NDI, preview → local SDL).
/// </summary>
public sealed partial class PlayerOutputBinding : ObservableObject
{
    public PlayerOutputBinding(OutputLineViewModel line)
    {
        Line = line;
    }

    public OutputLineViewModel Line { get; }

    [ObservableProperty]
    private bool _isSelected;
}
