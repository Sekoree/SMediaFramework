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

        // Size first — Width/Height on a non-maximized window are the un-maximized values.
        if (snap.Width > 200 && snap.Height > 150)
        {
            Width = snap.Width;
            Height = snap.Height;
        }

        // Position: only honor when the point lands within a visible screen. Otherwise let the platform
        // pick (typically center of primary monitor).
        var candidate = new PixelPoint(snap.X, snap.Y);
        if (IsPointOnAnyScreen(candidate))
            Position = candidate;

        if (snap.IsMaximized)
            WindowState = WindowState.Maximized;

        CaptureNormalSample();
    }

    private bool IsPointOnAnyScreen(PixelPoint point)
    {
        if (Screens is null) return false;
        foreach (var s in Screens.All)
        {
            if (s.Bounds.Contains(point))
                return true;
        }
        return false;
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

    /// <summary>Save the *current* state synchronously — the debounce timer fires after the next
    /// dispatcher pump, which doesn't happen during shutdown. Without the synchronous write on Closing,
    /// a fresh-from-launch resize and quit would lose the last 400 ms of edits.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _saveDebounce?.Stop();
        if (DataContext is MainViewModel vm)
            vm.SaveWindowState(BuildSnapshot());
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
