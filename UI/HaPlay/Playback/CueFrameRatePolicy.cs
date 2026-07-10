using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>Helpers for comparing probed source frame rates against composition canvas rates
/// (Phase 5.9.2).</summary>
internal static class CueFrameRatePolicy
{
    private const double FpsTolerance = 0.05;

    public static bool IsKnown(Rational rate) =>
        rate.Numerator > 0 && rate.Denominator > 0;

    public static bool IsKnown(int num, int den) =>
        num > 0 && den > 0;

    public static double ToFps(Rational rate) =>
        rate.Numerator / (double)rate.Denominator;

    public static double ToFps(int num, int den) =>
        num / (double)den;

    /// <summary>Returns true when the source and canvas rates are not simple integer multiples
    /// of each other - visible pulldown / frame-drop risk on long runs.</summary>
    public static bool RatesMismatch(Rational source, Rational canvas)
    {
        if (!IsKnown(source) || !IsKnown(canvas))
            return false;

        var sourceFps = ToFps(source);
        var canvasFps = ToFps(canvas);
        if (sourceFps <= 0 || canvasFps <= 0)
            return false;

        if (IsNearMultiple(sourceFps, canvasFps))
            return false;
        return !IsNearMultiple(canvasFps, sourceFps);
    }

    public static bool RatesMismatch(int sourceNum, int sourceDen, int canvasNum, int canvasDen) =>
        RatesMismatch(new Rational(sourceNum, sourceDen), new Rational(canvasNum, canvasDen));

    private static bool IsNearMultiple(double higher, double lower)
    {
        if (lower <= 0)
            return false;
        var ratio = higher / lower;
        var nearest = Math.Round(ratio);
        if (nearest < 1)
            return false;
        return Math.Abs(nearest * lower - higher) <= FpsTolerance;
    }
}
