using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class ControlWorkspaceView : UserControl
{
    private ScriptEditorWindow? _scriptEditorWindow;

    public ControlWorkspaceView()
    {
        InitializeComponent();
    }

    private void OnScriptListDoubleTapped(object? sender, TappedEventArgs e) => OpenScriptEditorWindow();

    private void OnEditScriptClick(object? sender, RoutedEventArgs e) => OpenScriptEditorWindow();

    private void OpenScriptEditorWindow()
    {
        if (DataContext is not ControlWorkspaceViewModel viewModel || !viewModel.HasSelectedScript)
            return;

        if (_scriptEditorWindow is not null)
        {
            _scriptEditorWindow.Activate();
            return;
        }

        var window = new ScriptEditorWindow { DataContext = viewModel };
        window.Closed += (_, _) => _scriptEditorWindow = null;
        _scriptEditorWindow = window;

        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }
}
