using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Intrinsics;

namespace S.Media.Routing;

public sealed partial class AudioRouter
{
    // ---- Fused matrix mixing (perf review finding #1) ---------------------------------------
    //
    // ApplyMatrix installs one single-cell route per non-zero cell, so a dense S×D matrix costs
    // up to S*D independent full-buffer passes per chunk - each re-probing the SIMD dispatch
    // chain and striding the whole destination to touch one channel (~130 µs/chunk for a dense
    // 8×8 in the benchmark vs ~12 µs fused). The run loop therefore groups co-routed single-cell
    // routes into one dense matrix-vector pass per (source, output) pair.
    //
    // The plan is rebuilt only when the immutable route array's identity changes (control-plane
    // rate); per chunk the run loop pays one reference compare. Reconciliation/ramp semantics are
    // unchanged: per-cell RouteGainSlot Current/Target are re-read every chunk and the ramp uses
    // the exact sample-mid interpolation of ApplyRoute, so fades stay click-free and identical.

    /// <summary>Groups smaller than this always stay on the per-route path (a dense pass costs
    /// S*D multiplies per frame regardless of live cell count, so sparse groups lose).</summary>
    private const int MinFusedMatrixCells = 4;

    private sealed class FusedMatrixGroup
    {
        public required SourceEntry Source { get; init; }
        public required OutputEntry Output { get; init; }
        public required ResolvedRoute[] Cells { get; init; }
        /// <summary>Per-cell flattened gain index: <c>dst * SrcChannels + src</c>.</summary>
        public required int[] CellGainIndex { get; init; }
        public required int SrcChannels { get; init; }
        public required int DstChannels { get; init; }
        /// <summary>Reusable per-chunk scratch, length <c>SrcChannels * DstChannels</c>.</summary>
        public required float[] FromGains { get; init; }
        public required float[] ToGains { get; init; }
    }

    /// <summary>One mix-plan step: either a plain route (existing path) or a fused group.</summary>
    private readonly record struct MixPlanEntry(ResolvedRoute? Single, FusedMatrixGroup? Fused);

    // Run-loop-thread state: the cached plan and the route-array identity it was built from.
    private ImmutableArray<ResolvedRoute> _mixPlanIdentity;
    private MixPlanEntry[] _mixPlan = [];

    private void EnsureMixPlan(ImmutableArray<ResolvedRoute> routes)
    {
        if (_mixPlanIdentity == routes)
            return;

        var plan = new List<MixPlanEntry>(routes.Length);
        // Group fusable routes by (source, output) instance pair, preserving each group's
        // first-member position in the plan so additive mixing order stays close to route order.
        var groups = new Dictionary<(SourceEntry, OutputEntry), List<(ResolvedRoute Route, int GainIndex)>>();
        foreach (var resolved in routes)
        {
            var srcChannels = resolved.Source.Source.Format.Channels;
            var dstChannels = resolved.Output.Output.Format.Channels;
            if (TryGetSingleCell(resolved.Route.Map, out var srcChannel, out var dstChannel)
                && srcChannel < srcChannels
                && dstChannel < dstChannels
                && resolved.Route.Map.OutputChannels == dstChannels)
            {
                var key = (resolved.Source, resolved.Output);
                if (!groups.TryGetValue(key, out var members))
                    groups[key] = members = [];
                members.Add((resolved, dstChannel * srcChannels + srcChannel));
            }
        }

        var emitted = new HashSet<(SourceEntry, OutputEntry)>();
        foreach (var resolved in routes)
        {
            var key = (resolved.Source, resolved.Output);
            if (!groups.TryGetValue(key, out var members)
                || members.Count < MinFusedMatrixCells
                || !members.Exists(m => ReferenceEquals(m.Route, resolved)))
            {
                plan.Add(new MixPlanEntry(resolved, null));
                continue;
            }

            if (!emitted.Add(key))
                continue; // group already emitted at its first member's position

            var srcChannels = resolved.Source.Source.Format.Channels;
            var dstChannels = resolved.Output.Output.Format.Channels;
            plan.Add(new MixPlanEntry(null, new FusedMatrixGroup
            {
                Source = resolved.Source,
                Output = resolved.Output,
                Cells = members.ConvertAll(m => m.Route).ToArray(),
                CellGainIndex = members.ConvertAll(m => m.GainIndex).ToArray(),
                SrcChannels = srcChannels,
                DstChannels = dstChannels,
                FromGains = new float[srcChannels * dstChannels],
                ToGains = new float[srcChannels * dstChannels],
            }));
        }

        _mixPlan = plan.ToArray();
        _mixPlanIdentity = routes;
    }

    /// <summary>True when <paramref name="map"/> feeds exactly one output channel from one source
    /// channel (the shape <c>ApplyMatrix</c> installs per cell).</summary>
    internal static bool TryGetSingleCell(ChannelMap map, out int srcChannel, out int dstChannel)
    {
        srcChannel = -1;
        dstChannel = -1;
        var span = map.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] < 0)
                continue;
            if (dstChannel >= 0)
                return false;
            dstChannel = i;
            srcChannel = span[i];
        }

        return dstChannel >= 0;
    }

    private void ApplyFusedMatrixGroup(FusedMatrixGroup group, int samplesPerChannel)
    {
        var from = group.FromGains;
        var to = group.ToGains;
        Array.Clear(from);
        Array.Clear(to);

        var anyRamp = false;
        var anyLive = false;
        var cells = group.Cells;
        for (var i = 0; i < cells.Length; i++)
        {
            var slot = cells[i].Route.GainSlot;
            var f = slot.Current;
            var t = slot.Target;
            var index = group.CellGainIndex[i];
            // ACCUMULATE, never assign: AddRoute documents that multiple routes (distinct ids) may
            // target the same cell and their contributions sum additively - the per-route path
            // applies each separately, so the fused gather must sum their gains (ramps included).
            from[index] += f;
            to[index] += t;
            anyRamp |= f != t;
            anyLive |= f != 0f || t != 0f;
        }

        if (anyLive)
        {
            var src = group.Source.Scratch.AsSpan(0, samplesPerChannel * group.SrcChannels);
            var dst = group.Output.Pump.WorkingBuffer.AsSpan(0, samplesPerChannel * group.DstChannels);
            // Cells in a fused group never hit ApplyRoute's counters, so record the pass here or
            // route-mix profiling silently undercounts whenever fusion is active.
            var profile = ChannelRouteMixProfiling.ShouldProfileApplyRoute();
            var t0 = profile ? Stopwatch.GetTimestamp() : 0L;
            if (anyRamp)
                ApplyFusedMatrixRamp(src, group.SrcChannels, dst, group.DstChannels, from, to, samplesPerChannel);
            else
                ApplyFusedMatrixSettled(src, group.SrcChannels, dst, group.DstChannels, from, samplesPerChannel);
            if (profile)
                ChannelRouteMixProfiling.RecordFusedMatrixMix(Stopwatch.GetTimestamp() - t0);
        }

        // Advance every cell's ramp exactly like the per-route loop does.
        for (var i = 0; i < cells.Length; i++)
        {
            var slot = cells[i].Route.GainSlot;
            if (slot.Current != slot.Target)
                slot.Current = slot.Target;
        }
    }

    /// <summary>Dense settled-gain matrix mix: <c>dst[f,d] += Σ_s src[f,s] * gains[d*S+s]</c> -
    /// one pass over src and dst regardless of live cell count. Source widths 8 and 4 take a
    /// vector dot-product per output channel (one frame load, one gain-row load, horizontal sum -
    /// ~3.7x the scalar shape in the MatrixMix benchmark); other widths use the scalar loop. The
    /// vector sum reorders the additions, which is why the equivalence tests use a tolerance.</summary>
    internal static void ApplyFusedMatrixSettled(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        ReadOnlySpan<float> gains, int samplesPerChannel)
    {
        if (Vector256.IsHardwareAccelerated && srcChannels == 8)
        {
            for (var s = 0; s < samplesPerChannel; s++)
            {
                var srcVec = Vector256.Create(src.Slice(s * 8, 8));
                var dstFrame = dst.Slice(s * dstChannels, dstChannels);
                for (var d = 0; d < dstChannels; d++)
                    dstFrame[d] += Vector256.Sum(srcVec * Vector256.Create(gains.Slice(d * 8, 8)));
            }

            return;
        }

        if (Vector128.IsHardwareAccelerated && srcChannels == 4)
        {
            for (var s = 0; s < samplesPerChannel; s++)
            {
                var srcVec = Vector128.Create(src.Slice(s * 4, 4));
                var dstFrame = dst.Slice(s * dstChannels, dstChannels);
                for (var d = 0; d < dstChannels; d++)
                    dstFrame[d] += Vector128.Sum(srcVec * Vector128.Create(gains.Slice(d * 4, 4)));
            }

            return;
        }

        // Span-slicing scalar shape: benchmarked faster than direct flat indexing (the slices give
        // the JIT constant-length bounds elision on the inner dot product).
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcFrame = src.Slice(s * srcChannels, srcChannels);
            var dstFrame = dst.Slice(s * dstChannels, dstChannels);
            for (var d = 0; d < dstChannels; d++)
            {
                var row = gains.Slice(d * srcChannels, srcChannels);
                var acc = 0f;
                for (var c = 0; c < srcChannels; c++)
                    acc += srcFrame[c] * row[c];
                dstFrame[d] += acc;
            }
        }
    }

    /// <summary>Ramping variant: every cell interpolates from→to at sample-mid
    /// (<c>gain(s) = from + (to - from) * (s + 0.5) / n</c>), the same interpolation as
    /// <see cref="ApplyRoute"/>'s ramp so fused and per-route fades stay click-free and match
    /// within float rounding. Vector paths mirror <see cref="ApplyFusedMatrixSettled"/> with the
    /// gain row interpolated per frame before the dot product.</summary>
    internal static void ApplyFusedMatrixRamp(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        ReadOnlySpan<float> fromGains, ReadOnlySpan<float> toGains, int samplesPerChannel)
    {
        var invSamples = 1f / samplesPerChannel;

        if (Vector256.IsHardwareAccelerated && srcChannels == 8)
        {
            for (var s = 0; s < samplesPerChannel; s++)
            {
                var t = Vector256.Create((s + 0.5f) * invSamples);
                var srcVec = Vector256.Create(src.Slice(s * 8, 8));
                var dstFrame = dst.Slice(s * dstChannels, dstChannels);
                for (var d = 0; d < dstChannels; d++)
                {
                    var from = Vector256.Create(fromGains.Slice(d * 8, 8));
                    var gain = from + (Vector256.Create(toGains.Slice(d * 8, 8)) - from) * t;
                    dstFrame[d] += Vector256.Sum(srcVec * gain);
                }
            }

            return;
        }

        if (Vector128.IsHardwareAccelerated && srcChannels == 4)
        {
            for (var s = 0; s < samplesPerChannel; s++)
            {
                var t = Vector128.Create((s + 0.5f) * invSamples);
                var srcVec = Vector128.Create(src.Slice(s * 4, 4));
                var dstFrame = dst.Slice(s * dstChannels, dstChannels);
                for (var d = 0; d < dstChannels; d++)
                {
                    var from = Vector128.Create(fromGains.Slice(d * 4, 4));
                    var gain = from + (Vector128.Create(toGains.Slice(d * 4, 4)) - from) * t;
                    dstFrame[d] += Vector128.Sum(srcVec * gain);
                }
            }

            return;
        }

        for (var s = 0; s < samplesPerChannel; s++)
        {
            var t = (s + 0.5f) * invSamples;
            var srcFrame = src.Slice(s * srcChannels, srcChannels);
            var dstFrame = dst.Slice(s * dstChannels, dstChannels);
            for (var d = 0; d < dstChannels; d++)
            {
                var fromRow = fromGains.Slice(d * srcChannels, srcChannels);
                var toRow = toGains.Slice(d * srcChannels, srcChannels);
                var acc = 0f;
                for (var c = 0; c < srcChannels; c++)
                {
                    var from = fromRow[c];
                    acc += srcFrame[c] * (from + (toRow[c] - from) * t);
                }

                dstFrame[d] += acc;
            }
        }
    }
}
