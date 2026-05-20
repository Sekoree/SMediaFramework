using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels;

public sealed partial class PlaylistTabViewModel : ObservableObject
{
    public PlaylistTabViewModel(string name)
    {
        Name = name;
    }

    [ObservableProperty]
    private string _name;

    public ObservableCollection<string> Paths { get; } = new();

    [ObservableProperty]
    private string? _selectedPath;

    [ObservableProperty]
    private bool _isLooping;

    [ObservableProperty]
    private bool _autoAdvance;

    public PlaylistConfig ToConfig() => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Set" : Name,
        Paths = Paths.ToList(),
        SelectedPath = SelectedPath,
        IsLooping = IsLooping,
        AutoAdvance = AutoAdvance,
    };

    public static PlaylistTabViewModel FromConfig(PlaylistConfig config)
    {
        var tab = new PlaylistTabViewModel(string.IsNullOrWhiteSpace(config.Name) ? "Set" : config.Name)
        {
            IsLooping = config.IsLooping,
            AutoAdvance = config.AutoAdvance,
        };
        foreach (var path in config.Paths)
            tab.Paths.Add(path);
        tab.SelectedPath = config.SelectedPath is { } sp && tab.Paths.Contains(sp, StringComparer.Ordinal)
            ? sp
            : tab.Paths.FirstOrDefault();
        return tab;
    }
}
