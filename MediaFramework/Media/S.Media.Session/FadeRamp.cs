namespace S.Media.Session;

/// <summary>
/// The one fade-ramp loop every session fade shares - clip fade-in, natural fade-out, stop fade, and voice
/// fade-out differ only in what a step applies (gain/opacity via their own dispatcher-marshaled closure), when
/// they are done, and what runs afterwards; the loop mechanics live here once. A step receives the ramp's
/// elapsed time and computes its own level from its own duration (the stop fade ramps several groups with
/// different durations off one clock), applies it, and returns true to end the ramp (target reached, or its
/// guard found the clip replaced/stopped). The first step runs immediately (a fade must take effect on the
/// tick it starts, not one interval later).
/// </summary>
internal static class FadeRamp
{
    /// <summary>The step rate every session fade ramps at - fine enough to be click-free, coarse enough that
    /// the short marshaled steps stay negligible dispatcher load.</summary>
    public static readonly TimeSpan DefaultStepInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Runs the ramp inline: step (immediately, then every <paramref name="stepInterval"/>) until the
    /// step reports done or <paramref name="ct"/> fires. The step closure is expected to marshal itself onto
    /// the session dispatcher (<c>InvokeAsync</c>) - the loop itself stays off it, so a long fade never parks
    /// the serial loop (NXT-18). Exceptions propagate to the caller (the awaited stop fade ODE-guards itself).</summary>
    public static async Task RunAsync(TimeSpan stepInterval, CancellationToken ct, Func<TimeSpan, Task<bool>> step)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            if (await step(sw.Elapsed).ConfigureAwait(false))
                return;
            await Task.Delay(stepInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fire-and-forget ramp: <see cref="RunAsync"/> on a worker, then - when the ramp ended on its own
    /// rather than by cancellation - <paramref name="onCompleted"/> (the fade's release/commit tail, expected to
    /// marshal itself like the step). Suppresses <c>ExecutionContext</c> flow so the dispatcher's
    /// <c>AsyncLocal</c> identity cannot leak into the worker (a leaked identity would make the step's
    /// <c>InvokeAsync</c> run inline off the real loop and race transport commands - NXT-22). Cancellation and
    /// step/completion failures are swallowed: a fade hiccup must never crash the session.</summary>
    public static void Start(
        TimeSpan stepInterval,
        CancellationToken ct,
        Func<TimeSpan, Task<bool>> step,
        Func<Task>? onCompleted = null)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunAsync(stepInterval, ct, step).ConfigureAwait(false);
                        if (onCompleted is not null && !ct.IsCancellationRequested)
                            await onCompleted().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch { /* best-effort - a fade hiccup must never crash the session */ }
                },
                ct);
        }
    }

    /// <summary>The linear down-ramp level for <paramref name="elapsed"/> of <paramref name="duration"/>:
    /// 1 → 0, clamped.</summary>
    public static float LevelDown(TimeSpan elapsed, TimeSpan duration) =>
        (float)Math.Clamp(1d - elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);

    /// <summary>The linear up-ramp level for <paramref name="elapsed"/> of <paramref name="duration"/>:
    /// 0 → 1, clamped (exactly 1 once past the duration).</summary>
    public static float LevelUp(TimeSpan elapsed, TimeSpan duration) =>
        elapsed >= duration
            ? 1f
            : (float)Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);
}
