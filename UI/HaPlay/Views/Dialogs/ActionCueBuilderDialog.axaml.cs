using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class ActionCueBuilderDialog : Window
{
    public ActionCueBuilderDialog() => InitializeComponent();

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not ActionCueBuilderDialogViewModel vm)
        {
            Close(null);
            return;
        }

        if (!vm.TryBuild(out var endpointId, out var actionKind, out var commandText, out _))
        {
            return;
        }

        Close(new ActionCueBuilderResult(endpointId, actionKind, commandText));
    }
}

public sealed record ActionCueBuilderResult(Guid? EndpointId, CueActionKind ActionKind, string CommandText);
