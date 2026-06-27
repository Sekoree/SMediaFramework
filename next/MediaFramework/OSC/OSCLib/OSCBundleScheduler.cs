namespace OSCLib;

internal static class OSCBundleScheduler
{
    public static TimeSpan GetDelay(OSCTimeTag timeTag, DateTimeOffset nowUtc)
    {
        if (timeTag.IsImmediately)
            return TimeSpan.Zero;

        var dueUtc = timeTag.ToDateTimeOffset();
        var delay = dueUtc - nowUtc.ToUniversalTime();
        return delay <= TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public static async ValueTask DelayUntilDueAsync(
        OSCTimeTag timeTag,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var delay = GetDelay(timeTag, nowUtc);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
