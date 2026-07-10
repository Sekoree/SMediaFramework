using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HaPlay.Views.Controls;

/// <summary>
/// Phase E (§8.1) - minimal line-chart for health throughput sparklines. Renders a single polyline
/// scaled to <see cref="PeakSample"/> with the leftmost sample anchored at the left edge. Designed
/// for compact, inline use in list rows; intentionally has no axes, labels, or hover affordances.
/// </summary>
public sealed class SparklineControl : Control
{
    /// <summary>Sample buffer in oldest→newest order. Re-render fires when this changes.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty =
        AvaloniaProperty.Register<SparklineControl, IReadOnlyList<double>?>(nameof(Samples));

    /// <summary>Peak value the Y axis scales against. Pass 0 (or omit) to auto-scale to
    /// <c>Samples.Max()</c>; bind to a VM-tracked peak when you want the scale to be sticky.</summary>
    public static readonly StyledProperty<double> PeakSampleProperty =
        AvaloniaProperty.Register<SparklineControl, double>(nameof(PeakSample));

    /// <summary>Line stroke (foreground). Defaults to the theme accent if unset.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(Stroke));

    /// <summary>Optional fill under the line - passes <see langword="null"/> to disable.</summary>
    public static readonly StyledProperty<IBrush?> AreaFillProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(AreaFill));

    /// <summary>Sparkline change-stamp bound from the VM. Forces a redraw without re-binding
    /// <see cref="Samples"/> (the ring buffer's content changes but the reference may not).</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<SparklineControl, int>(nameof(Revision));

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(SamplesProperty, PeakSampleProperty, StrokeProperty, AreaFillProperty, RevisionProperty);
    }

    public IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public double PeakSample
    {
        get => GetValue(PeakSampleProperty);
        set => SetValue(PeakSampleProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? AreaFill
    {
        get => GetValue(AreaFillProperty);
        set => SetValue(AreaFillProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var samples = Samples;
        if (samples is null || samples.Count < 2)
            return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
            return;

        var peak = PeakSample > 0 ? PeakSample : samples.Max();
        if (peak <= 0) peak = 1;

        var n = samples.Count;
        var stepX = w / (n - 1);
        var bottom = h - 1;

        var points = new Point[n];
        for (var i = 0; i < n; i++)
        {
            var ratio = samples[i] / peak;
            ratio = ratio < 0 ? 0 : (ratio > 1 ? 1 : ratio);
            points[i] = new Point(i * stepX, bottom - ratio * (h - 2));
        }

        var stroke = Stroke ?? new SolidColorBrush(Color.FromRgb(76, 175, 80));

        if (AreaFill is { } fill)
        {
            // Build a filled area: polyline → bottom-right → bottom-left → close.
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(points[0], isFilled: true);
                for (var i = 1; i < n; i++)
                    g.LineTo(points[i]);
                g.LineTo(new Point(points[n - 1].X, bottom));
                g.LineTo(new Point(points[0].X, bottom));
                g.EndFigure(isClosed: true);
            }
            ctx.DrawGeometry(fill, null, geo);
        }

        var pen = new Pen(stroke, 1.25);
        for (var i = 1; i < n; i++)
            ctx.DrawLine(pen, points[i - 1], points[i]);
    }
}
