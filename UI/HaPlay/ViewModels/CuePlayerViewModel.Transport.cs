using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public partial class CuePlayerViewModel
{
    [RelayCommand(CanExecute = nameof(CanStandbySelected))]
    private void StandbySelected()
    {
        if (SelectedCueNode is null)
            return;
        if (SelectedCueNode.Kind == CueNodeKind.Group && ResolveFireableCue(SelectedCueNode) is null)
            return;
        StandbyCueNode = SelectedCueNode;
        StatusMessage = Strings.Format(nameof(Strings.CueStandbyStatusFormat), CueDisplay(SelectedCueNode));
    }

    private bool CanStandbySelected() =>
        SelectedCueNode is { Kind: CueNodeKind.Group } group
            ? ResolveFireableCue(group) is not null
            : SelectedCueNode is not null;

    [RelayCommand(CanExecute = nameof(CanFireSelectedVisualizer))]
    private Task FireSelectedVisualizer()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Visualizer } cue)
            return Task.CompletedTask;

        _selectedCuePendingForGo = false;
        return FireVisualizerIndependentlyAsync(cue);
    }

    private async Task FireVisualizerIndependentlyAsync(CueNodeViewModel cue)
    {
        // This is deliberately independent of GO/standby. A visualizer is commonly used as a
        // persistent overlay while the main cue-list transport keeps advancing, so applying an
        // edited Start/Stop cue must not cancel the current transport run or consume its standby.
        try
        {
            var result = await ExecuteCueAsync(cue, CancellationToken.None);
            ApplyCueExecutionResult(cue, result, mediaExecutionConfigured: false);
        }
        catch (Exception ex)
        {
            // Keep the main transport untouched even when this auxiliary operation fails.
            StatusMessage = Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat),
                CueDisplay(cue),
                ex.Message);
        }
    }

    private bool CanFireSelectedVisualizer() =>
        SelectedCueNode is { Kind: CueNodeKind.Visualizer };

    [RelayCommand(CanExecute = nameof(CanStandbySelected))]
    private Task FireSelectedCueNow()
    {
        if (SelectedCueNode is not { } cue)
            return Task.CompletedTask;
        _selectedCuePendingForGo = false;
        _immediateJumpChain.Clear();
        return FireOperatorSelectedCueAsync(cue);
    }

    private Task FireOperatorSelectedCueAsync(CueNodeViewModel cue)
    {
        if (cue.Kind == CueNodeKind.Visualizer)
            return FireVisualizerIndependentlyAsync(cue);
        if (cue.Kind == CueNodeKind.Media && FindContainingGroupPath(cue).Count > 0)
            return FireGroupedMediaIndependentlyAsync(cue);
        return GoCore(cue);
    }

    private async Task FireGroupedMediaIndependentlyAsync(CueNodeViewModel cue)
    {
        if (MediaCueIndependentExecutor is not { } executor
            || cue.ToModel() is not MediaCueNode media)
        {
            StatusMessage = Strings.CueMediaExecutionNotConfigured;
            return;
        }

        try
        {
            var result = await executor(media, CancellationToken.None);
            ApplyCueExecutionResult(cue, result, mediaExecutionConfigured: true);
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(
                nameof(Strings.CueExecutionFailedWithDetailStatusFormat), CueDisplay(cue), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopSelectedCue))]
    private async Task StopSelectedCue()
    {
        if (SelectedCueNode is not { } cue)
            return;
        if (_runningVisualizers.ContainsKey(cue.Id))
            await StopVisualizerAsync(cue.Id);
        else if (_activeCueIds.Contains(cue.Id))
            await (CancelCueCallback?.Invoke(cue.Id) ?? Task.CompletedTask);
    }

    private bool CanStopSelectedCue() =>
        SelectedCueNode is { } cue
        && (_activeCueIds.Contains(cue.Id) || _runningVisualizers.ContainsKey(cue.Id));

    // Immediate Jump→Jump control flow carries this visited set across internally-triggered GO calls.
    // Any operator/Auto-Follow GO starts a fresh chain; landing on a non-jump clears it again.
    private readonly HashSet<Guid> _immediateJumpChain = [];

    // Per-Jump runtime history for the optional random no-repeat policy. This is deliberately session
    // state rather than project data: loading a project starts each random sequence afresh.
    private readonly Dictionary<Guid, Guid> _lastRandomJumpTargetIds = [];

    // A row click is a one-shot operator override for GO. After that selected cue fires, selection remains
    // in the properties drawer but subsequent GO presses return to the automatically advanced standby.
    private bool _selectedCuePendingForGo;

    [RelayCommand(CanExecute = nameof(CanGo))]
    private Task Go()
    {
        _immediateJumpChain.Clear();
        var selectedIntent = _selectedCuePendingForGo ? SelectedCueNode : null;
        _selectedCuePendingForGo = false;

        // A visualizer is an auxiliary persistent overlay. Firing a deliberately selected visualizer via
        // GO must not replace the current song/playhead or consume the song group's standby.
        return selectedIntent is not null
            ? FireOperatorSelectedCueAsync(selectedIntent)
            : GoCore();
    }

    private async Task GoCore(CueNodeViewModel? operatorSelectedCue = null)
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return;

        if (CurrentCueNode is not null && IsTransportPaused)
        {
            IsTransportPaused = false;
            _ = SetPlaybackPausedCallback?.Invoke(false);
            StatusMessage = Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
            return;
        }

        // A newly selected row is a one-shot operator override. Otherwise GO follows the live standby;
        // keeping the properties drawer on an older cue must not cause that cue to repeat forever.
        var selectedFire = operatorSelectedCue is not null
                           && ordered.Contains(ResolveFireableCue(operatorSelectedCue)!)
            ? operatorSelectedCue
            : null;
        var fire = selectedFire ?? StandbyCueNode ?? ordered.FirstOrDefault();
        if (fire is null)
            return;

        CancelTransportRun();
        var plan = BuildTriggerPlan(fire);
        if (plan.Count == 0)
            return;

        var resolvedFire = ResolveFireableCue(fire) ?? fire;
        var nextStandby = NextCueAfter(resolvedFire, ordered);
        CurrentCueNode = plan[0].Cue;
        IsTransportPaused = false;
        _suppressStandbyPreRollRefresh = true;
        try
        {
            StandbyCueNode = nextStandby;
        }
        finally
        {
            _suppressStandbyPreRollRefresh = false;
        }
        // Transport state is shown by the current/standby dots. Keep the editor selection untouched so
        // GO never replaces the properties drawer the operator is currently working in.
        StatusMessage = Strings.Format(
            nameof(Strings.CueGoStatusFormat),
            CueDisplay(fire),
            plan.Count,
            plan.Count == 1 ? string.Empty : Strings.PluralSuffixS);

        _transportRunCts = new CancellationTokenSource();
        try
        {
            await RunTriggerPlanAsync(plan, _transportRunCts.Token);
            SuggestPreRollRefresh();
        }
        catch (OperationCanceledException)
        {
            // Stop/Panic/next GO cancelled the prior run.
        }
    }

    private bool CanGo() => EnumerateFireableCueOrder().Any();

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (CurrentCueNode is null)
            return;
        IsTransportPaused = !IsTransportPaused;
        _ = SetPlaybackPausedCallback?.Invoke(IsTransportPaused);
        StatusMessage = IsTransportPaused
            ? Strings.Format(nameof(Strings.CuePausedStatusFormat), CueDisplay(CurrentCueNode))
            : Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
    }

    private bool CanPause() => CurrentCueNode is not null;

    [RelayCommand]
    private void Stop()
    {
        CancelTransportRun();
        if (StopPlaybackCallback is { } stopPlayback)
        {
            // The host ShowSession owns the synchronized clip + persistent-surface fade. Retire the UI rows
            // immediately, but do not issue an individual visualizer Stop that would detach the surface early.
            _ = stopPlayback();
            OnVisualizerLayersCleared();
        }
        else
        {
            StopAllVisualizers();
        }
        if (CurrentCueNode is null && StandbyCueNode is null && !IsTransportPaused)
            return;
        CurrentCueNode = null;
        IsTransportPaused = false;
        StatusMessage = Strings.CueStoppedStatus;
    }

    [RelayCommand]
    private void Panic()
    {
        CancelTransportRun();
        if (StopPlaybackCallback is { } stopPlayback)
        {
            _ = stopPlayback();
            OnVisualizerLayersCleared();
        }
        else
        {
            StopAllVisualizers();
        }
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        StatusMessage = Strings.CuePanicStatus;
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return;
        var anchor = StandbyCueNode ?? CurrentCueNode ?? ordered.First();
        var resolvedAnchor = ResolveFireableCue(anchor) ?? anchor;
        var idx = ordered.IndexOf(resolvedAnchor);
        if (idx < 0)
            return;
        var prev = idx > 0 ? ordered[idx - 1] : ordered[0];
        StandbyCueNode = prev;
        StatusMessage = Strings.Format(nameof(Strings.CueStandbyStatusFormat), CueDisplay(prev));
    }

    private bool CanBack() => EnumerateFireableCueOrder().Any();
}
