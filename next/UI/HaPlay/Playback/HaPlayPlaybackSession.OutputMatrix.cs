using System.Threading;
using System.Diagnostics.CodeAnalysis;
using HaPlay.ViewModels;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Players;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Decode.FFmpeg.Audio;
using S.Media.Decode.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;
using S.Media.Session;
using S.Media.Audio.PortAudio;
using S.Media.Present.SDL3;

namespace HaPlay.Playback;

internal sealed partial class HaPlayPlaybackSession
{
    /// <summary>
    /// Build the channel map for a given mix mode against an arbitrary output width.
    /// </summary>
    internal static ChannelMap MixModeToChannelMap(AudioRouteMixMode mode, int sourceChannels, int sinkChannels)
    {
        var count = Math.Max(1, sinkChannels);
        var arr = new int[count];
        for (var i = 0; i < count; i++)
            arr[i] = ChannelMap.Silence;

        if (mode == AudioRouteMixMode.Silence)
            return new ChannelMap(arr);

        if (sourceChannels <= 0)
            return new ChannelMap(arr);

        if (sourceChannels == 1)
        {
            for (var i = 0; i < count; i++) arr[i] = 0;
            return new ChannelMap(arr);
        }

        switch (mode)
        {
            case AudioRouteMixMode.Swap:
                if (count > 0) arr[0] = Math.Min(1, sourceChannels - 1);
                if (count > 1) arr[1] = 0;
                for (var i = 2; i < count; i++)
                    arr[i] = i < sourceChannels ? i : ChannelMap.Silence;
                break;
            case AudioRouteMixMode.MonoLeft:
                for (var i = 0; i < count; i++) arr[i] = 0;
                break;
            case AudioRouteMixMode.MonoRight:
                var right = Math.Min(1, sourceChannels - 1);
                for (var i = 0; i < count; i++) arr[i] = right;
                break;
            default: // Stereo / identity
                for (var i = 0; i < count; i++)
                    arr[i] = i < sourceChannels ? i : ChannelMap.Silence;
                break;
        }

        return new ChannelMap(arr);
    }

    /// <summary>
    /// Phase C (§4.3.4) — reconfigure the channel map of an already-wired audio route without tearing
    /// the route down. Calls <see cref="AudioPlayer.Connect"/> which replaces the existing route's
    /// <c>ChannelMap</c> in-place via <c>AudioRouter.AddRoute</c>. Caller must re-apply gain immediately
    /// after — <c>AddRoute</c> resets the route's current gain to the supplied value (default 1.0),
    /// so without a follow-up <see cref="TrySetOutputGain"/> the level would jump.
    /// </summary>
    public bool TrySetOutputChannelMap(OutputLineViewModel line, AudioRouteMixMode mode, float gain,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (!_lineWiring.TryGetValue(line, out var wiring) || wiring.AudioOutputId is null)
            return true;
        if (Player.AudioRouter is null || string.IsNullOrEmpty(Player.AudioSourceId))
            return true;

        // When the matrix path owns this line's routes, the legacy single-route mix-mode reroute is a no-op.
        // The caller should be using TrySetOutputMatrix instead — silently honour both paths so VMs that
        // still poke MixMode (e.g. via "preset" buttons) don't fight the matrix.
        if (wiring.Cells.Count > 0)
            return true;

        try
        {
            var srcChannels = SourceChannelCountOrFallback(2);
            var sinkChannels = wiring.SinkChannelCount > 0 ? wiring.SinkChannelCount : GetOutputChannelCount(line.Definition);
            var map = MixModeToChannelMap(mode, srcChannels, sinkChannels);
            Player.AudioRouter!.Connect(Player.AudioSourceId!, wiring.AudioOutputId, map, gain);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Phase C (§4.3.4) — install per-cell routing. Replaces any cell routes that were previously
    /// registered for <paramref name="line"/> (idempotent rebuild). Each non-muted, above-floor cell becomes
    /// one router route via <see cref="AudioRouter.AddRoute(string,string,string,ChannelMap,float)"/> with a
    /// stable id so subsequent gain rides via <see cref="TrySetOutputMatrixCompoundGain"/> only touch
    /// <see cref="AudioRouter.SetRouteGainById"/> (click-free per-cell fades).
    /// </summary>
    /// <param name="cells">The intended matrix layout for this line. An empty / all-muted list leaves the
    /// line silent (all per-cell routes removed; the legacy single route, if any, is also dropped).</param>
    /// <param name="compoundEnvelope">Master × per-output linear gain applied on top of each cell's own gain.</param>
    public bool TrySetOutputMatrix(OutputLineViewModel line,
        IReadOnlyList<AudioMatrixCellConfig> cells, float compoundEnvelope, out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (!_lineWiring.TryGetValue(line, out var wiring) || wiring.AudioOutputId is null)
            return true; // video-only line
        if (Player.AudioRouter is null || string.IsNullOrEmpty(Player.AudioSourceId))
            return true;

        try
        {
            var router = Player.AudioRouter;
            // The legacy single route (if WireAudio's initial Connect installed one) must go — the
            // matrix owns this line's contribution now. ApplyMatrix only touches its own prefix.
            try { router.RemoveRoute(Player.AudioSourceId!, wiring.AudioOutputId); }
            catch { /* tolerate "no route" — back-compat removal sweeps */ }

            wiring.Cells.Clear();

            var srcChannels = SourceChannelCountOrFallback(0);
            wiring.SinkChannelCount = wiring.SinkChannelCount > 0
                ? wiring.SinkChannelCount
                : GetOutputChannelCount(line.Definition);

            // P5c: per-cell routes via the framework's atomic reconcile (AudioRouter.ApplyMatrix):
            // changed cells ramp click-free, new cells fade in from silence, dropped cells removed —
            // all in one router-state swap instead of the old remove-everything-re-add hard cut.
            var gains = BuildMatrixGains(cells, srcChannels, wiring.SinkChannelCount, compoundEnvelope, wiring.Cells);
            router.ApplyMatrix(Player.AudioSourceId!, wiring.AudioOutputId, gains);

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Phase C (§4.3.4) — click-free gain ride across every cell route belonging to <paramref name="line"/>.
    /// Each cell route ends up at <c>compoundEnvelope × cell.linearGain</c> via
    /// <see cref="AudioRouter.SetRouteGainById"/>. No-op when the line has no cell routes installed (caller
    /// should fall back to <see cref="TrySetOutputGain"/> in that case).
    /// </summary>
    public bool TrySetOutputMatrixCompoundGain(OutputLineViewModel line, float compoundEnvelope,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Session is disposed.";
            return false;
        }

        if (!_lineWiring.TryGetValue(line, out var wiring) || wiring.Cells.Count == 0)
            return false; // no matrix; caller picks the legacy gain path
        if (Player.AudioRouter is null || string.IsNullOrEmpty(Player.AudioSourceId) || wiring.AudioOutputId is null)
            return true;

        try
        {
            // Same reconcile as TrySetOutputMatrix: identical cell set, rescaled gains → ApplyMatrix
            // only moves each route's GainSlot.Target (click-free ramps), no add/remove churn.
            var gains = BuildMatrixGains(wiring.Cells, SourceChannelCountOrFallback(0),
                wiring.SinkChannelCount, compoundEnvelope, liveCellsOut: null);
            Player.AudioRouter.ApplyMatrix(Player.AudioSourceId!, wiring.AudioOutputId, gains);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds the linear gain matrix for <see cref="AudioRouter.ApplyMatrix"/> from cell configs:
    /// muted/floor/out-of-range cells are zero; live cells get <c>envelope × 10^(dB/20)</c>.
    /// When <paramref name="liveCellsOut"/> is non-null it receives the surviving cells (the set the
    /// compound-gain ride later rescales).
    /// </summary>
    private static float[,] BuildMatrixGains(
        IReadOnlyList<AudioMatrixCellConfig> cells,
        int srcChannels,
        int sinkChannels,
        float compoundEnvelope,
        List<AudioMatrixCellConfig>? liveCellsOut)
    {
        var gains = new float[Math.Max(0, srcChannels), Math.Max(0, sinkChannels)];
        foreach (var cell in cells)
        {
            if (cell.Muted) continue;
            if (cell.GainDb <= AudioMatrixDefaults.MutedFloorDb) continue;
            if (cell.InputChannel < 0 || cell.InputChannel >= srcChannels) continue;
            if (cell.OutputChannel < 0 || cell.OutputChannel >= sinkChannels) continue;

            var cellLinear = (float)Math.Pow(10.0, cell.GainDb / 20.0);
            gains[cell.InputChannel, cell.OutputChannel] = compoundEnvelope * cellLinear;
            liveCellsOut?.Add(cell);
        }

        return gains;
    }
}
