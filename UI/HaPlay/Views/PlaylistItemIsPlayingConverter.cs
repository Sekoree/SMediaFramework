using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HaPlay.Models;

namespace HaPlay.Views;

/// <summary>MultiBinding converter for the "now playing" playlist row: returns an accent border brush when
/// the row's item is the one loaded in the deck, else a transparent brush (so the row keeps its layout).
/// Compares by <see cref="PlaylistItem.Id"/> so it survives a record <c>with</c> copy (properties /
/// audio-track edits keep the Id) and never false-matches two same-path items (each carries a distinct
/// Id). Bind [ row item, <c>CurrentPlayingItem</c> ].</summary>
public sealed class PlaylistItemIsPlayingConverter : IMultiValueConverter
{
    public static readonly PlaylistItemIsPlayingConverter Instance = new();

    // A "live/playing" green that reads the same in the Classic light theme and reversed dark variant.
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(0x2E, 0x9E, 0x4B));

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2
        && values[0] is PlaylistItem row
        && values[1] is PlaylistItem playing
        && row.Id == playing.Id
            ? Accent
            : Brushes.Transparent;
}
