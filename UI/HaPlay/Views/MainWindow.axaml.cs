using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class MainWindow : Window
{
    /// <summary>Phase E (§8.7) - debounce window-state writes so a drag-resize or move doesn't fire a
    /// disk write per pixel change. The on-Closing handler still writes the final state synchronously.</summary>
    private DispatcherTimer? _saveDebounce;

    /// <summary>Tracks the last "Normal" size the user dragged to, so restoring after Maximized still
    /// returns to the dragged size rather than the maximized one.</summary>
    private double _lastNormalWidth;
    private double _lastNormalHeight;
    private PixelPoint _lastNormalPosition;
    private bool _hasNormalSample;
    private WindowStateSnapshot? _preparedWindowState;

    // Native window frames are slightly larger than Avalonia's logical client size. Keeping this
    // margin inside the working area prevents the right-hand title-bar buttons from landing beyond
    // a monitor edge, especially with fractional display scaling.
    private const int WindowFrameSafetyMarginPixels = 32;

    public MainWindow()
    {
        InitializeComponent();
        _lastNormalWidth = Width;
        _lastNormalHeight = Height;

        Opened += OnOpened;
        Closing += OnClosing;
        // Avalonia raises these on every drag pixel - debounce the save through _saveDebounce. We watch
        // ClientSize + WindowState via the unified PropertyChanged signal (typed-Subscribe wants an
        // IObserver, which is more ceremony than this needs).
        PropertyChanged += OnAvaloniaPropertyChanged;
        PositionChanged += (_, _) => OnGeometryChanged();
    }

    /// <summary>Applies saved client geometry before Show creates the platform window. Screen-aware
    /// clamping still happens in <see cref="OnOpened"/>, once Avalonia exposes the monitor list.</summary>
    internal void PrepareInitialWindowState(WindowStateSnapshot? snapshot)
    {
        _preparedWindowState = snapshot;
        if (snapshot is null)
            return;

        if (snapshot.Width > 200 && snapshot.Height > 150)
        {
            Width = Math.Max(MinWidth, snapshot.Width);
            Height = Math.Max(MinHeight, snapshot.Height);
        }

        if (snapshot.IsMaximized)
            WindowState = WindowState.Maximized;
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

        // Offer to restore a crashed session (best-effort; runs after the window is up so it can show a modal).
        _ = vm.CheckForRecoverableSessionAsync();

        var snap = _preparedWindowState ?? vm.GetSavedWindowState();
        if (snap is null)
        {
            CaptureNormalSample();
            return;
        }

        var candidate = new PixelPoint(snap.X, snap.Y);
        var screen = FindScreen(candidate) ?? Screens?.Primary;
        var workingArea = screen?.WorkingArea;
        var scaling = Math.Max(0.1, screen?.Scaling ?? 1.0);

        // Size first - Width/Height on a non-maximized window are the un-maximized values.
        if (snap.Width > 200 && snap.Height > 150)
        {
            Width = workingArea is { } area
                ? ClampWindowDimension(snap.Width, MinWidth, area.Width, scaling)
                : snap.Width;
            Height = workingArea is { } area2
                ? ClampWindowDimension(snap.Height, MinHeight, area2.Height, scaling)
                : snap.Height;
        }

        // Clamp the complete window, not just its top-left point. The old point-only validation let a
        // valid saved X place the right edge (and native Close button) beyond the monitor working area.
        if (workingArea is { } visibleArea)
            Position = ClampWindowPosition(candidate, visibleArea, scaling, Width, Height);

        if (snap.IsMaximized)
            WindowState = WindowState.Maximized;

        CaptureNormalSample();

        // Re-check after the compositor has produced a frame: at this point the platform backend has
        // final DPI and native-frame metrics. This is intentionally position-only, so there is no
        // visible resize after launch.
        RequestAnimationFrame(_ => Dispatcher.UIThread.Post(
            EnsureNormalWindowIsVisible,
            DispatcherPriority.Loaded));
    }

    private Screen? FindScreen(PixelPoint point)
    {
        if (Screens is null) return null;
        foreach (var s in Screens.All)
        {
            if (s.Bounds.Contains(point))
                return s;
        }
        return null;
    }

    private static double ClampWindowDimension(double requestedDip, double minimumDip,
        int workingAreaPixels, double scaling)
    {
        var availableDip = Math.Max(minimumDip,
            (workingAreaPixels - WindowFrameSafetyMarginPixels) / Math.Max(0.1, scaling));
        return Math.Clamp(requestedDip, minimumDip, availableDip);
    }

    internal static PixelPoint ClampWindowPosition(PixelPoint requested, PixelRect workingArea,
        double scaling, double clientWidthDip, double clientHeightDip)
    {
        scaling = Math.Max(0.1, scaling);
        var frameWidth = (int)Math.Ceiling(Math.Max(1, clientWidthDip) * scaling)
                         + WindowFrameSafetyMarginPixels;
        var frameHeight = (int)Math.Ceiling(Math.Max(1, clientHeightDip) * scaling)
                          + WindowFrameSafetyMarginPixels;
        var maximumX = Math.Max(workingArea.X, workingArea.X + workingArea.Width - frameWidth);
        var maximumY = Math.Max(workingArea.Y, workingArea.Y + workingArea.Height - frameHeight);
        return new PixelPoint(
            Math.Clamp(requested.X, workingArea.X, maximumX),
            Math.Clamp(requested.Y, workingArea.Y, maximumY));
    }

    private void EnsureNormalWindowIsVisible()
    {
        if (WindowState != WindowState.Normal)
            return;
        var screen = FindScreen(Position) ?? Screens?.Primary;
        if (screen is null)
            return;
        Position = ClampWindowPosition(Position, screen.WorkingArea, screen.Scaling, Width, Height);
        CaptureNormalSample();
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

    // Set once the shared replacement gate has verified Save/auto-save or the operator chose Discard, so the
    // second (programmatic) Close() doesn't re-enter the gate.
    private bool _forceClose;

    /// <summary>Save the *current* window state synchronously - the debounce timer fires after the next
    /// dispatcher pump, which doesn't happen during shutdown. Without the synchronous write on Closing,
    /// a fresh-from-launch resize and quit would lose the last 400 ms of edits. Also prompts to save a project
    /// with unsaved changes (or scripts that only live in the scratch cache) before letting the window close.</summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _saveDebounce?.Stop();
        if (DataContext is not MainViewModel vm)
            return;
        vm.SaveWindowState(BuildSnapshot());

        // HAPLAY_SMOKE self-exits through a clean shutdown; never block that on a modal prompt.
        var smoke = System.Environment.GetEnvironmentVariable("HAPLAY_SMOKE");
        if (_forceClose || smoke is "1" or "true")
            return;

        // Hold the close open while the shared project-replacement gate verifies auto-save or asks the operator.
        e.Cancel = true;
        if (await vm.ConfirmCanReplaceProjectAsync(closing: true))
        {
            _forceClose = true;
            Close();
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
