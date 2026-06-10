using System.Collections.Immutable;

namespace S.Media.Core.Audio;

public sealed partial class AudioRouter
{
    /// <summary>
    /// Installs (or reconciles) a full gain matrix between <paramref name="sourceId"/> and
    /// <paramref name="outputId"/> as one route per non-zero cell, atomically.
    /// </summary>
    /// <param name="gains">
    /// Cell gains indexed <c>[sourceChannel, outputChannel]</c>. Dimensions must not exceed the
    /// source's / output's channel counts; a smaller matrix leaves the remaining channels unrouted
    /// (hosts often size matrices from their UI model rather than the negotiated formats).
    /// A zero cell means "no route".
    /// </param>
    /// <param name="routeIdPrefix">
    /// Namespace for the per-cell route ids (<c>{prefix}#{src}:{dst}</c>). Defaults to a prefix
    /// derived from the pair, so repeated calls reconcile the same route set. Pass an explicit
    /// prefix to keep several independent matrices between the same pair.
    /// </param>
    /// <remarks>
    /// <para>
    /// Reconciliation semantics per cell, applied in a single state swap under the router lock:
    /// an existing cell route whose gain changed fades to the new value over the next chunk
    /// (<see cref="RouteGainSlot"/> ramp, same as <see cref="SetRouteGain"/>); a newly non-zero
    /// cell fades in from silence; a cell that became zero is removed (hard cut — set a small gain
    /// instead when you need an audible fade-out first). Routes outside
    /// <paramref name="routeIdPrefix"/> are never touched, so a matrix can coexist with manually
    /// registered routes for the same pair.
    /// </para>
    /// <para>
    /// Standard layout matrices (identity, ITU-R BS.775 downmixes) come from
    /// <see cref="AudioChannelLayoutPresets"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Unknown ids, dimension mismatch, or a route id collision:
    /// a route under <paramref name="routeIdPrefix"/> already exists for a different source/output pair.</exception>
    public void ApplyMatrix(string sourceId, string outputId, float[,] gains, string? routeIdPrefix = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        ArgumentNullException.ThrowIfNull(gains);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_state.Sources.TryGetValue(sourceId, out var src))
                throw new ArgumentException($"unknown source ID '{sourceId}'", nameof(sourceId));
            if (!_state.Outputs.TryGetValue(outputId, out var output))
                throw new ArgumentException($"unknown output ID '{outputId}'", nameof(outputId));

            var srcChannels = gains.GetLength(0);
            var dstChannels = output.Output.Format.Channels;
            if (gains.GetLength(0) > src.Source.Format.Channels || gains.GetLength(1) > dstChannels)
                throw new ArgumentException(
                    $"matrix is {gains.GetLength(0)}x{gains.GetLength(1)} but source '{sourceId}' has {src.Source.Format.Channels} channels and output '{outputId}' has {dstChannels}",
                    nameof(gains));

            var prefix = (routeIdPrefix ?? DefaultMatrixRouteIdPrefix(sourceId, outputId)) + '#';
            var builder = _state.Routes.ToBuilder();
            var matrixDst = gains.GetLength(1);

            // Pass 1 — reconcile existing prefix-owned routes: update changed gains in place, drop
            // cells that are now zero/out of range, reject prefix collisions with foreign pairs.
            var coveredLen = Math.Max(1, srcChannels * matrixDst);
            Span<bool> covered = coveredLen <= 256
                ? stackalloc bool[coveredLen]
                : new bool[coveredLen];
            for (var i = builder.Count - 1; i >= 0; i--)
            {
                var route = builder[i].Route;
                if (!route.RouteId.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                if (route.SourceId != sourceId || route.OutputId != outputId)
                    throw new ArgumentException(
                        $"route id prefix collision: '{route.RouteId}' is registered for ('{route.SourceId}' -> '{route.OutputId}'), not ('{sourceId}' -> '{outputId}')",
                        nameof(routeIdPrefix));

                if (TryParseMatrixCellId(route.RouteId.AsSpan(prefix.Length), out var s, out var d)
                    && s < srcChannels && d < matrixDst && gains[s, d] != 0f)
                {
                    route.GainSlot.Target = gains[s, d]; // click-free ramp from the current gain
                    covered[s * matrixDst + d] = true;
                }
                else
                {
                    builder.RemoveAt(i);
                }
            }

            // Pass 2 — add routes for non-zero cells that had none; fade in from silence.
            for (var s = 0; s < gains.GetLength(0); s++)
            {
                for (var d = 0; d < gains.GetLength(1); d++)
                {
                    var gain = gains[s, d];
                    if (gain == 0f || covered[s * matrixDst + d])
                        continue;

                    var slot = new RouteGainSlot(gain) { Current = 0f };
                    var route = new AudioRoute(sourceId, outputId, $"{prefix}{s}:{d}",
                        SingleCellMap(s, d, dstChannels), gain, slot);
                    builder.Add(new ResolvedRoute(route, src, output));
                }
            }

            Volatile.Write(ref _state, _state with { Routes = builder.ToImmutable() });
        }
    }

    /// <summary>
    /// Removes every route installed by <see cref="ApplyMatrix"/> for the pair (one atomic swap).
    /// Returns the number of routes removed.
    /// </summary>
    public int RemoveMatrix(string sourceId, string outputId, string? routeIdPrefix = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var prefix = (routeIdPrefix ?? DefaultMatrixRouteIdPrefix(sourceId, outputId)) + '#';
            var removed = 0;
            var builder = _state.Routes.ToBuilder();
            for (var i = builder.Count - 1; i >= 0; i--)
            {
                var route = builder[i].Route;
                if (route.RouteId.StartsWith(prefix, StringComparison.Ordinal)
                    && route.SourceId == sourceId && route.OutputId == outputId)
                {
                    builder.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0)
                Volatile.Write(ref _state, _state with { Routes = builder.ToImmutable() });
            return removed;
        }
    }

    private static string DefaultMatrixRouteIdPrefix(string sourceId, string outputId) =>
        string.Concat("matrix\u001f", sourceId, '\u001f', outputId);

    private static bool TryParseMatrixCellId(ReadOnlySpan<char> cell, out int src, out int dst)
    {
        src = 0;
        dst = 0;
        var colon = cell.IndexOf(':');
        if (colon <= 0 || colon == cell.Length - 1)
            return false;
        return int.TryParse(cell[..colon], out src) && src >= 0
               && int.TryParse(cell[(colon + 1)..], out dst) && dst >= 0;
    }

    /// <summary>Map that feeds source channel <paramref name="src"/> into output channel
    /// <paramref name="dst"/> and silences every other output channel of this route.</summary>
    private static ChannelMap SingleCellMap(int src, int dst, int dstChannels)
    {
        Span<int> outToIn = dstChannels <= 64 ? stackalloc int[dstChannels] : new int[dstChannels];
        outToIn.Fill(-1);
        outToIn[dst] = src;
        return new ChannelMap(outToIn);
    }
}
