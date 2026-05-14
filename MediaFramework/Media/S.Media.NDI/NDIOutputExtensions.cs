namespace S.Media.NDI;

/// <summary>Optional wait helpers for <see cref="NDIOutput"/>.</summary>
public static class NDIOutputExtensions
{
    /// <summary>Default upper bound for <paramref name="maxWaitMs"/> (matches historical smoke tooling).</summary>
    public const int DefaultMaxWaitFirstReceiverMs = 60_000;

    /// <summary>Blocks until an NDI receiver attaches or the SDK times out.</summary>
    public static void WaitForFirstReceiverIfRequested(
        this NDIOutput? ndi,
        int maxWaitMs,
        Action<string>? logStdErr,
        Action<string>? logStdOut,
        int maxWaitCapMs = DefaultMaxWaitFirstReceiverMs)
    {
        if (ndi is null || maxWaitMs <= 0)
            return;
        maxWaitMs = Math.Clamp(maxWaitMs, 0, maxWaitCapMs);
        var ms = (uint)maxWaitMs;
        var n = ndi.GetReceiverConnectionCount(ms);
        if (n < 1)
            logStdErr?.Invoke($"[ndi] no receiver within {ms} ms — continuing (Monitor may still connect).");
        else
            logStdOut?.Invoke($"[ndi] {n} receiver(s) connected (wait up to {ms} ms).");
    }
}
