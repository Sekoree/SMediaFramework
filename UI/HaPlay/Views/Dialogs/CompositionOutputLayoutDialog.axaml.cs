using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HaPlay.Views.Dialogs;

/// <summary>
/// Composition-level multi-output layout editor. Hosts an <see cref="Controls.OutputLayoutCanvas"/> bound to
/// a <see cref="HaPlay.ViewModels.Dialogs.CompositionOutputLayoutViewModel"/> so the operator can drag each
/// physical output to the part of the composition canvas it displays (video wall / stitched surface). Closes
/// with <c>true</c> on Save; the caller reads the edited items and writes them back to the bindings.
/// </summary>
public partial class CompositionOutputLayoutDialog : Window
{
    public CompositionOutputLayoutDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(CompositionOutputLayoutDialog), MinWidth, MinHeight);
    }

    private void SaveClick(object? sender, RoutedEventArgs e) => Close(true);

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
