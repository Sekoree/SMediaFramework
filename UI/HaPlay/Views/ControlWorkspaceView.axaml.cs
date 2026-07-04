using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HaPlay.Resources;
using HaPlay.ViewModels;
using HaPlay.Views.Dialogs;

namespace HaPlay.Views;

public partial class ControlWorkspaceView : UserControl
{
    // One editor window PER SCRIPT (keyed by row) — editing two scripts side by side is two windows,
    // and selecting another script in the list never hijacks an already-open editor.
    private readonly Dictionary<ControlScriptRowViewModel, ScriptEditorWindow> _scriptEditorWindows = new();
    private readonly PopoutRegion[] _tabPopouts = [new(), new(), new(), new()];

    public ControlWorkspaceView()
    {
        InitializeComponent();
    }

    /// <summary>Pops the selected tab's content into its own window (placeholder stays in the tab).</summary>
    private void OnPopOutTabClick(object? sender, RoutedEventArgs e)
    {
        var index = ControlTabs.SelectedIndex;
        (ContentControl Host, string TabLabel)? region = index switch
        {
            0 => (SurfacesPopoutHost, Strings.ControlTabSurfaces),
            1 => (ScriptsPopoutHost, Strings.ControlTabScripts),
            2 => (MonitorPopoutHost, Strings.ControlTabMonitor),
            3 => (ToolsPopoutHost, Strings.ControlTabTools),
            _ => null,
        };
        if (region is not { } r)
            return;

        _tabPopouts[index].OpenOrActivate(
            r.Host,
            Strings.Format(nameof(Strings.PopoutControlTabTitleFormat), r.TabLabel),
            TopLevel.GetTopLevel(this) as Window);
    }

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
