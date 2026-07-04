using S.Media.Core.Video;

namespace S.Media.Compositor;

internal static class LayerConfigResolver
{
    public static LayerTransform2D ToTransform(LayerConfig config, VideoFormat source, VideoFormat canvas)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return LayerTransform2D.Identity;

        var baseScale = ReferenceEquals(config.Position, LayerPosition.Center)
            ? Math.Min((float)canvas.Width / source.Width, (float)canvas.Height / source.Height)
            : Math.Max((float)canvas.Width / source.Width, (float)canvas.Height / source.Height);

        var scale = baseScale * Math.Max(0f, config.Scale);
        var scaledW = source.Width * scale;
        var scaledH = source.Height * scale;

        var (tx, ty) = config.Position switch
        {
            LayerPosition.AbsolutePixelsPosition abs => (abs.X, abs.Y),
            LayerPosition.NormalizedPosition norm => (
                norm.X01 * canvas.Width,
                norm.Y01 * canvas.Height),
            LayerPosition.AnchoredPosition anc => AnchorOffset(anc.Anchor, anc.MarginX, anc.MarginY, canvas, scaledW, scaledH),
            _ => (
                (canvas.Width - scaledW) * 0.5f,
                (canvas.Height - scaledH) * 0.5f),
        };

        var transform = LayerTransform2D.Compose(
            LayerTransform2D.Translate(tx, ty),
            LayerTransform2D.Scale(scale, scale));

        if (Math.Abs(config.Rotation) > 1e-6f)
            transform = LayerTransform2D.Compose(transform, LayerTransform2D.Rotate(config.Rotation));

        return transform;
    }

    /// <summary>Resolves config + transitions, then applies in-progress <see cref="Transition.MoveToTransition"/> via transform lerp.</summary>
    public static LayerTransform2D ResolveTransform(
        LayerConfig baseConfig,
        IReadOnlyList<ScheduledTransition> transitions,
        TimeSpan timeline,
        VideoFormat source,
        VideoFormat canvas)
    {
        foreach (var st in transitions)
        {
            if (st.Transition is not Transition.MoveToTransition move)
                continue;
            if (timeline < st.At)
                continue;
            var elapsed = timeline - st.At;
            if (elapsed >= move.Duration)
                continue;

            var t = Progress(elapsed, move.Duration, move.Easing);
            var atConfig = ResolveAt(baseConfig, transitions, st.At);
            var start = ToTransform(atConfig, source, canvas);
            var end = ToTransform(atConfig with { Position = move.Position }, source, canvas);
            return LerpTransform(start, end, t);
        }

        return ToTransform(ResolveAt(baseConfig, transitions, timeline), source, canvas);
    }

    private static LayerTransform2D LerpTransform(LayerTransform2D a, LayerTransform2D b, float t) => new(
        a.M11 + (b.M11 - a.M11) * t,
        a.M12 + (b.M12 - a.M12) * t,
        a.Tx + (b.Tx - a.Tx) * t,
        a.M21 + (b.M21 - a.M21) * t,
        a.M22 + (b.M22 - a.M22) * t,
        a.Ty + (b.Ty - a.Ty) * t);

    private static (float X, float Y) AnchorOffset(
        LayerAnchor anchor,
        float marginX,
        float marginY,
        VideoFormat canvas,
        float scaledW,
        float scaledH)
    {
        return anchor switch
        {
            LayerAnchor.TopLeft => (marginX, marginY),
            LayerAnchor.TopCenter => ((canvas.Width - scaledW) * 0.5f + marginX, marginY),
            LayerAnchor.TopRight => (canvas.Width - scaledW - marginX, marginY),
            LayerAnchor.CenterLeft => (marginX, (canvas.Height - scaledH) * 0.5f + marginY),
            LayerAnchor.Center => (
                (canvas.Width - scaledW) * 0.5f + marginX,
                (canvas.Height - scaledH) * 0.5f + marginY),
            LayerAnchor.CenterRight => (
                canvas.Width - scaledW - marginX,
                (canvas.Height - scaledH) * 0.5f + marginY),
            LayerAnchor.BottomLeft => (marginX, canvas.Height - scaledH - marginY),
            LayerAnchor.BottomCenter => (
                (canvas.Width - scaledW) * 0.5f + marginX,
                canvas.Height - scaledH - marginY),
            LayerAnchor.BottomRight => (
                canvas.Width - scaledW - marginX,
                canvas.Height - scaledH - marginY),
            _ => (marginX, marginY),
        };
    }

    public static LayerConfig ResolveAt(
        LayerConfig baseConfig,
        IReadOnlyList<ScheduledTransition> transitions,
        TimeSpan timeline)
    {
        if (transitions.Count == 0)
            return baseConfig;

        var config = baseConfig;
        foreach (var t in transitions)
        {
            if (timeline < t.At)
                continue;
            config = ApplyTransition(config, t.Transition, timeline - t.At);
        }

        return config;
    }

    private static LayerConfig ApplyTransition(LayerConfig current, Transition transition, TimeSpan elapsed)
    {
        switch (transition)
        {
            case Transition.CutTransition cut:
                return cut.Config;
            case Transition.FadeToTransition fade:
            {
                var tween = new LayerOpacityTween(current.Opacity, fade.Opacity, fade.Duration, fade.Easing);
                var opacity = tween.OpacityAt(elapsed);
                return current with { Opacity = opacity };
            }
            case Transition.MoveToTransition move:
                return elapsed >= move.Duration
                    ? current with { Position = move.Position }
                    : current;
            case Transition.ScaleToTransition scale:
            {
                var tween = new LayerOpacityTween(current.Scale, scale.Scale, scale.Duration, scale.Easing);
                var s = tween.OpacityAt(elapsed);
                return current with { Scale = s };
            }
            case Transition.ComboTransition combo:
            {
                var merged = current;
                foreach (var child in combo.Items)
                    merged = ApplyTransition(merged, child, elapsed);
                return merged;
            }
            case Transition.SequenceTransition seq:
            {
                var offset = TimeSpan.Zero;
                var merged = current;
                foreach (var child in seq.Items)
                {
                    if (elapsed < offset)
                        break;
                    merged = ApplyTransition(merged, child, elapsed - offset);
                    offset += DurationOf(child);
                }
                return merged;
            }
            default:
                return current;
        }
    }

    private static float Progress(TimeSpan elapsed, TimeSpan duration, LayerEasing easing)
    {
        if (duration <= TimeSpan.Zero) return 1f;
        if (elapsed >= duration) return 1f;
        if (elapsed <= TimeSpan.Zero) return 0f;
        var t = (float)(elapsed.TotalSeconds / duration.TotalSeconds);
        return LayerOpacityTween.Ease(t, easing);
    }

    private static TimeSpan DurationOf(Transition t) => t switch
    {
        Transition.FadeToTransition f => f.Duration,
        Transition.MoveToTransition m => m.Duration,
        Transition.ScaleToTransition s => s.Duration,
        Transition.SequenceTransition seq => seq.Items.Aggregate(TimeSpan.Zero, (a, c) => a + DurationOf(c)),
        _ => TimeSpan.Zero,
    };
}

internal readonly record struct ScheduledTransition(TimeSpan At, Transition Transition);
