using S.Media.Core.Video;
using S.Media.Effects;

namespace HaPlay.Playback;

internal static class OutputPresetFormats
{
    private static readonly Rational Rate60 = new(60, 1);

    public static bool TryResolve(
        PlayerOutputPreset preset,
        Rational sourceFrameRate,
        out VideoFormat target,
        int customWidth = 0,
        int customHeight = 0)
    {
        var rate = sourceFrameRate.Denominator > 0 && sourceFrameRate.Numerator > 0
            ? sourceFrameRate
            : Rate60;

        switch (preset)
        {
            case PlayerOutputPreset.Preset1080p60:
                target = new VideoFormat(1920, 1080, PixelFormat.Bgra32, Rate60);
                return true;
            case PlayerOutputPreset.Preset720p60:
                target = new VideoFormat(1280, 720, PixelFormat.Bgra32, Rate60);
                return true;
            case PlayerOutputPreset.Custom:
                if (customWidth >= 16 && customHeight >= 16)
                {
                    target = new VideoFormat(customWidth, customHeight, PixelFormat.Bgra32, rate);
                    return true;
                }
                target = default;
                return false;
            default:
                target = default;
                return false;
        }
    }

    public static LayerTransform2D LetterboxTransform(VideoFormat source, VideoFormat target) =>
        LayerConfig.Background.ToTransform(source, target);
}
