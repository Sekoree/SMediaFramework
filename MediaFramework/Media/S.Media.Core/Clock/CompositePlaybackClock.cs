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
/// When several candidates have <see cref="IPlaybackClock.IsAdvancing"/> <c>true</c> at once,
/// <see cref="ElapsedSinceStart"/> is taken from the single highest-<see cref="PlaybackClockCandidate.Priority"/> entry — an
/// instantaneous priority handoff, not a temporal blend of elapsed values.
/// </para>
/// <para>
/// Use with <see cref="MediaClockExtensions.SetMasterChain"/> to feed <see cref="MediaClock"/> from several
/// clocks (hardware audio, PTS+wall, NDI ingest, …) with explicit priority.
/// </para>
/// <para>
/// Candidates are evaluated in registration order within the same priority value
/// (first registered wins ties — the constructor sorts by priority descending, then by registration index ascending).
/// </para>
/// <para>
/// Priority merge affects <see cref="IPlaybackClock.ElapsedSinceStart"/> / <see cref="IPlaybackClock.IsAdvancing"/> only;
/// graph-wide coordinated master PPM and synchronized multi-sink drop/repeat remain host-owned — see
/// <see cref="MediaClock"/> and <see cref="S.Media.Core.Audio.AudioRouter"/> remarks (checklist Tier E **18**; first-party module — §Tier F **31**).
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

        // Sort by priority (desc), then registration index (asc) so equal priorities follow the documented
        // first-registered tie-break; plain Array.Sort on priority alone is unstable for duplicates.
        var indexed = new (PlaybackClockCandidate cand, int reg)[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
            indexed[i] = (candidates[i], i);
        Array.Sort(indexed, static (a, b) =>
        {
            var p = b.cand.Priority.CompareTo(a.cand.Priority);
            return p != 0 ? p : a.reg.CompareTo(b.reg);
        });
        _candidates = new PlaybackClockCandidate[candidates.Length];
        for (var i = 0; i < indexed.Length; i++)
            _candidates[i] = indexed[i].cand;
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
