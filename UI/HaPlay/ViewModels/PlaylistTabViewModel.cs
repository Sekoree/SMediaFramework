using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels;

public sealed partial class PlaylistTabViewModel : ObservableObject
{
    public PlaylistTabViewModel(string name)
    {
        Name = name;
    }

    [ObservableProperty]
    private string _name;

    /// <summary>True while the tab header shows its rename TextBox (entered by double-click; left on
    /// Enter/Escape/focus-loss). Plain clicks select the tab - the old always-editable header TextBox
    /// swallowed every click, so set tabs could never actually be switched.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>Runtime-only (not persisted): true for the Set the player is currently playing from, so the
    /// tab strip can mark it. The player owns this flag - see <c>MediaPlayerViewModel.RefreshPlayingTabIndicators</c>.</summary>
    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>Phase C.5 (§6.8) - discriminated items (files + live inputs). Replaces the v1 flat
    /// <c>Paths</c> string list. The bound playlist ListBox renders items via
    /// <see cref="PlaylistItem.DisplayName"/>; live items add <see cref="PlaylistItem.KindGlyph"/>.</summary>
    public ObservableCollection<PlaylistItem> Items { get; } = new();

    [ObservableProperty]
    private PlaylistItem? _selectedItem;

    [ObservableProperty]
    private bool _isLooping;

    [ObservableProperty]
    private bool _autoAdvance;

    [ObservableProperty]
    private bool _shuffle;

    [ObservableProperty]
    private bool _repeatAll;

    public PlaylistConfig ToConfig() => new()
    {
        Schema = "HaPlayPlaylist/v2",
        Name = string.IsNullOrWhiteSpace(Name) ? "Set" : Name,
        Items = Items.ToList(),
        SelectedItemId = SelectedItem?.Id,
        IsLooping = IsLooping,
        AutoAdvance = AutoAdvance,
        Shuffle = Shuffle,
        RepeatAll = RepeatAll,
    };

    public static PlaylistTabViewModel FromConfig(PlaylistConfig config)
    {
        var tab = new PlaylistTabViewModel(string.IsNullOrWhiteSpace(config.Name) ? "Set" : config.Name)
        {
            IsLooping = config.IsLooping,
            AutoAdvance = config.AutoAdvance,
            Shuffle = config.Shuffle,
            RepeatAll = config.RepeatAll,
        };

        // v2 path: discriminated items are canonical.
        if (config.Items.Count > 0)
        {
            foreach (var item in config.Items)
                tab.Items.Add(item);
        }
        // v1 fallback: project legacy file-path list onto FilePlaylistItem.
        else if (config.Paths is { Count: > 0 } legacy)
        {
            foreach (var p in legacy)
                tab.Items.Add(new FilePlaylistItem(p));
        }

        // Resolve selection. v2 id first, v1 path as fallback, else first item.
        PlaylistItem? selected = null;
        if (config.SelectedItemId is { } sid)
            selected = tab.Items.FirstOrDefault(i => i.Id == sid);
        if (selected is null && !string.IsNullOrEmpty(config.SelectedPath))
            selected = tab.Items.OfType<FilePlaylistItem>()
                .FirstOrDefault(f => string.Equals(f.Path, config.SelectedPath, StringComparison.Ordinal));
        tab.SelectedItem = selected ?? tab.Items.FirstOrDefault();

        return tab;
    }
}
