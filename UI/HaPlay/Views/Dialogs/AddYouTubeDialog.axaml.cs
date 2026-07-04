using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class AddYouTubeDialog : Window
{
    public AddYouTubeDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(AddYouTubeDialog), MinWidth, MinHeight);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AddYouTubeDialogViewModel vm)
                vm.Completed += OnCompleted;
        };
        Closing += (_, _) => (DataContext as AddYouTubeDialogViewModel)?.CancelPending();
    }

    private void OnCompleted(YouTubePlaylistItem item) => Close(item);

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
