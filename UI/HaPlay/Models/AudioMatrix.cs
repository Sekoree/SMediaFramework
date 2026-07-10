using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Phase C (§4.3.4) - persisted form of one cell in the per-output audio mix matrix. The matrix is
/// "rows = output (device) channels, columns = input (source) channels" with one cell per intersection
/// carrying its own gain (in dB) and mute flag. Cells with <c>Muted = true</c> or a gain below
/// <see cref="MutedFloorDb"/> register no route at the framework router; non-zero cells each install
/// one <see cref="S.Media.Core.Audio.AudioRouter.Route"/> via the multi-route per-pair API.
/// </summary>
public sealed record AudioMatrixCellConfig
{
    /// <summary>Source (input) channel index. 0 = L for a stereo source.</summary>
    public int InputChannel { get; init; }

    /// <summary>Output (output device) channel index. 0 = L for a stereo output.</summary>
    public int OutputChannel { get; init; }

    /// <summary>Per-cell gain in dB. Composed multiplicatively with master and per-output gain at routing time.</summary>
    public double GainDb { get; init; }

    /// <summary>When true, the cell is muted regardless of gain.</summary>
    public bool Muted { get; init; }
}

/// <summary>
/// Phase C (§4.3.4) - audible threshold for cells. Cells at or below this gain are dropped instead of
/// installed as a router route. Matches the lowest slider value the UI exposes (-60 dB), so any cell the
/// user can reach interactively still produces a route.
/// </summary>
public static class AudioMatrixDefaults
{
    public const double MutedFloorDb = -60.0;
    public const double IdentityGainDb = 0.0;
}
