using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HaPlay.ViewModels;

/// <summary>Phase E (§8.1) - converter that maps the aggregate-health flag to a chip background brush.
/// Green when everything's healthy (or idle), red when any line is warn/error. Kept as a one-way
/// converter so the chip stays declarative in XAML.</summary>
public sealed class AggregateBrushConverter : IValueConverter
{
    public static readonly AggregateBrushConverter Instance = new();

    private static readonly SolidColorBrush HealthyBrush = new(Color.FromArgb(0x66, 0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush IssueBrush = new(Color.FromArgb(0x99, 0xE5, 0x39, 0x35));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool issues && issues)
            return IssueBrush;
        return HealthyBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
