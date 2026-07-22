using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Media.Core.Diagnostics;
using S.Media.Players;
using S.Media.Session;

namespace HaPlay.ViewModels;

/// <summary>
/// The I/O workspace's Debug page: a 1 Hz list-driven poll of every active session's pipeline metrics
/// (decode timing, jitter-buffer depth, audio mix timing, composition pump/composite timing, and
/// per-video-output pump submit timing/queues/drops). Reads only lock-free snapshot APIs
/// (<see cref="ShowSession.GetActiveClipPipelineMetrics"/> / <see cref="ShowSession.GetAllCompositionStats"/>,
/// same family as the outputs-panel health poll) - no dispatcher marshaling into the sessions.
/// Rows are keyed and reused so sparklines stay continuous across ticks.
/// </summary>
public partial class PipelineStatsViewModel : ViewModelBase
{
    public ObservableCollection<PipelineStatsRowViewModel> Rows { get; } = [];

    /// <summary>Set by <c>MainViewModel</c>: the player decks whose sessions the poll walks.</summary>
    public Func<IReadOnlyList<MediaPlayerViewModel>>? ActivePlayersProbe { get; set; }

    /// <summary>Set by <c>CueShowSessionCoordinator</c>: the cue workspace's headless session (null when idle).</summary>
    internal Func<ShowSession?>? CueSessionProbe { get; set; }

    [ObservableProperty]
    private bool _hasRows;

    private readonly Dictionary<string, PipelineStatsRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenThisTick = new(StringComparer.Ordinal);
    private DispatcherTimer? _timer;

    /// <summary>Starts the 1 Hz poll (same cadence/priority as the outputs health poll). Idempotent.</summary>
    public void Start()
    {
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh())
        {
            IsEnabled = true,
        };
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    [RelayCommand]
    private void Clear()
    {
        Rows.Clear();
        _rowsByKey.Clear();
        HasRows = false;
    }

    internal void Refresh()
    {
        _seenThisTick.Clear();

        foreach (var player in ActivePlayersProbe?.Invoke() ?? [])
        {
            if (player.PipelineStatsSession is { } session)
                RefreshSession($"deck:{player.Name}", player.Name, session);
        }

        if (CueSessionProbe?.Invoke() is { } cueSession)
            RefreshSession("cues", "Cues", cueSession);

        // Retire rows whose stage disappeared (clip stopped, session closed, output removed).
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            var row = Rows[i];
            if (_seenThisTick.Contains(row.Key))
                continue;
            Rows.RemoveAt(i);
            _rowsByKey.Remove(row.Key);
        }

        HasRows = Rows.Count > 0;
    }

    private void RefreshSession(string keyPrefix, string sessionName, ShowSession session)
    {
        IReadOnlyList<ShowSession.ActiveClipPipelineMetrics> clips;
        IReadOnlyList<ClipCompositionRuntimeStats> compositions;
        try
        {
            clips = session.GetActiveClipPipelineMetrics();
            compositions = session.GetAllCompositionStats();
        }
        catch (ObjectDisposedException)
        {
            return; // session tore down between probe and read
        }

        foreach (var clip in clips)
        {
            var m = clip.Metrics;
            var clipLabel = clip.CueId is { Length: > 0 } cue ? $"{sessionName} · {cue}" : sessionName;

            if (m.Video is { } video && (video.DecodedCount > 0 || video.QueueDepth > 0))
            {
                var row = GetRow($"{keyPrefix}/clip:{clip.GroupId}/video", "Playback", clipLabel);
                row.Name = clipLabel;
                var decodeMs = video.DecodeTiming.WindowAvgMs(row.PrevTiming1);
                var displayedPerSec = Math.Max(0, video.DisplayedCount - row.PrevCount1);
                var lateDelta = Math.Max(0, video.DroppedLate - row.PrevCount2);
                var drainDelta = Math.Max(0, video.DroppedDrain - row.PrevCount3);
                row.PrimaryText =
                    $"decode {decodeMs,6:0.00} ms (max {video.DecodeTiming.MaxMs:0.0}) · {displayedPerSec,3} fps out";
                row.SecondaryText =
                    $"queue {video.QueueDepth}/{video.QueueCapacity} · late {lateDelta}/s · drain {drainDelta}/s · decoded {video.DecodedCount:N0} · pos {m.Clock.CurrentPosition:hh\\:mm\\:ss} ({m.Clock.MasterTypeName})";
                row.SparklineLabel = "decode ms";
                row.RecordSparklineSample(decodeMs);
                row.PrevTiming1 = video.DecodeTiming;
                row.PrevCount1 = video.DisplayedCount;
                row.PrevCount2 = video.DroppedLate;
                row.PrevCount3 = video.DroppedDrain;
            }

            if (m.AudioRouter is { } audio)
            {
                var row = GetRow($"{keyPrefix}/clip:{clip.GroupId}/audio", "Audio mix", clipLabel);
                row.Name = clipLabel;
                var mixMs = audio.MixTiming.WindowAvgMs(row.PrevTiming1);
                var chunksPerSec = Math.Max(0, audio.ChunksProduced - row.PrevCount1);
                var droppedDelta = Math.Max(0, audio.TotalDropped - row.PrevCount2);
                row.PrimaryText =
                    $"mix {mixMs,6:0.00} ms (max {audio.MixTiming.MaxMs:0.0}) · {chunksPerSec,3} chunks/s";
                row.SecondaryText =
                    $"outputs {audio.OutputCount} · enq {audio.TotalEnqueued:N0} · drop {droppedDelta}/s ({audio.TotalDropped:N0} total)"
                    + (m.PortAudio is { } pa ? $" · underrun {pa.UnderrunSamples:N0} smp" : string.Empty);
                row.SparklineLabel = "mix ms";
                row.RecordSparklineSample(mixMs);
                row.PrevTiming1 = audio.MixTiming;
                row.PrevCount1 = audio.ChunksProduced;
                row.PrevCount2 = audio.TotalDropped;
            }

            foreach (var pump in m.VideoOutputs)
            {
                var row = GetRow($"{keyPrefix}/clip:{clip.GroupId}/vout:{pump.OutputId}", "Video out", $"{clipLabel} → {pump.OutputId}");
                var pm = pump.Metrics;
                var submitMs = pm.SubmitTiming.WindowAvgMs(row.PrevTiming1);
                var fps = Math.Max(0, pm.SubmittedFrames - row.PrevCount1);
                var dropDelta = Math.Max(0, pm.DroppedFrames - row.PrevCount2);
                row.PrimaryText =
                    $"submit {submitMs,6:0.00} ms (max {pm.SubmitTiming.MaxMs:0.0}) · {fps,3} fps";
                row.SecondaryText =
                    $"queue {pm.CurrentQueuedDepth}/{pm.MaxQueueDepth} · drop {dropDelta}/s ({pm.DroppedFrames:N0} total) · submitted {pm.SubmittedFrames:N0}";
                row.SparklineLabel = "submit ms";
                row.RecordSparklineSample(submitMs);
                row.PrevTiming1 = pm.SubmitTiming;
                row.PrevCount1 = pm.SubmittedFrames;
                row.PrevCount2 = pm.DroppedFrames;
            }
        }

        foreach (var comp in compositions)
        {
            if (comp.FramesComposited == 0 && comp.LayerCount == 0)
                continue; // loaded but idle composition - skip until it does work
            var row = GetRow($"{keyPrefix}/comp:{comp.CompositionId}", "Composition", $"{sessionName} · {comp.CompositionId}");
            var pumpMs = comp.PumpTiming.WindowAvgMs(row.PrevTiming1);
            var compositeMs = comp.CompositeTiming.WindowAvgMs(row.PrevTiming2);
            var fps = Math.Max(0, comp.FramesComposited - row.PrevCount1);
            var overrunDelta = Math.Max(0, comp.PumpOverruns - row.PrevCount2);
            var budgetMs = comp.CanvasPeriod.TotalMilliseconds;
            row.PrimaryText =
                $"pump {pumpMs,6:0.00} ms / {budgetMs:0.0} budget · composite {compositeMs,6:0.00} ms · {fps,3} fps";
            row.SecondaryText =
                $"layers {comp.LayerCount} · overruns {overrunDelta}/s ({comp.PumpOverruns:N0} total) · behind-master {comp.FramesBehindMaster:N0} · slot-overflow {comp.SlotOverflowFrames:N0}";
            row.SparklineLabel = "pump ms";
            row.RecordSparklineSample(pumpMs);
            row.PrevTiming1 = comp.PumpTiming;
            row.PrevTiming2 = comp.CompositeTiming;
            row.PrevCount1 = comp.FramesComposited;
            row.PrevCount2 = comp.PumpOverruns;
        }
    }

    private PipelineStatsRowViewModel GetRow(string key, string kind, string name)
    {
        _seenThisTick.Add(key);
        if (_rowsByKey.TryGetValue(key, out var row))
            return row;
        row = new PipelineStatsRowViewModel(key, kind, name);
        _rowsByKey[key] = row;
        Rows.Add(row);
        return row;
    }
}
