using Avalonia.Controls;
using Avalonia.Threading;
using HaPlay.Models;

namespace HaPlay.Views.Dialogs;

/// <summary>
/// Phase B (§12.2) - opt-in helper that restores a dialog's last-known size on open and saves the
/// current size (debounced) on resize / close. Attach via <see cref="Attach"/> from the dialog's
/// constructor; the persister owns the wiring and detaches itself when the window closes.
/// </summary>
/// <remarks>
/// Position is not persisted - dialogs always centre on their owner (Avalonia's
/// <see cref="WindowStartupLocation.CenterOwner"/>). The state lives in
/// <see cref="AppSettings.DialogSizes"/> keyed by the dialog id passed to <see cref="Attach"/>.
/// </remarks>
internal sealed class DialogStatePersister
{
    private static readonly object _settingsGate = new();
    private static AppSettings? _settings;

    private readonly Window _window;
    private readonly string _id;
    private readonly double _minWidth;
    private readonly double _minHeight;
    private DispatcherTimer? _saveDebounce;

    private DialogStatePersister(Window window, string id, double minWidth, double minHeight)
    {
        _window = window;
        _id = id;
        _minWidth = minWidth;
        _minHeight = minHeight;
    }

    /// <summary>Attach size persistence to <paramref name="window"/>. The <paramref name="id"/> is the
    /// dialog-type key - use a stable string like the class name so two instances of the same dialog
    /// share their size memory (which is what a user expects when they resize an Add… dialog once and
    /// open it again later).</summary>
    public static void Attach(Window window, string id, double minWidth = 200, double minHeight = 150)
    {
        var p = new DialogStatePersister(window, id, minWidth, minHeight);
        window.Opened += p.OnOpened;
        window.Closing += p.OnClosing;
        window.PropertyChanged += p.OnAvaloniaPropertyChanged;
    }

    private static AppSettings LoadOrGetCachedSettings()
    {
        lock (_settingsGate)
        {
            return _settings ??= AppSettings.Load();
        }
    }

    private static void SaveSnapshot(string id, DialogSizeSnapshot snap)
    {
        lock (_settingsGate)
        {
            // Keep the read cache coherent AND persist via the merge-safe write path (review H5: saving
            // the cached whole object clobbered other writers' fields with stale values).
            (_settings ??= AppSettings.Load()).DialogSizes[id] = snap;
        }

        AppSettings.Update(settings => settings.DialogSizes[id] = snap);
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        var settings = LoadOrGetCachedSettings();
        if (!settings.DialogSizes.TryGetValue(_id, out var snap))
            return;
        if (snap.Width >= _minWidth)
            _window.Width = snap.Width;
        if (snap.Height >= _minHeight)
            _window.Height = snap.Height;
    }

    private void OnAvaloniaPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.ClientSizeProperty) return;
        _saveDebounce ??= new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(300) };
        _saveDebounce.Tick -= OnSaveTick;
        _saveDebounce.Tick += OnSaveTick;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void OnSaveTick(object? sender, System.EventArgs e)
    {
        _saveDebounce?.Stop();
        SaveSnapshot(_id, new DialogSizeSnapshot { Width = _window.Width, Height = _window.Height });
    }

    /// <summary>Same as the main-window persister: flush synchronously on Closing so a fresh resize +
    /// quick close still records the last drag.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _saveDebounce?.Stop();
        SaveSnapshot(_id, new DialogSizeSnapshot { Width = _window.Width, Height = _window.Height });
        _window.Opened -= OnOpened;
        _window.Closing -= OnClosing;
        _window.PropertyChanged -= OnAvaloniaPropertyChanged;
    }
}
