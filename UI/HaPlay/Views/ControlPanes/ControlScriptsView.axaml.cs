using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlay.ViewModels;

namespace HaPlay.Views.ControlPanes;

public partial class ControlScriptsView : UserControl
{
    // One editor window PER SCRIPT (keyed by row) - editing two scripts side by side is two windows, and
    // selecting another script in the list never hijacks an already-open editor.
    private readonly Dictionary<ControlScriptRowViewModel, ScriptEditorWindow> _scriptEditorWindows = new();

    public ControlScriptsView() => InitializeComponent();

    private void OnScriptListDoubleTapped(object? sender, TappedEventArgs e) => OpenScriptEditorWindow();

    private void OnEditScriptClick(object? sender, RoutedEventArgs e) => OpenScriptEditorWindow();

    private void OpenScriptEditorWindow()
    {
        if (DataContext is not ControlWorkspaceViewModel viewModel
            || viewModel.SelectedScriptRow is not { } row)
            return;

        if (_scriptEditorWindows.TryGetValue(row, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new ScriptEditorWindow
        {
            DataContext = new ScriptEditorWindowViewModel(viewModel, row),
        };
        window.Closed += (_, _) => _scriptEditorWindows.Remove(row);
        _scriptEditorWindows[row] = window;

        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }
}
