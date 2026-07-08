using System.Globalization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.Resources;
using HaPlay.Services;

namespace HaPlay.Views.Dialogs;

/// <summary>
/// Shown on launch when a previous session ended uncleanly and left a recoverable capture behind (see
/// <see cref="SessionRecoveryService"/>). For a session that had a saved project the operator can restore into
/// the original file or as an untitled copy that leaves the file untouched; an untitled session offers only the
/// copy. Closing via the window chrome returns <see cref="RecoverSessionChoice.Ignore"/> (keep the capture for
/// a later launch). The window is its own data context, matching the other in-repo dialogs.
/// </summary>
public partial class RecoverSessionDialog : Window, INotifyPropertyChanged
{
    // Parameterless ctor for the XAML previewer / designer only.
    public RecoverSessionDialog() : this([])
    {
    }

    public RecoverSessionDialog(IReadOnlyList<RecoverableSession> sessions)
    {
        Sessions = sessions;
        _selectedSession = sessions.FirstOrDefault();
        InitializeComponent();
        DataContext = this;
    }

    public IReadOnlyList<RecoverableSession> Sessions { get; }

    private RecoverableSession? _selectedSession;
    public RecoverableSession? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (ReferenceEquals(_selectedSession, value))
                return;
            _selectedSession = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSavedProject));
            OnPropertyChanged(nameof(IsUntitled));
            OnPropertyChanged(nameof(Body));
        }
    }

    /// <summary>True when the crashed session had a real project file (⇒ "into original" is offered).</summary>
    public bool HasSavedProject => SelectedSession?.HadSavedProject == true;

    /// <summary>Complement of <see cref="HasSavedProject"/> — toggles the untitled button row.</summary>
    public bool IsUntitled => !HasSavedProject;

    /// <summary>Localized description of what was found (project name + when it was last captured).</summary>
    public string Body => BuildBody(SelectedSession);

    /// <summary>The operator's decision; <see cref="RecoverSessionChoice.Ignore"/> when dismissed.</summary>
    public RecoverSessionChoice Result { get; private set; } = RecoverSessionChoice.Ignore;

    private static string BuildBody(RecoverableSession? session)
    {
        var title = session?.Info.ProjectTitle;
        if (string.IsNullOrEmpty(title))
            title = Strings.RecoverSessionUntitledLabel;
        var when = (session?.Info.LastSavedUtc ?? default).ToLocalTime().ToString("g", CultureInfo.CurrentUICulture);
        return Strings.Format(nameof(Strings.RecoverSessionBodyFormat), title, when);
    }

    private void OnRestoreIntoOriginalClick(object? sender, RoutedEventArgs e) => Finish(RecoverSessionChoice.RestoreIntoOriginal);
    private void OnRestoreAsCopyClick(object? sender, RoutedEventArgs e) => Finish(RecoverSessionChoice.RestoreAsCopy);
    private void OnDiscardClick(object? sender, RoutedEventArgs e) => Finish(RecoverSessionChoice.Discard);
    private void OnNotNowClick(object? sender, RoutedEventArgs e) => Finish(RecoverSessionChoice.Ignore);

    private void Finish(RecoverSessionChoice choice)
    {
        Result = choice;
        Close();
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum RecoverSessionChoice
{
    /// <summary>Load the recovered content and re-point it at the original file (unsaved — the operator then
    /// chooses Save to overwrite, or Save As).</summary>
    RestoreIntoOriginal,

    /// <summary>Load the recovered content as an untitled copy; the original file is left untouched.</summary>
    RestoreAsCopy,

    /// <summary>Throw the capture away.</summary>
    Discard,

    /// <summary>Leave the capture in place to be offered again next launch.</summary>
    Ignore,
}
