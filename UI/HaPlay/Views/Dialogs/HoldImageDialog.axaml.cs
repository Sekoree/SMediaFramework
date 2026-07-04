using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HaPlay.Views.Dialogs;

/// <summary>HOLD idle-image configuration for one player (was a flyout under the HOLD ▾ arrow —
/// promoted to a dialog so the browse/clear flow is discoverable). Binds directly to the player
/// view-model; changes apply live, Close just dismisses.</summary>
public partial class HoldImageDialog : Window
{
    public HoldImageDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(HoldImageDialog), MinWidth, MinHeight);
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
