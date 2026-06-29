using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// Per-player wrapper around a shared <see cref="OutputLineViewModel"/>. Each player keeps its own checkbox state
/// so two players can route to overlapping subsets of outputs (e.g. main → NDI, preview → local SDL).
/// </summary>
public sealed partial class PlayerOutputBinding : ObservableObject
{
    public PlayerOutputBinding(OutputLineViewModel line)
    {
        Line = line;
    }

    public OutputLineViewModel Line { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    private double _gainDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    private bool _isMuted;

    /// <summary>Phase C (§4.3.4) first-cut audio matrix — per-output channel-mix mode. Applying a mode here
    /// rebuilds <see cref="Matrix"/>'s cells into the corresponding preset layout. Saved alongside the
    /// matrix itself so a config that pre-dates the matrix grid still loads cleanly.</summary>
    [ObservableProperty]
    private AudioRouteMixMode _mixMode = AudioRouteMixMode.Stereo;

    /// <summary>Phase C (§4.3.4) — full N×M channel-mix matrix. Sized lazily by the host VM once the
    /// source channel count is known (on session open).</summary>
    public AudioMatrixViewModel Matrix { get; } = new();

    public string GainText => IsMuted ? Strings.MutedLabel : Strings.Format(nameof(Strings.DecibelValueFormat), GainDb);
}
