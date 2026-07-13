using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

/// <summary>UX-03: the searchable keyboard-shortcut help overlay. Self-contained - owns its VM and closes
/// on Close / Escape.</summary>
public partial class KeyboardShortcutsDialog : Window
{
    private readonly Action<CueHotkeyProfile>? _save;

    public KeyboardShortcutsDialog() : this(new CueHotkeyProfile(), null)
    {
    }

    public KeyboardShortcutsDialog(CueHotkeyProfile hotkeys, Action<CueHotkeyProfile>? save)
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        _save = save;
        DataContext = new KeyboardShortcutsDialogViewModel(hotkeys);
        Opened += (_, _) => SearchBox.Focus(); // land in the search box for type-to-filter
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnResetClick(object? sender, RoutedEventArgs e) =>
        (DataContext as KeyboardShortcutsDialogViewModel)?.ResetCueHotkeys();

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KeyboardShortcutsDialogViewModel vm
            || !vm.TryBuildCueHotkeys(out var profile))
            return;
        _save?.Invoke(profile);
        Close();
    }

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
