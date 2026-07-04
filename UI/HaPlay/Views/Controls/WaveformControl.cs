using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HaPlay.Views.Controls;

/// <summary>
/// Renders a low-resolution waveform overview as vertical bars.
/// <see cref="Peaks"/> is an array of normalized values (0..1).
/// </summary>
public sealed class WaveformControl : Control
{
    public static readonly StyledProperty<float[]?> PeaksProperty =
        AvaloniaProperty.Register<WaveformControl, float[]?>(nameof(Peaks));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<WaveformControl, int>(nameof(Revision));

    static WaveformControl()
    {
        AffectsRender<WaveformControl>(PeaksProperty, RevisionProperty);
    }

    public float[]? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    // Translucent dark — the waveform draws directly on the Classic theme's light-gray chrome
    // (the old solid white came from the dark-Fluent era and was invisible there).
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.Parse("#66000000"));

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var peaks = Peaks;
        if (peaks is null || peaks.Length == 0)
            return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var n = peaks.Length;
        var barW = w / n;
        if (barW < 0.5) barW = 0.5;
        var mid = h / 2;

        for (var i = 0; i < n; i++)
        {
            var peak = Math.Clamp(peaks[i], 0, 1);
            if (peak <= 0.01f) continue;

            var barH = peak * mid;
            var x = i * w / n;
            var rect = new Rect(x, mid - barH, barW, barH * 2);
            ctx.DrawRectangle(BarBrush, null, rect);
        }
    }
}
