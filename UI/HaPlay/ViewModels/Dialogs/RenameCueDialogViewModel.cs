using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>VM for the F2 / "Rename…" popup. Edits a copy of the cue's Number + Label so Esc
/// (or the close button) reliably discards changes without depending on the dialog's lifetime
/// semantics.</summary>
public sealed partial class RenameCueDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _number = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    public string DialogTitle => Strings.RenameCueDialogTitle;

    public static RenameCueDialogViewModel For(CueNodeViewModel cue) => new()
    {
        Number = cue.Number,
        Label = cue.Label,
    };
}

/// <summary>Returned on commit (Enter / OK). Null = cancel.</summary>
public sealed record RenameCueDialogResult(string Number, string Label);
