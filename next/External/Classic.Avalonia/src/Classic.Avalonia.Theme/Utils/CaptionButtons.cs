using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;

namespace Classic.Avalonia.Theme.Utils;

/// <summary>
/// Vendored replacement for Avalonia 11's <c>Avalonia.Controls.Chrome.CaptionButtons</c>, which was
/// removed in Avalonia 12 (window chrome moved to WindowDrawnDecorations). Same template contract:
/// PART_CloseButton / PART_RestoreButton / PART_MinimizeButton / PART_FullScreenButton, window-state
/// pseudo-classes, and Attach/Detach lifetime driven by the hosting title bar.
/// </summary>
[TemplatePart("PART_CloseButton", typeof(Button))]
[TemplatePart("PART_RestoreButton", typeof(Button))]
[TemplatePart("PART_MinimizeButton", typeof(Button))]
[TemplatePart("PART_FullScreenButton", typeof(Button))]
[PseudoClasses(":minimized", ":normal", ":maximized", ":fullscreen")]
internal class CaptionButtons : TemplatedControl
{
    private IDisposable? _windowStateSubscription;

    protected Window? HostWindow { get; private set; }

    public virtual void Attach(Window hostWindow)
    {
        HostWindow = hostWindow;
        _windowStateSubscription = hostWindow.GetObservable(Window.WindowStateProperty)
            .Subscribe(state =>
            {
                PseudoClasses.Set(":minimized", state == WindowState.Minimized);
                PseudoClasses.Set(":normal", state == WindowState.Normal);
                PseudoClasses.Set(":maximized", state == WindowState.Maximized);
                PseudoClasses.Set(":fullscreen", state == WindowState.FullScreen);
            });
    }

    public virtual void Detach()
    {
        _windowStateSubscription?.Dispose();
        _windowStateSubscription = null;
        HostWindow = null;
    }

    protected virtual void OnClose() => HostWindow?.Close();

    protected virtual void OnMinimize()
    {
        if (HostWindow is { } window)
            window.WindowState = WindowState.Minimized;
    }

    protected virtual void OnRestore()
    {
        if (HostWindow is { } window)
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
    }

    protected virtual void OnToggleFullScreen()
    {
        if (HostWindow is { } window)
            window.WindowState = window.WindowState == WindowState.FullScreen
                ? WindowState.Normal
                : WindowState.FullScreen;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        Wire(e, "PART_CloseButton", OnClose);
        Wire(e, "PART_RestoreButton", OnRestore);
        Wire(e, "PART_MinimizeButton", OnMinimize);
        Wire(e, "PART_FullScreenButton", OnToggleFullScreen);

        void Wire(TemplateAppliedEventArgs args, string name, Action action)
        {
            if (args.NameScope.Find<Button>(name) is { } button)
                button.Click += (_, _) => action();
        }
    }
}
