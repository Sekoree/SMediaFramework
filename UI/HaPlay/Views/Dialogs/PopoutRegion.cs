using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HaPlay.Resources;

namespace HaPlay.Views.Dialogs;

/// <summary>
/// Pops a region of the shell out into a floating non-modal <see cref="PopoutWindow"/> by MOVING the
/// host's single content control there (a control can only have one visual parent, and several
/// regions — the player deck in particular — must never materialize twice for the same view-model).
/// The host shows a placeholder with bring-to-front / restore actions while popped out; closing the
/// window reparents the content back. One instance per poppable region.
/// </summary>
/// <remarks>
/// Each window owns its own <c>LayoutManager</c>, and a subtree moved across windows within one
/// dispatcher pass can still have measure/arrange work queued on the source window's manager —
/// executing that queue then throws "Attempt to call InvalidateArrange on wrong LayoutManager".
/// Both moves therefore detach immediately but re-attach in a <see cref="DispatcherPriority.Background"/>
/// post, which runs after the source window's pending layout pass has drained.
/// </remarks>
public sealed class PopoutRegion
{
    private PopoutWindow? _window;
    private bool _moveInFlight;

    public bool IsOpen => _window is not null;

    /// <summary>
    /// Pops <paramref name="host"/>'s content into a floating window titled <paramref name="title"/>,
    /// or activates the already-open window for this region.
    /// </summary>
    public void OpenOrActivate(ContentControl host, string title, Window? owner)
    {
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        // _moveInFlight: a deferred re-attach is pending (host currently shows the placeholder) —
        // popping out now would move the placeholder instead of the real content.
        if (_moveInFlight || host.Content is not Control content)
            return;

        // Reparenting breaks DataContext inheritance (the window has none) — carry the host's
        // inherited context over so the moved subtree's bindings (including local bindings like
        // CuePlayerView's DataContext="{Binding CuePlayer}") keep resolving.
        var window = new PopoutWindow { Title = title, DataContext = host.DataContext };
        host.Content = BuildPlaceholder(title, window);
        _window = window;

        window.Closed += (_, _) =>
        {
            _window = null;
            window.HostControl.Content = null;
            _moveInFlight = true;
            Dispatcher.UIThread.Post(() =>
            {
                _moveInFlight = false;
                if (_window is null) // region not re-opened meanwhile
                    host.Content = content;
            }, DispatcherPriority.Background);
        };

        if (owner is not null)
            window.Show(owner);
        else
            window.Show();

        _moveInFlight = true;
        Dispatcher.UIThread.Post(() =>
        {
            _moveInFlight = false;
            if (ReferenceEquals(_window, window)) // window not closed meanwhile
                window.HostControl.Content = content;
        }, DispatcherPriority.Background);
    }

    private static Control BuildPlaceholder(string title, PopoutWindow window) =>
        new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings.Format(nameof(Strings.PopoutPlaceholderFormat), title),
                        Opacity = 0.7,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    MakeButton(Strings.PopoutBringToFrontButton, window.Activate),
                    MakeButton(Strings.PopoutRestoreButton, window.Close),
                },
            },
        };

    private static Button MakeButton(string caption, Action onClick)
    {
        var button = new Button
        {
            Content = caption,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
