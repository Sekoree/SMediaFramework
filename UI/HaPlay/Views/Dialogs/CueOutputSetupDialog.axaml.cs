using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HaPlay.Views.Dialogs;

public partial class CueOutputSetupDialog : Window
{
    public CueOutputSetupDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(CueOutputSetupDialog), MinWidth, MinHeight);
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
