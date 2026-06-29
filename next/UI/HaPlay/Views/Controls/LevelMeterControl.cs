using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HaPlay.Views.Controls;

/// <summary>
/// Horizontal peak level meter. <see cref="Level"/> is 0..1 (normalized).
/// Bar is green below -6 dB (~0.75), yellow from -6 to 0 dB, red above 0 dB.
/// </summary>
public sealed class LevelMeterControl : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeterControl, double>(nameof(Level));

    static LevelMeterControl()
    {
        AffectsRender<LevelMeterControl>(LevelProperty);
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#22808080"));
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush YellowBrush = new SolidColorBrush(Color.Parse("#FFC107"));
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#F44336"));

    // -6 dB threshold in normalized space: (-6 + 60) / 72 ≈ 0.75
    private const double YellowThreshold = 0.75;
    // 0 dB threshold in normalized space: (0 + 60) / 72 ≈ 0.833
    private const double RedThreshold = 0.833;

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var rect = new Rect(0, 0, w, h);
        ctx.DrawRectangle(BackgroundBrush, null, rect, 2, 2);

        var level = Math.Clamp(Level, 0, 1);
        if (level <= 0) return;

        var fillW = level * w;

        IBrush brush;
        if (level >= RedThreshold) brush = RedBrush;
        else if (level >= YellowThreshold) brush = YellowBrush;
        else brush = GreenBrush;

        var fillRect = new Rect(0, 0, fillW, h);
        ctx.DrawRectangle(brush, null, fillRect, 2, 2);
    }
}
