using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Video;
using S.Media.Session;

namespace HaPlay.Core;

/// <summary>
/// The MVVM spine the HaPlay UI binds to — a thin view model over the headless <see cref="ShowSession"/>. Transport
/// commands marshal onto the session dispatcher; an async <c>[RelayCommand]</c> never lets an exception escape (one
/// that throws leaves its bound button stuck disabled), so failures land in <see cref="StatusMessage"/> instead.
/// It also holds the editable <see cref="ShowDocument"/> for cue authoring — edits rebuild the document, reload it
/// into the session, and can be saved back to JSON (the UI owns view-state; the document is the model, D10).
/// </summary>
public sealed partial class ShowSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ShowSession _session;
    private ShowDocument _document = new(1, [], [], [], [], [], []);

    public ShowSessionViewModel(ShowSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private long _positionTicks;
    [ObservableProperty] private long _durationTicks;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _cueCount;
    [ObservableProperty] private CueListItem? _selectedCue;
    [ObservableProperty] private string _newCueLabel = "";

    /// <summary>The cues of the loaded show — the cue-list workspace binds to this.</summary>
    public ObservableCollection<CueListItem> Cues { get; } = new();

    /// <summary>Load (replace) the show from a <see cref="ShowDocument"/> JSON string.</summary>
    public void LoadShow(string json)
    {
        try
        {
            _document = ShowDocument.FromJson(json);
            _session.LoadDocument(_document);
            StatusMessage = "show loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"load failed: {ex.Message}";
        }
    }

    /// <summary>Serialize the current (possibly edited) show to JSON — the save path.</summary>
    public string ToShowJson() => _document.ToJson();

    /// <summary>
    /// Attach a live preview surface to the loaded show's first composition (no-op if the show has none). The
    /// composited canvas then renders into <paramref name="output"/> once a composition-bound clip plays.
    /// </summary>
    public async Task<bool> AttachPreviewAsync(IVideoOutput output)
    {
        if (_document.Compositions.Count == 0)
            return false;
        var compositionId = _document.Compositions[0].Id;
        var attached = await _session.AttachCompositionOutputAsync(compositionId, output);
        StatusMessage = attached ? $"preview → {compositionId}" : "preview attach failed";
        return attached;
    }

    /// <summary>Bind the selected cue to a media file (replacing any existing clip for that cue).</summary>
    public Task SetClipForSelectedCueAsync(string mediaPath)
    {
        if (SelectedCue is not { } cue)
            return Task.CompletedTask;
        var others = _document.Clips.Where(clip => clip.CueId != cue.Id);
        _document = _document with { Clips = [.. others, new ShowClipBinding(cue.Id, mediaPath)] };
        return ApplyDocumentAsync($"cue {cue.Number} → {System.IO.Path.GetFileName(mediaPath)}");
    }

    /// <summary>Append a new, empty cue (auto-numbered) and apply it to the session.</summary>
    [RelayCommand]
    private Task AddCueAsync()
    {
        var number = _document.Cues.Count == 0 ? 1 : _document.Cues.Max(c => c.Number) + 1;
        var cue = new CueDefinition($"cue-{number}", number, $"New cue {number}");
        _document = _document with { Cues = [.. _document.Cues, cue] };
        return ApplyDocumentAsync("cue added");
    }

    /// <summary>Remove the selected cue (and any clips bound to it) and apply it to the session.</summary>
    [RelayCommand]
    private Task RemoveSelectedCueAsync()
    {
        if (SelectedCue is not { } cue)
            return Task.CompletedTask;
        _document = _document with
        {
            Cues = [.. _document.Cues.Where(c => c.Id != cue.Id)],
            Clips = [.. _document.Clips.Where(clip => clip.CueId != cue.Id)],
        };
        return ApplyDocumentAsync($"cue {cue.Number} removed");
    }

    /// <summary>Rename the selected cue to <see cref="NewCueLabel"/> (no-op if blank or none selected).</summary>
    [RelayCommand]
    private Task RenameSelectedCueAsync()
    {
        if (SelectedCue is not { } cue || string.IsNullOrWhiteSpace(NewCueLabel))
            return Task.CompletedTask;
        _document = _document with
        {
            Cues = [.. _document.Cues.Select(c => c.Id == cue.Id ? c with { Label = NewCueLabel } : c)],
        };
        return ApplyDocumentAsync($"cue {cue.Number} renamed");
    }

    [RelayCommand]
    private Task GoAsync() => RunAsync(_session.GoAsync(), "GO");

    [RelayCommand]
    private Task StopAsync() => RunAsync(_session.StopAsync(), "stop");

    /// <summary>Fire the cue selected in the cue list (no-op if none is selected).</summary>
    [RelayCommand]
    private Task FireSelectedCueAsync() =>
        SelectedCue is { } cue ? RunAsync(_session.FireCueAsync(cue.Id), $"fire cue {cue.Number}") : Task.CompletedTask;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var snap = (await _session.SnapshotAsync()).FirstOrDefault();
            if (snap is not null)
            {
                PositionTicks = snap.ClipPosition.Ticks;
                DurationTicks = snap.ClipDuration.Ticks;
                IsRunning = snap.IsRunning;
            }

            await RebuildCuesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"refresh failed: {ex.Message}";
        }
    }

    private async Task ApplyDocumentAsync(string status)
    {
        try
        {
            _session.LoadDocument(_document);
            await RebuildCuesAsync(force: true);
            StatusMessage = status;
        }
        catch (Exception ex)
        {
            StatusMessage = $"edit failed: {ex.Message}";
        }
    }

    private async Task RebuildCuesAsync(bool force = false)
    {
        var cues = await _session.GetCueDefinitionsAsync();
        CueCount = cues.Count;
        // A transport refresh only rebuilds on a count change (keeps the selection); an edit forces a rebuild so a
        // rename/clip change shows even when the count is unchanged. Either way the selection is restored by id.
        if (!force && Cues.Count == cues.Count)
            return;

        var selectedId = SelectedCue?.Id;
        Cues.Clear();
        foreach (var c in cues)
            Cues.Add(new CueListItem(c.Id, c.Number, c.Label));
        SelectedCue = Cues.FirstOrDefault(c => c.Id == selectedId);
    }

    private async Task RunAsync(Task action, string label)
    {
        try
        {
            await action;
            StatusMessage = label;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"{label} failed: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
