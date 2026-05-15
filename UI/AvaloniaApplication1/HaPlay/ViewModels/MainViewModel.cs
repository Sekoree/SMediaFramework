using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HaPlay.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private int _nextPlayerNumber = 1;

    public MainViewModel()
    {
        OutputManagement = new OutputManagementViewModel();
        Players = new ObservableCollection<MediaPlayerViewModel>();
        // First player can't be removed — there's always at least one in the UI.
        Players.Add(CreatePlayer(removable: false));
        SelectedPlayer = Players[0];
    }

    public OutputManagementViewModel OutputManagement { get; }
    public ObservableCollection<MediaPlayerViewModel> Players { get; }

    [ObservableProperty]
    private MediaPlayerViewModel? _selectedPlayer;

    [RelayCommand]
    private void AddPlayer()
    {
        var p = CreatePlayer(removable: true);
        Players.Add(p);
        SelectedPlayer = p;
    }

    private MediaPlayerViewModel CreatePlayer(bool removable)
    {
        var name = $"Player {_nextPlayerNumber++}";
        return new MediaPlayerViewModel(OutputManagement, name, removable ? RemovePlayer : null);
    }

    private void RemovePlayer(MediaPlayerViewModel player)
    {
        var idx = Players.IndexOf(player);
        if (idx < 0) return;
        Players.RemoveAt(idx);
        if (SelectedPlayer == player)
            SelectedPlayer = Players.Count > 0 ? Players[Math.Min(idx, Players.Count - 1)] : null;
    }
}
