using Avalonia.Controls;

namespace HaPlay.Views;

public partial class ControlWorkspaceView : UserControl
{
    // The four panes are now a Dock.Avalonia layout (see ControlDockFactory) whose native float/split
    // supersedes the old tab pop-out. Per-pane behaviour (the script editor windows) lives in the
    // extracted views under Views/ControlPanes.
    public ControlWorkspaceView()
    {
        InitializeComponent();
    }
}
