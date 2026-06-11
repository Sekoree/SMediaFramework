using Avalonia.Controls;

namespace HaPlay.Views.Dialogs;

/// <summary>Floating non-modal shell for popped-out workspace regions (see <see cref="PopoutRegion"/>).</summary>
public partial class PopoutWindow : Window
{
    public PopoutWindow()
    {
        InitializeComponent();
    }

    /// <summary>The single content slot the popped-out region is reparented into.</summary>
    public ContentControl HostControl => Host;
}
