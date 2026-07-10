using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

/// <summary>UX-03: the searchable keyboard-shortcut help overlay. Self-contained - owns its VM and closes
/// on Close / Escape.</summary>
public partial class KeyboardShortcutsDialog : Window
{
    public KeyboardShortcutsDialog()
    {
        InitializeComponent();
        DataContext = new KeyboardShortcutsDialogViewModel();
        Opened += (_, _) => SearchBox.Focus(); // land in the search box for type-to-filter
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
