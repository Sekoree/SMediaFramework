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

    /// <summary>
    /// Maps an NDI frame's egress timecode/timestamp to an <strong>absolute</strong> presentation time (the
    /// raw 100&nbsp;ns value as a <see cref="TimeSpan"/>), with no per-receiver session origin or rebase.
    /// Unlike <see cref="TryMapPresentationTime"/> (which is relative to the first frame this receiver saw),
    /// this resolves the <em>same</em> frame to the <em>same</em> time on every receiver of one sender — so,
    /// driven by a shared/synced reference clock, multiple receivers present a stitched wall in lock-step.
    /// Returns <c>false</c> when the frame carries neither a real timecode nor a timestamp (caller should
    /// continue a synthetic timeline).
    /// </summary>
    public static bool TryGetAbsolutePresentationTime(long timecode100Ns, long timestamp100Ns, out TimeSpan presentationTime)
    {
        if (TryGetFrameStartTicks(timecode100Ns, timestamp100Ns, out var startTicks) && startTicks >= 0)
        {
            presentationTime = TimeSpan.FromTicks(startTicks);
            return true;
        }

        presentationTime = TimeSpan.Zero;
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
