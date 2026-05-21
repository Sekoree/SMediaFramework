using NDILib;

namespace S.Media.NDI;

/// <summary>Maps NDI frame timecode/timestamp fields to presentation timelines.</summary>
internal static class NDIFrameTiming
{
    public static bool TryGetFrameStartTicks(long timecode100Ns, long timestamp100Ns, out long startTicks)
    {
        if (timecode100Ns != NDIConstants.TimecodeSynthesize)
        {
            startTicks = timecode100Ns;
            return true;
        }

        if (timestamp100Ns != NDIConstants.TimestampUndefined)
        {
            startTicks = timestamp100Ns;
            return true;
        }

        startTicks = 0;
        return false;
    }

    public static bool TryMapPresentationTime(
        long timecode100Ns,
        long timestamp100Ns,
        ref long sessionOriginTicks,
        ref bool sessionOriginSet,
        out TimeSpan presentationTime)
    {
        presentationTime = TimeSpan.Zero;
        if (!TryGetFrameStartTicks(timecode100Ns, timestamp100Ns, out var startTicks))
            return false;

        if (!sessionOriginSet)
        {
            sessionOriginTicks = startTicks;
            sessionOriginSet = true;
        }

        var delta = startTicks - sessionOriginTicks;
        presentationTime = delta < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(delta);
        return true;
    }

    public static long FrameDurationTicks(int frameRateN, int frameRateD)
    {
        if (frameRateN <= 0 || frameRateD <= 0)
            return TimeSpan.FromMilliseconds(33).Ticks;
        return (long)Math.Round(frameRateD * (double)TimeSpan.TicksPerSecond / frameRateN);
    }
}
