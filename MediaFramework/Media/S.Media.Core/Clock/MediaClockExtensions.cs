namespace S.Media.Core.Clock;

/// <summary>
/// Convenience for <see cref="IMediaClock.SetMaster"/> with a <see cref="CompositePlaybackClock"/>.
/// </summary>
/// <remarks>
/// <para>
/// Higher <see cref="PlaybackClockCandidate.Priority"/> wins when several clocks report
/// <see cref="IPlaybackClock.IsAdvancing"/> at once; equal priorities use registration order
/// (earlier <see cref="SetMasterChain"/> argument wins). For a stable playhead, prefer a single
/// advancing primary (e.g. hardware audio) and keep fallbacks non-advancing until the primary stops.
/// </para>
/// <para>
/// If the active leaf clock changes while multiple candidates advance, composite
/// <see cref="IPlaybackClock.ElapsedSinceStart"/> can jump; re-anchor with <see cref="IMediaClock.SetMaster"/>
/// or <see cref="IMediaClock.Seek"/> if you intentionally hand off between clocks. Optional
/// <see cref="SetMasterChain(IMediaClock, TimeSpan, TimeSpan, PlaybackClockCandidate[])"/> combine handoff and co-advance smoothing (see <see cref="CompositePlaybackClockBlend"/>).
/// </para>
/// <para>
/// This API selects which <see cref="IPlaybackClock"/> drives <see cref="MediaClock"/>; it does not implement
/// graph-wide coordinated master PPM or synchronized multi-output drop/repeat — see <see cref="MediaClock"/> and
/// <see cref="S.Media.Core.Audio.AudioRouter"/>.
/// </para>
/// </remarks>
public static class MediaClockExtensions
{
    /// <summary>
    /// Attaches a <see cref="CompositePlaybackClock"/> built from <paramref name="candidates"/>.
    /// Call with no arguments to detach (same as <see cref="IMediaClock.SetMaster"/> with <c>null</c>).
    /// </summary>
    public static void SetMasterChain(this IMediaClock clock, params PlaybackClockCandidate[] candidates) =>
        clock.SetMaster(candidates.Length == 0 ? null : new CompositePlaybackClock(candidates));

    /// <summary>
    /// Attaches a <see cref="CompositePlaybackClock"/> with optional handoff crossfade when the advancing winner changes.
    /// When <paramref name="masterHandoffCrossFade"/> is not positive, behaviour matches <see cref="SetMasterChain(IMediaClock, PlaybackClockCandidate[])"/>.
    /// </summary>
    public static void SetMasterChain(this IMediaClock clock, TimeSpan masterHandoffCrossFade, params PlaybackClockCandidate[] candidates)
    {
        if (candidates.Length == 0)
        {
            clock.SetMaster(null);
            return;
        }

        var blend = masterHandoffCrossFade > TimeSpan.Zero
            ? new CompositePlaybackClockBlend { HandoffCrossFade = masterHandoffCrossFade }
            : CompositePlaybackClockBlend.Disabled;
        clock.SetMaster(new CompositePlaybackClock(blend, candidates));
    }

    /// <summary>
    /// Attaches a <see cref="CompositePlaybackClock"/> with optional <see cref="CompositePlaybackClockBlend.HandoffCrossFade"/>
    /// and/or <see cref="CompositePlaybackClockBlend.CoAdvanceSmoothingTau"/>. Pass <see cref="TimeSpan.Zero"/> for either
    /// span to leave that part of the blend disabled.
    /// </summary>
    public static void SetMasterChain(
        this IMediaClock clock,
        TimeSpan masterHandoffCrossFade,
        TimeSpan coAdvanceSmoothingTau,
        params PlaybackClockCandidate[] candidates)
    {
        if (candidates.Length == 0)
        {
            clock.SetMaster(null);
            return;
        }

        var blend = new CompositePlaybackClockBlend
        {
            HandoffCrossFade = masterHandoffCrossFade > TimeSpan.Zero ? masterHandoffCrossFade : default,
            CoAdvanceSmoothingTau = coAdvanceSmoothingTau > TimeSpan.Zero ? coAdvanceSmoothingTau : default,
        };

        if (!blend.HasHandoffCrossFade && !blend.HasCoAdvanceSmoothing)
            clock.SetMaster(new CompositePlaybackClock(candidates));
        else
            clock.SetMaster(new CompositePlaybackClock(blend, candidates));
    }
}
