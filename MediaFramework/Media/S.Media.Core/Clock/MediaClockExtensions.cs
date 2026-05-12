namespace S.Media.Core.Clock;

/// <summary>
/// Convenience for <see cref="IMediaClock.SetMaster"/> with a <see cref="CompositePlaybackClock"/>.
/// </summary>
/// <remarks>
/// <para>
/// Higher <see cref="PlaybackClockCandidate.Priority"/> wins when several clocks report
/// <see cref="IPlaybackClock.IsAdvancing"/> at once. For a stable playhead, prefer a single
/// advancing primary (e.g. hardware audio) and keep fallbacks non-advancing until the primary stops.
/// </para>
/// <para>
/// If the active leaf clock changes while multiple candidates advance, composite
/// <see cref="IPlaybackClock.ElapsedSinceStart"/> can jump; re-anchor with <see cref="IMediaClock.SetMaster"/>
/// or <see cref="IMediaClock.Seek"/> if you intentionally hand off between clocks.
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
}
