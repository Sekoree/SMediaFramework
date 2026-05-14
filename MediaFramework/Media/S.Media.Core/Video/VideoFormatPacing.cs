namespace S.Media.Core.Video;

/// <summary>Wall-clock pacing helpers for video sinks (NDI throttling, etc.).</summary>
public static class VideoFormatPacing
{
    /// <summary>Wall throttle slightly below one frame period (e.g. NDI video pacing).</summary>
    public static TimeSpan PaceBelowFramePeriod(VideoFormat fmt)
    {
        var fps = fmt.FrameRate.ToDouble();
        if (fps <= 0 || double.IsNaN(fps)) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(1.0 / fps * 0.93);
    }
}
