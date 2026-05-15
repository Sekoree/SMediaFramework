using System.Globalization;
using Avalonia.Data.Converters;

namespace HaPlay.Views;

/// <summary>One-way converter that reduces a full path to just the file name. Used by the playlist row template.</summary>
public sealed class FilenameConverter : IValueConverter
{
    public static readonly FilenameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s) ? Path.GetFileName(s) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
