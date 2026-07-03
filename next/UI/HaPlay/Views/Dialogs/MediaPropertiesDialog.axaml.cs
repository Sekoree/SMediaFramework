using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

/// <summary>
/// Common per-item properties dialog. The window's own result is the (possibly edited) playlist
/// item on OK, null on Cancel; the per-kind editor buttons open the existing add/edit dialogs as
/// children and feed their results back into the view-model.
/// </summary>
public partial class MediaPropertiesDialog : Window
{
    public MediaPropertiesDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(MediaPropertiesDialog), MinWidth, MinHeight);
        Opened += async (_, _) =>
        {
            if (DataContext is MediaPropertiesDialogViewModel vm)
            {
                await vm.LoadDetailsAsync();
                await vm.LoadAudioTracksAsync();
            }
        };
    }

    private MediaPropertiesDialogViewModel? Vm => DataContext as MediaPropertiesDialogViewModel;

    private void OkClick(object? sender, RoutedEventArgs e) => Close(Vm?.BuildResult());

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private async void EditSubtitlesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Current: FilePlaylistItem file } vm)
            return;

        var dialogVm = new SubtitleSelectionDialogViewModel();
        dialogVm.Load(file.Path, file.Subtitles);
        var dialog = new SubtitleSelectionDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<IReadOnlyList<CueSubtitleSelection>?>(this);
        if (result is not null)
            vm.ApplySubtitles(result);
    }

    private async void EditSceneClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Current: MmdPlaylistItem mmd } vm)
            return;

        var dialogVm = new AddMmdDialogViewModel();
        dialogVm.LoadFromExisting(mmd);
        var dialog = new AddMmdDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<MmdPlaylistItem?>(this);
        if (result is not null)
            vm.ReplaceItem(result);
    }

    private async void ChangeStreamsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { Current: YouTubePlaylistItem youTube } vm)
            return;

        var dialogVm = new AddYouTubeDialogViewModel();
        dialogVm.LoadFromExisting(youTube);
        var dialog = new AddYouTubeDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<YouTubePlaylistItem?>(this);
        if (result is not null)
        {
            vm.ReplaceItem(result);
            await vm.LoadDetailsAsync();
        }
    }
}
