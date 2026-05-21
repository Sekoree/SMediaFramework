using HaPlay.Models;
using S.Media.Core.Video;

namespace HaPlay.Playback;

internal static class OutputPresetFormats
{
    private static readonly Rational Rate60 = new(60, 1);

    public static bool TryResolve(PlayerOutputPreset preset, Rational sourceFrameRate, out VideoFormat target)
    {
        var rate = sourceFrameRate.Denominator > 0 && sourceFrameRate.Numerator > 0
            ? sourceFrameRate
            : Rate60;

        target = preset switch
        {
            PlayerOutputPreset.Preset1080p60 => new VideoFormat(1920, 1080, PixelFormat.Bgra32, Rate60),
            PlayerOutputPreset.Preset720p60 => new VideoFormat(1280, 720, PixelFormat.Bgra32, Rate60),
            PlayerOutputPreset.AsSource or PlayerOutputPreset.Custom => default,
            _ => default,
        };

        return preset is PlayerOutputPreset.Preset1080p60 or PlayerOutputPreset.Preset720p60;
    }

    public static LayerTransform2D LetterboxTransform(VideoFormat source, VideoFormat target)
    {
        if (source.Width <= 0 || source.Height <= 0)
            return LayerTransform2D.Identity;

        var scale = Math.Min((float)target.Width / source.Width, (float)target.Height / source.Height);
        var scaledW = source.Width * scale;
        var scaledH = source.Height * scale;
        var tx = (target.Width - scaledW) * 0.5f;
        var ty = (target.Height - scaledH) * 0.5f;
        return LayerTransform2D.Compose(
            LayerTransform2D.Translate(tx, ty),
            LayerTransform2D.Scale(scale, scale));
    }
}
