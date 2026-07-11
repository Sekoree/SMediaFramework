using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels;

/// <summary>
/// One row on the I/O Debug page: a pipeline stage (active clip, composition pump, or a video-output
/// pump) with its 1 Hz-windowed numbers and a 60 s sparkline of the row's headline value. Rows are
/// keyed and reused across refresh ticks (list-driven - later phases add new row kinds cheaply);
/// <see cref="PipelineStatsViewModel"/> owns creation/removal on the UI thread.
/// </summary>
public partial class PipelineStatsRowViewModel : ViewModelBase
{
    public PipelineStatsRowViewModel(string key, string kind, string name)
    {
        Key = key;
        Kind = kind;
        _name = name;
    }

    /// <summary>Stable identity across refresh ticks (session + stage + id).</summary>
    public string Key { get; }

    /// <summary>Stage category chip: "Playback", "Composition", "Video out", …</summary>
    public string Kind { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>Headline numbers (timings), monospace in the view.</summary>
    [ObservableProperty]
    private string? _primaryText;

    /// <summary>Secondary numbers (throughput/drops/queues), monospace in the view.</summary>
    [ObservableProperty]
    private string? _secondaryText;

    /// <summary>Unit label for the sparkline's current value (e.g. "ms").</summary>
    [ObservableProperty]
    private string? _sparklineLabel;

    // Rolling ring of the row's headline value, same shape as OutputLineViewModel's throughput ring
    // (60 entries × 1 s ticks = 1 minute window).
    public const int SparklineCapacity = 60;

    private readonly double[] _sparklineSamples = new double[SparklineCapacity];
    private int _sparklineCount;
    private int _sparklineHead;

    /// <summary>Current sparkline snapshot in oldest→newest order (see <see cref="OutputLineViewModel.SparklineSamples"/>).</summary>
    public IReadOnlyList<double> SparklineSamples
    {
        get
        {
            if (_sparklineCount == 0) return Array.Empty<double>();
            var result = new double[_sparklineCount];
            var start = (_sparklineHead - _sparklineCount + SparklineCapacity) % SparklineCapacity;
            for (var i = 0; i < _sparklineCount; i++)
                result[i] = _sparklineSamples[(start + i) % SparklineCapacity];
            return result;
        }
    }

    [ObservableProperty]
    private double _sparklinePeakSample;

    [ObservableProperty]
    private int _sparklineRevision;

    [ObservableProperty]
    private double _sparklineLastSample;

    /// <summary>Pushes this tick's headline value (already a windowed rate/duration) into the ring.</summary>
    public void RecordSparklineSample(double value)
    {
        _sparklineSamples[_sparklineHead] = value;
        _sparklineHead = (_sparklineHead + 1) % SparklineCapacity;
        if (_sparklineCount < SparklineCapacity)
            _sparklineCount++;

        var peak = 0.0;
        for (var i = 0; i < _sparklineCount; i++)
            if (_sparklineSamples[i] > peak)
                peak = _sparklineSamples[i];

        SparklineLastSample = value;
        SparklinePeakSample = peak;
        SparklineRevision++;
        OnPropertyChanged(nameof(SparklineSamples));
    }

    // Previous cumulative counters/timings for the 1 Hz window deltas (UI-thread only - the poll timer
    // is the sole writer). Generic slots so every row kind can stash what it diffs.
    internal long PrevCount1;
    internal long PrevCount2;
    internal long PrevCount3;
    internal long PrevCount4;
    internal S.Media.Core.Diagnostics.TimingSnapshot PrevTiming1;
    internal S.Media.Core.Diagnostics.TimingSnapshot PrevTiming2;
}
