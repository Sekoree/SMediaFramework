using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class MainWindow : Window
{
    /// <summary>Phase E (§8.7) — debounce window-state writes so a drag-resize or move doesn't fire a
    /// disk write per pixel change. The on-Closing handler still writes the final state synchronously.</summary>
    private DispatcherTimer? _saveDebounce;

    /// <summary>Tracks the last "Normal" size the user dragged to, so restoring after Maximized still
    /// returns to the dragged size rather than the maximized one.</summary>
    private double _lastNormalWidth;
    private double _lastNormalHeight;
    private PixelPoint _lastNormalPosition;
    private bool _hasNormalSample;

    public MainWindow()
    {
        InitializeComponent();
        _lastNormalWidth = Width;
        _lastNormalHeight = Height;

        Opened += OnOpened;
        Closing += OnClosing;
        // Avalonia raises these on every drag pixel — debounce the save through _saveDebounce. We watch
        // ClientSize + WindowState via the unified PropertyChanged signal (typed-Subscribe wants an
        // IObserver, which is more ceremony than this needs).
        PropertyChanged += OnAvaloniaPropertyChanged;
        PositionChanged += (_, _) => OnGeometryChanged();
    }

    private void OnAvaloniaPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ClientSizeProperty || e.Property == WindowStateProperty)
            OnGeometryChanged();
    }

    /// <summary>Restore size + position from <see cref="MainViewModel.GetSavedWindowState"/>. Clamps the
    /// saved position to the union of available screens so a saved point that's now off-screen (laptop
    /// unplugged from external monitor) doesn't open the window invisibly.</summary>
    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var snap = vm.GetSavedWindowState();
        if (snap is null)
        {
            CaptureNormalSample();
            return;
        }

        var candidate = new PixelPoint(snap.X, snap.Y);
        var workingArea = FindWorkingArea(candidate) ?? Screens?.Primary?.WorkingArea;

        // Size first — Width/Height on a non-maximized window are the un-maximized values.
        if (snap.Width > 200 && snap.Height > 150)
        {
            Width = workingArea is { } area
                ? Math.Clamp(snap.Width, MinWidth, Math.Max(MinWidth, area.Width))
                : snap.Width;
            Height = workingArea is { } area2
                ? Math.Clamp(snap.Height, MinHeight, Math.Max(MinHeight, area2.Height))
                : snap.Height;
        }

        // Position: only honor when the point lands within a visible screen. Otherwise let the platform
        // pick (typically center of primary monitor).
        if (FindWorkingArea(candidate) is not null)
        {
            Position = candidate;
        }
        else if (workingArea is { } area)
        {
            Position = new PixelPoint(
                area.X + Math.Max(0, (area.Width - (int)Width) / 2),
                area.Y + Math.Max(0, (area.Height - (int)Height) / 2));
        }

        if (snap.IsMaximized)
            WindowState = WindowState.Maximized;

        CaptureNormalSample();
    }

    private PixelRect? FindWorkingArea(PixelPoint point)
    {
        if (Screens is null) return null;
        foreach (var s in Screens.All)
        {
            if (s.Bounds.Contains(point))
                return s.WorkingArea;
        }
        return null;
    }

    private void OnGeometryChanged()
    {
        if (WindowState == WindowState.Normal)
            CaptureNormalSample();

        _saveDebounce ??= new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick -= OnSaveDebounceTick;
        _saveDebounce.Tick += OnSaveDebounceTick;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void OnSaveDebounceTick(object? sender, System.EventArgs e)
    {
        _saveDebounce?.Stop();
        if (DataContext is not MainViewModel vm) return;
        vm.SaveWindowState(BuildSnapshot());
    }

    // Set once the operator has answered the unsaved-scripts prompt with Save/Don't-save, so the second
    // (programmatic) Close() doesn't re-prompt.
    private bool _forceClose;

    /// <summary>Save the *current* window state synchronously — the debounce timer fires after the next
    /// dispatcher pump, which doesn't happen during shutdown. Without the synchronous write on Closing,
    /// a fresh-from-launch resize and quit would lose the last 400 ms of edits. Also prompts to save scripts
    /// that only live in the scratch cache (project never saved) before letting the window close.</summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _saveDebounce?.Stop();
        if (DataContext is not MainViewModel vm)
            return;
        vm.SaveWindowState(BuildSnapshot());

        if (_forceClose || !vm.HasUnsavedScratchScripts)
            return;

        // Hold the close open while we ask; re-close programmatically once the operator has decided.
        e.Cancel = true;
        var choice = await new Dialogs.UnsavedScriptsDialog().ShowDialog<Dialogs.UnsavedScriptsChoice?>(this);
        switch (choice)
        {
            case Dialogs.UnsavedScriptsChoice.Save:
                await vm.SaveProjectCommand.ExecuteAsync(null);
                if (vm.HasUnsavedScratchScripts)
                    return; // Save-As cancelled or failed — keep the window open rather than lose scripts.
                _forceClose = true;
                Close();
                break;
            case Dialogs.UnsavedScriptsChoice.Discard:
                _forceClose = true;
                Close();
                break;
            default:
                break; // Cancel / dismissed — stay open.
        }
    }

    private void CaptureNormalSample()
    {
        if (WindowState != WindowState.Normal)
            return;
        _lastNormalWidth = Width;
        _lastNormalHeight = Height;
        _lastNormalPosition = Position;
        _hasNormalSample = true;
    }

    /// <summary>Build the snapshot the way OnClosing wants it: the un-maximized size + position, plus
    /// the current maximized flag. Falls back to the live <see cref="Window.Width"/> / <see cref="Window.Height"/>
    /// when no "Normal" sample has been captured yet (e.g. the window was opened maximized and never
    /// restored down).</summary>
    private WindowStateSnapshot BuildSnapshot() => new()
    {
        Width = _hasNormalSample ? _lastNormalWidth : Width,
        Height = _hasNormalSample ? _lastNormalHeight : Height,
        X = _hasNormalSample ? _lastNormalPosition.X : Position.X,
        Y = _hasNormalSample ? _lastNormalPosition.Y : Position.Y,
        IsMaximized = WindowState == WindowState.Maximized,
    };
}
