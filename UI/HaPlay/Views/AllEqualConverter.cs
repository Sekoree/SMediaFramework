using System.Globalization;
using Avalonia.Data.Converters;

namespace HaPlay.Views;

/// <summary>True when every bound value is equal - the working replacement for the broken
/// <c>ObjectConverters.Equal</c> + <c>ConverterParameter={Binding}</c> pattern (ConverterParameter
/// is a plain object, so a Binding there never resolves and the comparison was always false;
/// the sidebar's selected-workspace highlight silently never applied).</summary>
public sealed class AllEqualConverter : IMultiValueConverter
{
    public static readonly AllEqualConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
            return false;
        for (var i = 1; i < values.Count; i++)
            if (!Equals(values[0], values[i]))
                return false;
        return true;
    }
}
