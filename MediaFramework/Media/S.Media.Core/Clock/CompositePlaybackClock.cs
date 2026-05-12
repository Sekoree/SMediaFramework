namespace S.Media.Core.Clock;

/// <summary>
/// Merges several <see cref="IPlaybackClock"/> instances by <strong>priority</strong>:
/// the active clock is the highest-priority candidate whose <see cref="IPlaybackClock.IsAdvancing"/>
/// is <c>true</c>. <see cref="ElapsedSinceStart"/> is read from that clock only.
/// </summary>
/// <remarks>
/// <para>
/// When no candidate is advancing, <see cref="IsAdvancing"/> is <c>false</c> and
/// <see cref="ElapsedSinceStart"/> returns <see cref="TimeSpan.Zero"/> (neutral idle state).
/// </para>
/// <para>
/// Use with <see cref="MediaClockExtensions.SetMasterChain"/> to feed <see cref="MediaClock"/> from several
/// clocks (hardware audio, PTS+wall, NDI ingest, …) with explicit priority.
/// </para>
/// <para>
/// Candidates are evaluated in registration order within the same priority value
/// (first registered wins ties).
/// </para>
/// </remarks>
public sealed class CompositePlaybackClock : IPlaybackClock
{
    private readonly PlaybackClockCandidate[] _candidates;

    /// <param name="candidates">Registration list (tie-break: earlier entry wins at equal priority).</param>
    public CompositePlaybackClock(params PlaybackClockCandidate[] candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Length == 0)
            throw new ArgumentException("at least one playback clock candidate is required", nameof(candidates));
        _candidates = [.. candidates];
        Array.Sort(_candidates, static (a, b) => b.Priority.CompareTo(a.Priority));
    }

    public bool IsAdvancing
    {
        get
        {
            foreach (var c in _candidates)
            {
                if (c.Clock.IsAdvancing) return true;
            }

            return false;
        }
    }

    public TimeSpan ElapsedSinceStart
    {
        get
        {
            foreach (var c in _candidates)
            {
                if (c.Clock.IsAdvancing)
                    return c.Clock.ElapsedSinceStart;
            }

            return TimeSpan.Zero;
        }
    }
}

/// <summary>Entry for <see cref="CompositePlaybackClock"/>.</summary>
/// <param name="Clock">Underlying clock (typically <see cref="PortAudioOutput"/>, <see cref="VideoPtsClock"/>, …).</param>
/// <param name="Priority">Higher wins when multiple clocks are advancing simultaneously.</param>
public readonly record struct PlaybackClockCandidate(IPlaybackClock Clock, int Priority);
