using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// Phase C (§4.3.4) — runtime model behind one row of the per-output audio matrix. Each instance
/// represents one (input channel × output channel) cell, with its own gain (dB) and mute toggle.
/// Cells fire <see cref="INotifyPropertyChanged"/> so the host view-model can push the change down to
/// <see cref="Playback.HaPlayPlaybackSession.TrySetOutputMatrix"/> without re-snapshotting the entire grid.
/// </summary>
public sealed partial class AudioMatrixCellViewModel : ObservableObject
{
    public AudioMatrixCellViewModel(int inputChannel, int outputChannel, double gainDb, bool muted)
    {
        InputChannel = inputChannel;
        OutputChannel = outputChannel;
        _gainDb = gainDb;
        _muted = muted;
    }

    public int InputChannel { get; }

    public int OutputChannel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    [NotifyPropertyChangedFor(nameof(IsAudible))]
    private double _gainDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    [NotifyPropertyChangedFor(nameof(IsAudible))]
    private bool _muted;

    public string GainText => Muted ? Strings.MuteLabel : Strings.Format(nameof(Strings.DecibelValueFormat), GainDb);

    /// <summary>True when this cell will install a router route at the next matrix push.</summary>
    public bool IsAudible => !Muted && GainDb > AudioMatrixDefaults.MutedFloorDb;

    public AudioMatrixCellConfig ToConfig() => new()
    {
        InputChannel = InputChannel,
        OutputChannel = OutputChannel,
        GainDb = GainDb,
        Muted = Muted,
    };
}

/// <summary>
/// Per-input channel attenuation/mute row. Applied to every matrix cell that reads this input channel.
/// </summary>
public sealed partial class AudioMatrixInputTrimViewModel : ObservableObject
{
    public AudioMatrixInputTrimViewModel(int inputChannel, int inputChannelCount, double gainDb, bool muted)
    {
        InputChannel = inputChannel;
        InputChannelCount = inputChannelCount;
        _gainDb = gainDb;
        _muted = muted;
    }

    public int InputChannel { get; }

    public int InputChannelCount { get; }

    public string Label =>
        InputChannelCount == 2
            ? (InputChannel == 0 ? Strings.ChannelInLeftLabel : Strings.ChannelInRightLabel)
            : Strings.Format(nameof(Strings.ChannelInNumberFormat), InputChannel + 1);

    [ObservableProperty]
    private double _gainDb;

    [ObservableProperty]
    private bool _muted;
}

/// <summary>
/// Phase C (§4.3.4) — one output device's view of the matrix: a list of (output-channel × input-channel)
/// cells. Hosted by <see cref="PlayerOutputBinding"/>; mutated as a single ObservableCollection so the
/// TreeDataGrid can render columns directly off <see cref="InputChannelCount"/> and the host VM can listen
/// to cell-level PropertyChanged for live route updates.
/// </summary>
public sealed partial class AudioMatrixViewModel : ObservableObject
{
    public AudioMatrixViewModel()
    {
        Cells.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Cells));
    }

    /// <summary>Source channel count this matrix was last sized to.</summary>
    [ObservableProperty]
    private int _inputChannelCount;

    /// <summary>Sink channel count this matrix was last sized to (typically 2 — see <see cref="HaPlayPlaybackSession.WireAudio"/>).</summary>
    [ObservableProperty]
    private int _outputChannelCount;

    public ObservableCollection<AudioMatrixCellViewModel> Cells { get; } = new();

    /// <summary>Phase C — rebuild cells for the new channel count. Preserves cells that map cleanly into the
    /// new layout (same input/output index) and defaults the rest. The "identity" default sets diagonal cells
    /// to 0 dB and the rest to silence so a stereo source → stereo sink resolves to L→L, R→R.</summary>
    public void Resize(int inputChannels, int outputChannels)
    {
        if (inputChannels == InputChannelCount && outputChannels == OutputChannelCount && Cells.Count > 0)
            return;

        var preserved = new Dictionary<(int, int), AudioMatrixCellViewModel>();
        foreach (var c in Cells)
            preserved[(c.InputChannel, c.OutputChannel)] = c;

        Cells.Clear();
        for (var oc = 0; oc < outputChannels; oc++)
        {
            for (var ic = 0; ic < inputChannels; ic++)
            {
                if (preserved.TryGetValue((ic, oc), out var existing))
                {
                    Cells.Add(existing);
                    continue;
                }
                // Identity default: diagonal cells at 0 dB, off-diagonal muted. For mono → stereo,
                // duplicate input ch 0 onto both outputs.
                var isIdentity = inputChannels >= 2
                    ? ic == oc
                    : ic == 0; // mono source: drive every output from input 0
                Cells.Add(new AudioMatrixCellViewModel(ic, oc,
                    gainDb: isIdentity ? AudioMatrixDefaults.IdentityGainDb : AudioMatrixDefaults.MutedFloorDb,
                    muted: !isIdentity));
            }
        }

        InputChannelCount = inputChannels;
        OutputChannelCount = outputChannels;
    }

    /// <summary>Phase C — overwrite the matrix from persisted cell configs (or the saved <see cref="AudioRouteMixMode"/>
    /// preset when no cells are stored). Preserves channel counts.</summary>
    public void ApplyConfig(IReadOnlyList<AudioMatrixCellConfig> cells)
    {
        if (cells.Count == 0)
            return;
        foreach (var saved in cells)
        {
            var match = Cells.FirstOrDefault(c =>
                c.InputChannel == saved.InputChannel && c.OutputChannel == saved.OutputChannel);
            if (match is null) continue;
            match.GainDb = saved.GainDb;
            match.Muted = saved.Muted;
        }
    }

    /// <summary>Phase C — overwrite the matrix from a preset mix mode. Useful before the user has touched any
    /// cell individually.</summary>
    public void ApplyPreset(AudioRouteMixMode mode)
    {
        if (OutputChannelCount < 1 || InputChannelCount < 1)
            return;

        foreach (var c in Cells)
        {
            c.GainDb = AudioMatrixDefaults.MutedFloorDb;
            c.Muted = true;
        }

        void Set(int input, int output)
        {
            var c = Cells.FirstOrDefault(x => x.InputChannel == input && x.OutputChannel == output);
            if (c is null) return;
            c.GainDb = AudioMatrixDefaults.IdentityGainDb;
            c.Muted = false;
        }

        switch (mode)
        {
            case AudioRouteMixMode.Stereo:
                for (var oc = 0; oc < OutputChannelCount; oc++)
                {
                    var src = InputChannelCount == 1
                        ? 0
                        : (oc < InputChannelCount ? oc : -1);
                    if (src >= 0)
                        Set(src, oc);
                }
                break;
            case AudioRouteMixMode.Swap:
                if (InputChannelCount >= 2)
                {
                    if (OutputChannelCount > 0) Set(1, 0);
                    if (OutputChannelCount > 1) Set(0, 1);
                    for (var oc = 2; oc < OutputChannelCount; oc++)
                        if (oc < InputChannelCount) Set(oc, oc);
                }
                else
                {
                    for (var oc = 0; oc < OutputChannelCount; oc++)
                        Set(0, oc);
                }
                break;
            case AudioRouteMixMode.MonoLeft:
                for (var oc = 0; oc < OutputChannelCount; oc++)
                    Set(0, oc);
                break;
            case AudioRouteMixMode.MonoRight:
                if (InputChannelCount >= 2)
                {
                    for (var oc = 0; oc < OutputChannelCount; oc++)
                        Set(1, oc);
                }
                else
                {
                    for (var oc = 0; oc < OutputChannelCount; oc++)
                        Set(0, oc);
                }
                break;
            case AudioRouteMixMode.Silence:
                /* every cell already muted */
                break;
        }
    }

    /// <summary>Phase C — current cell layout as persistable configs (filtered to changed/audible cells).</summary>
    public IReadOnlyList<AudioMatrixCellConfig> ToPersistableCells() =>
        Cells.Select(c => c.ToConfig()).ToList();

    /// <summary>Phase C — live cells passed to the session (every non-muted, audible cell becomes one router route).</summary>
    public IReadOnlyList<AudioMatrixCellConfig> ToRouteCells() =>
        Cells.Where(c => c.IsAudible).Select(c => c.ToConfig()).ToList();

    /// <summary>Phase C — pull the cell at <c>(input, output)</c> if present (null when the matrix isn't sized for those indices).</summary>
    public AudioMatrixCellViewModel? Cell(int input, int output) =>
        Cells.FirstOrDefault(c => c.InputChannel == input && c.OutputChannel == output);
}

/// <summary>
/// Phase C (§4.3.4) — one row in the TreeDataGrid: the cells that feed one (device, output-channel) pair.
/// <see cref="Cells"/> is ordered by input channel index so the view can bind a column per input.
/// </summary>
public sealed class AudioMatrixRow
{
    public AudioMatrixRow(PlayerOutputBinding binding, int outputChannel, int virtualOutputChannel, string label)
    {
        Binding = binding;
        OutputChannel = outputChannel;
        VirtualOutputChannel = virtualOutputChannel;
        Label = label;
    }

    public PlayerOutputBinding Binding { get; }

    public int OutputChannel { get; }

    /// <summary>1-based deterministic channel number across all selected outputs (VOut 1..N).</summary>
    public int VirtualOutputChannel { get; }

    /// <summary>Display label, e.g. "Main Speakers · Out L".</summary>
    public string Label { get; }

    /// <summary>Cells for this row, ordered by input channel index. Resolved lazily so view-bind expressions
    /// don't need to remember to re-fetch when the matrix is resized.</summary>
    public AudioMatrixCellViewModel? GetCell(int inputChannel) => Binding.Matrix.Cell(inputChannel, OutputChannel);
}

/// <summary>
/// One active matrix connection (audible cell) shown in the route list TreeDataGrid.
/// Uses the same <see cref="AudioMatrixCellViewModel"/> instance as the matrix grid so edits stay in sync.
/// </summary>
public sealed class AudioMatrixRouteRow
{
    public AudioMatrixRouteRow(
        int virtualOutputChannel,
        string outputLabel,
        int inputChannel,
        int inputChannelCount,
        AudioMatrixCellViewModel cell,
        string effectiveGainText)
    {
        VirtualOutputChannel = virtualOutputChannel;
        OutputLabel = outputLabel;
        InputChannel = inputChannel;
        InputChannelCount = inputChannelCount;
        Cell = cell;
        EffectiveGainText = effectiveGainText;
    }

    public int VirtualOutputChannel { get; }

    public string VirtualOutputLabel => Strings.Format(nameof(Strings.VirtualOutputLabelFormat), VirtualOutputChannel);

    public string OutputLabel { get; }

    public int InputChannel { get; }

    public int InputChannelCount { get; }

    public string InputLabel =>
        InputChannelCount == 2
            ? (InputChannel == 0 ? Strings.ChannelInLeftLabel : Strings.ChannelInRightLabel)
            : Strings.Format(nameof(Strings.ChannelInNumberFormat), InputChannel + 1);

    public AudioMatrixCellViewModel Cell { get; }

    public double GainDb
    {
        get => Cell.GainDb;
        set => Cell.GainDb = value;
    }

    public bool Muted
    {
        get => Cell.Muted;
        set => Cell.Muted = value;
    }

    public string EffectiveGainText { get; }
}
