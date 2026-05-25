using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HaPlay.ViewModels;

/// <summary>
/// Phase E (§8.1) — small one-way converter that takes a hex color string (e.g. <c>"#4CAF50"</c>)
/// and yields an <see cref="IBrush"/>. The line VM's <c>HealthColor</c> is exposed as a string so the
/// XAML can also bind it to <c>Fill</c> on an <c>Ellipse</c>; this converter lets the sparkline reuse
/// the same source as its stroke / area fill without having a duplicate brush-typed property.
/// </summary>
public sealed class BrushFromHexConverter : IValueConverter
{
    public static readonly BrushFromHexConverter Instance = new(opacity: 1.0);

    /// <summary>Translucent variant used as the sparkline area fill so the line on top stays legible.</summary>
    public static readonly BrushFromHexConverter AreaInstance = new(opacity: 0.18);

    private readonly double _opacity;

    private BrushFromHexConverter(double opacity)
    {
        _opacity = opacity;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrEmpty(hex))
            return null;
        if (!Color.TryParse(hex, out var color))
            return null;
        if (_opacity < 1.0)
            color = new Color((byte)(color.A * _opacity), color.R, color.G, color.B);
        return new SolidColorBrush(color);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
