using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

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

    public WaveformControl()
    {
        // The bar colour flips with the theme, so re-render when the resolved variant changes (theme toggle).
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
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

    // The waveform draws directly on the scrubber chrome, so it must contrast with the theme background:
    // translucent dark on light themes, translucent light on dark themes. A single hardcoded colour is
    // invisible on one or the other (the bars used to vanish entirely on dark themes).
    private static readonly IBrush LightThemeBars = new ImmutableSolidColorBrush(Color.Parse("#66000000"));
    private static readonly IBrush DarkThemeBars = new ImmutableSolidColorBrush(Color.Parse("#B3FFFFFF"));

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        var peaks = Peaks;
        if (peaks is null || peaks.Length == 0)
            return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var barBrush = ActualThemeVariant == ThemeVariant.Dark ? DarkThemeBars : LightThemeBars;
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
            ctx.DrawRectangle(barBrush, null, rect);
        }
    }
}
