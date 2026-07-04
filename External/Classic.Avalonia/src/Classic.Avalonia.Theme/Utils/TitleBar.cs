using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Input;

namespace Classic.Avalonia.Theme.Utils;

/// <summary>
/// Vendored replacement for Avalonia 11's <c>Avalonia.Controls.Chrome.TitleBar</c> (removed in
/// Avalonia 12): the auto-attaching title bar plus the interactions the old chrome provided — drag to
/// move, double-click to maximize/restore.
/// </summary>
[PseudoClasses(":minimized", ":normal", ":maximized", ":fullscreen")]
internal class TitleBar : AutoAttachTitleBar
{
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || VisualRoot is not Window window)
            return;

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2 && window.CanResize)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            window.BeginMoveDrag(e);
        }

        e.Handled = true;
    }
}
