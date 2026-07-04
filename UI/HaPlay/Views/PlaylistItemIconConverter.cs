using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HaPlay.Models;

namespace HaPlay.Views;

/// <summary>Maps a playlist item to its kind icon (<see cref="AppIcons"/>) for the playlist row —
/// replaces the per-kind emoji <c>KindGlyph</c> that rendered as tofu without a color-emoji font.</summary>
public sealed class PlaylistItemIconConverter : IValueConverter
{
    public static readonly PlaylistItemIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FilePlaylistItem => AppIcons.VideoClip,
        ImagePlaylistItem => AppIcons.Image,
        SubtitlePlaylistItem => AppIcons.Speech,
        TextPlaylistItem => AppIcons.TextCard,
        MMDPlaylistItem => AppIcons.Person,
        YouTubePlaylistItem => AppIcons.Play,
        NDIInputPlaylistItem => AppIcons.Antenna,
        PortAudioInputPlaylistItem => AppIcons.Microphone,
        _ => AppIcons.VideoClip,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
