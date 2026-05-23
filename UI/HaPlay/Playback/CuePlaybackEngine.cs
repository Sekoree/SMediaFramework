using Avalonia.Threading;
using HaPlay.Models;
using HaPlay.ViewModels;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Playback;

/// <summary>
/// Cue-side playback runtime. Owns one <see cref="HaPlayPlaybackSession"/> at a time and drives it
/// against the output lines the *cue itself* references via its <see cref="MediaCueNode.AudioRoutes"/>
/// and <see cref="MediaCueNode.VideoPlacements"/> (resolved through the cue list's video output
/// bindings). Completely independent of MediaPlayer tabs — only shares the
/// <see cref="OutputManagementViewModel"/> registry of physical output lines.
/// </summary>
public sealed class CuePlaybackEngine : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.CuePlaybackEngine");

    private readonly OutputManagementViewModel _outputs;
    private readonly CuePlayerViewModel _cuePlayer;
    private readonly object _gate = new();

    private HaPlayPlaybackSession? _session;
    private MediaCueNode? _activeCue;
    private CancellationTokenSource? _watchdogCts;

    public CuePlaybackEngine(OutputManagementViewModel outputs, CuePlayerViewModel cuePlayer)
    {
        _outputs = outputs;
        _cuePlayer = cuePlayer;
    }

    /// <summary>Raised on the UI thread when the active cue's media ends naturally (file reached
    /// duration). Cue VM listens to drive AutoFollow.</summary>
    public event EventHandler? NaturalEnd;

    public async Task<string?> ExecuteAsync(MediaCueNode cue, CancellationToken ct)
    {
        if (cue.Source is null)
            return "Cue has no source.";

        var list = await Dispatcher.UIThread.InvokeAsync(() => _cuePlayer.SelectedCueList?.ToModel());
        if (list is null)
            return "No cue list selected.";

        var targetLines = await Dispatcher.UIThread.InvokeAsync(() => ResolveTargetOutputLines(cue, list));
        if (targetLines.Count == 0)
            return "Cue has no audio routes or video placements wired to outputs.";

        await StopAsync().ConfigureAwait(false);

        HaPlayPlaybackSession? created = null;
        string? createErr = null;
        await Task.Run(() =>
        {
            try
            {
                var ok = HaPlayPlaybackSession.TryCreate(
                    cue.Source!,
                    targetLines,
                    _outputs,
                    out created,
                    out createErr,
                    filePlayback: BuildFileOptions(cue));
                if (!ok) created = null;
            }
            catch (Exception ex)
            {
                Trace.LogError(ex, "CuePlaybackEngine.ExecuteAsync: TryCreate threw");
                createErr = ex.Message;
            }
        }, ct).ConfigureAwait(false);

        if (created is null)
            return createErr ?? "Failed to open cue media.";

        lock (_gate)
        {
            _session = created;
            _activeCue = cue;
            _watchdogCts = new CancellationTokenSource();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                created.Router.Play();
            }
            catch (Exception ex)
            {
                Trace.LogError(ex, "CuePlaybackEngine.ExecuteAsync: Play threw");
                createErr = ex.Message;
            }
        });

        if (createErr is not null)
        {
            await StopAsync().ConfigureAwait(false);
            return createErr;
        }

        _ = WatchNaturalEndAsync(created, _watchdogCts!.Token);
        return $"playing {cue.Source.DisplayName} → {string.Join(", ", targetLines.Select(l => l.Definition.DisplayName))}";
    }

    public async Task StopAsync()
    {
        HaPlayPlaybackSession? toDispose;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            toDispose = _session;
            cts = _watchdogCts;
            _session = null;
            _activeCue = null;
            _watchdogCts = null;
        }

        try { cts?.Cancel(); } catch { /* best effort */ }
        try { cts?.Dispose(); } catch { /* best effort */ }

        if (toDispose is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try { toDispose.Dispose(); }
            catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine.StopAsync: dispose failed"); }
        });
    }

    /// <summary>Resolves the distinct output lines this cue touches: every line referenced by an
    /// audio route plus every line referenced by a video placement (via its composition → video
    /// output binding).</summary>
    private List<OutputLineViewModel> ResolveTargetOutputLines(MediaCueNode cue, CueList list)
    {
        var lineIds = new HashSet<Guid>();

        foreach (var route in cue.AudioRoutes)
            if (route.OutputLineId != Guid.Empty)
                lineIds.Add(route.OutputLineId);

        foreach (var placement in cue.VideoPlacements)
        {
            if (placement.CompositionId == Guid.Empty) continue;
            foreach (var binding in list.VideoOutputs)
                if (binding.CompositionId == placement.CompositionId && binding.OutputLineId != Guid.Empty)
                    lineIds.Add(binding.OutputLineId);
        }

        return _outputs.Outputs.Where(l => lineIds.Contains(l.Definition.Id)).ToList();
    }

    private static HaPlayFilePlaybackOptions BuildFileOptions(MediaCueNode cue) =>
        new(
            OutputPreset: Models.PlayerOutputPreset.AsSource,
            CueFadeInMs: Math.Max(0, cue.FadeInMs),
            CueFadeOutMs: Math.Max(0, cue.FadeOutMs));

    private async Task WatchNaturalEndAsync(HaPlayPlaybackSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);

                var duration = session.Player.Duration;
                if (duration <= TimeSpan.Zero) continue;
                var pos = session.Player.PlayClock.CurrentPosition;
                if (pos >= duration - TimeSpan.FromMilliseconds(50))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => NaturalEnd?.Invoke(this, EventArgs.Empty));
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.WatchNaturalEndAsync");
        }
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    }
}
