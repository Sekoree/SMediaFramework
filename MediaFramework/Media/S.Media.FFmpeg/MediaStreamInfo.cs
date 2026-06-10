using System.Runtime.InteropServices;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

public enum MediaStreamKind
{
    Audio,
    Video,
    Subtitle,
    Attachment,
    Data,
}

/// <summary>Reserved stream-index values for <c>AudioStreamIndex</c>/<c>VideoStreamIndex</c> open options.</summary>
public static class MediaStreamSelection
{
    /// <summary>
    /// Disables the side entirely: no packets are demuxed for it and no decoder is opened. For video this
    /// makes a video file behave like an audio-only file (stub video source) at zero video-decode cost.
    /// </summary>
    public const int Disabled = -1;
}

/// <summary>
/// Container stream description read from probe metadata (no decoding). <see cref="Index"/> is the
/// container stream index usable as an explicit <c>AudioStreamIndex</c>/<c>VideoStreamIndex</c> open option.
/// </summary>
public sealed record MediaStreamInfo(
    int Index,
    MediaStreamKind Kind,
    string CodecName,
    string? Language,
    string? Title,
    int Channels,
    int SampleRate,
    int Width,
    int Height,
    Rational FrameRate,
    bool IsDefault,
    bool IsForced,
    bool IsAttachedPicture,
    bool IsDecodable)
{
    /// <summary>Operator-facing one-line label for track pickers.</summary>
    public string ToDisplayString()
    {
        var detail = Kind switch
        {
            MediaStreamKind.Audio when Channels > 0 => $" {Channels}ch {SampleRate} Hz",
            MediaStreamKind.Video when Width > 0 => $" {Width}×{Height}",
            _ => "",
        };
        var lang = string.IsNullOrEmpty(Language) ? "" : $" [{Language}]";
        var title = string.IsNullOrEmpty(Title) ? "" : $" “{Title}”";
        var flags = (IsAttachedPicture, IsDefault, IsDecodable) switch
        {
            (true, _, _) => " (cover art)",
            (_, true, _) => " (default)",
            (_, _, false) => " (no decoder)",
            _ => "",
        };
        return $"#{Index} {CodecName}{detail}{lang}{title}{flags}";
    }

    /// <summary>
    /// Stable identity of the track's content independent of its index — used by hosts to detect that a
    /// persisted explicit index no longer points at the same track after a re-mux (and fall back to auto).
    /// </summary>
    public string ContentSignature => $"{Kind}:{CodecName}:{Language ?? ""}:{Channels}:{SampleRate}:{Width}x{Height}";
}

/// <summary>Probes a media file's stream table without building a decoder.</summary>
public static unsafe class MediaStreamProbe
{
    /// <summary>Opens <paramref name="path"/> just far enough to enumerate streams, then closes it.</summary>
    public static MediaStreamInfo[] ProbeFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("media file not found", path);

        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* fmt = null;
        var ret = avformat_open_input(&fmt, path, null, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_open_input));
        try
        {
            ret = avformat_find_stream_info(fmt, null);
            FFmpegException.ThrowIfError(ret, nameof(avformat_find_stream_info));
            return ReadAll(fmt);
        }
        finally
        {
            avformat_close_input(&fmt);
        }
    }

    internal static MediaStreamInfo[] ReadAll(AVFormatContext* fmt)
    {
        var result = new MediaStreamInfo[fmt->nb_streams];
        for (var i = 0; i < fmt->nb_streams; i++)
        {
            var st = fmt->streams[i];
            var cp = st->codecpar;
            var kind = cp->codec_type switch
            {
                AVMediaType.AVMEDIA_TYPE_AUDIO => MediaStreamKind.Audio,
                AVMediaType.AVMEDIA_TYPE_VIDEO => MediaStreamKind.Video,
                AVMediaType.AVMEDIA_TYPE_SUBTITLE => MediaStreamKind.Subtitle,
                AVMediaType.AVMEDIA_TYPE_ATTACHMENT => MediaStreamKind.Attachment,
                _ => MediaStreamKind.Data,
            };

            var fps = st->avg_frame_rate;
            if (fps.num <= 0 || fps.den <= 0) fps = st->r_frame_rate;
            var frameRate = fps.num > 0 && fps.den > 0 ? new Rational(fps.num, fps.den) : new Rational(0, 1);

            result[i] = new MediaStreamInfo(
                i,
                kind,
                avcodec_get_name(cp->codec_id) ?? "unknown",
                GetMetadata(st->metadata, "language"),
                GetMetadata(st->metadata, "title"),
                cp->ch_layout.nb_channels,
                cp->sample_rate,
                cp->width,
                cp->height,
                frameRate,
                (st->disposition & AV_DISPOSITION_DEFAULT) != 0,
                (st->disposition & AV_DISPOSITION_FORCED) != 0,
                (st->disposition & AV_DISPOSITION_ATTACHED_PIC) != 0,
                avcodec_find_decoder(cp->codec_id) != null);
        }

        return result;
    }

    private static string? GetMetadata(AVDictionary* dict, string key)
    {
        var entry = av_dict_get(dict, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((IntPtr)entry->value);
    }
}
