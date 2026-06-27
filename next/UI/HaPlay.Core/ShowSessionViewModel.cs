using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Session;

namespace HaPlay.Core;

/// <summary>
/// The MVVM spine the HaPlay UI binds to — a thin view model over the headless <see cref="ShowSession"/>. Transport
/// commands marshal onto the session dispatcher; an async <c>[RelayCommand]</c> never lets an exception escape (one
/// that throws leaves its bound button stuck disabled), so failures land in <see cref="StatusMessage"/> instead.
/// Transport state (<see cref="PositionTicks"/>/<see cref="DurationTicks"/>/<see cref="IsRunning"/>) is pulled from a
/// <see cref="TransportSnapshot"/> after each command and via <see cref="RefreshCommand"/>.
/// </summary>
public sealed partial class ShowSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ShowSession _session;

    public ShowSessionViewModel(ShowSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private long _positionTicks;
    [ObservableProperty] private long _durationTicks;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _cueCount;

    /// <summary>Load (replace) the show from a <see cref="ShowDocument"/> JSON string.</summary>
    public void LoadShow(string json)
    {
        try
        {
            _session.LoadDocument(ShowDocument.FromJson(json));
            StatusMessage = "show loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task GoAsync() => RunAsync(_session.GoAsync(), "GO");

    [RelayCommand]
    private Task StopAsync() => RunAsync(_session.StopAsync(), "stop");

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
            CueCount = (await _session.GetCueDefinitionsAsync()).Count;
        }
        catch (Exception ex)
        {
            StatusMessage = $"refresh failed: {ex.Message}";
        }
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
