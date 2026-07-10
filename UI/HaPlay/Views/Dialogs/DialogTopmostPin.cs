using Avalonia.Controls;

namespace HaPlay.Views.Dialogs;

/// <summary>
/// Keeps a modal dialog above every other window for as long as it is open. Avalonia already tries to
/// re-activate a modal child when its disabled owner is clicked (<c>Window.OnGotInputWhenDisabled</c>),
/// but on Linux that ends in a <c>_NET_ACTIVE_WINDOW</c> request that Wayland compositors (KWin via
/// XWayland in particular) routinely ignore under focus-stealing prevention - the dialog stays buried
/// behind the main window and the app reads as locked up, since the modal blocks all input. Keep-above
/// (<see cref="Window.Topmost"/>) is a window STATE, not an activation request, and is honored, so a
/// pinned dialog can never slip behind its owner in the first place. The trade-off - the dialog also
/// floats above other applications while open - is acceptable for these dialogs precisely because they
/// are modal: the app is unusable until they're dealt with anyway.
/// </summary>
internal static class DialogTopmostPin
{
    /// <summary>Call from the dialog's constructor. Do NOT attach to non-modal tool windows (e.g. the
    /// HOLD image dialog or pop-outs) - those must be allowed behind other windows while unfocused.</summary>
    public static void Attach(Window dialog)
    {
        dialog.Opened += (_, _) => dialog.Topmost = true;
        // Drop the pin as the dialog goes away so the WM never briefly re-stacks a dying topmost window.
        dialog.Closing += (_, _) => dialog.Topmost = false;
    }
}
