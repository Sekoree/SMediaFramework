using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        OutputManagement = new OutputManagementViewModel();
        MediaPlayer = new MediaPlayerViewModel(OutputManagement);
    }

    public OutputManagementViewModel OutputManagement { get; }

    public MediaPlayerViewModel MediaPlayer { get; }
}
