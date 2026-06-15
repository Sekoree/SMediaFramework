using System.Diagnostics;
using S.Media.Core.Clock;

namespace S.Media.Core.Video;

/// <summary>Tuning for a <see cref="VideoPresentSyncGroup"/>.</summary>
public sealed record VideoPresentSyncGroupOptions
{
    /// <summary>
    /// Frames at or before <c>reference + EarlyTolerance</c> are eligible to present this tick — absorbs the
    /// sub-tick phase between the present cadence and the source frame rate (mirrors
    /// <see cref="VideoPlayer"/>'s early tolerance). Keep it well under one frame interval.
    /// </summary>
    public TimeSpan EarlyTolerance { get; init; } = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Constant offset subtracted from the reference position to form the present target — hold video back
    /// by the outputs' presentation latency so it lines up with the reference timeline (mirrors
    /// <see cref="VideoPlayer.PlayheadOffset"/>). Default zero.
    /// </summary>
    public TimeSpan PresentLatency { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// When some members are advance-ready but others are not (a member fell behind), the group
    /// <strong>holds</strong> — presents nothing new — so the ready members do not run ahead and tear the
    /// stitched canvas. After this many consecutive holds it gives up waiting and presents the ready
    /// members anyway, so one wedged output can't freeze the whole wall. The lagging member rejoins
    /// lock-step automatically once it has frames again. Set to 0 to never hold (always present the ready
    /// members; favours liveness over seam-accuracy).
    /// </summary>
    public int MaxStarveHoldTicks { get; init; } = 4;
}

/// <summary>Opaque handle to a member registered with a <see cref="VideoPresentSyncGroup"/>.</summary>
public readonly record struct VideoPresentSyncMemberHandle(int Id);

/// <summary>Outcome of one <see cref="VideoPresentSyncGroup.Tick"/> — for HUD / diagnostics.</summary>
/// <param name="Presented">The group advanced this tick (at least one member presented a new frame).</param>
/// <param name="HeldForLaggingMember">Some members were advance-ready but the group held to wait for a lagging member.</param>
/// <param name="GroupTargetPts">The PTS the group presented at (default when it held / idled).</param>
/// <param name="PresentedMembers">Members that presented a new frame this tick.</param>
/// <param name="LaggingMembers">Active members with no unpresented frame due while others had one.</param>
public readonly record struct VideoPresentSyncTickResult(
    bool Presented,
    bool HeldForLaggingMember,
    TimeSpan GroupTargetPts,
    int PresentedMembers,
    int LaggingMembers);

/// <summary>
/// Video genlock domain (issues-doc #2, Option&nbsp;B, Phase&nbsp;2b): drives several
/// <see cref="ISyncPresentableVideoOutput"/> members from one shared reference timeline so they present the
/// frame for the same timestamp <em>on the same tick</em>, with coordinated repeat/drop. This is the
/// missing "synchronized drop/repeat across outputs" the architecture doc lists as not-implemented — what a
/// stitched video wall (one canvas split across several physical outputs) needs so an object crossing a
/// panel seam is never one frame ahead on one side.
/// </summary>
/// <remarks>
/// <para>
/// It pairs with <see cref="OutputSyncGroup"/>: that one rate-disciplines audio crystals (so a member's
/// master clock — and thus its video timeline — converges); this one phase-aligns the actual video
/// <em>present</em>. They compose through one master playhead — the same <see cref="MediaClock"/> whose
/// <see cref="IPlaybackClock"/> drives the audio group is the <see cref="IReadOnlyPlayhead"/> reference
/// here. On a single machine, software video pumps share the system timer and don't drift, so present
/// scheduling (not rate slewing) is the whole job; separate display pixel clocks ultimately need hardware
/// genlock, which this targets sub-frame and documents rather than promising in software.
/// </para>
/// <para>
/// "Advance-ready" and present operate on each member's <strong>unpresented</strong> frames. The policy per
/// tick: if every active member is advance-ready, present them all at the oldest of their newest-ready PTS
/// (lock-step). If none are, hold (normal between-frames). If some are and some are not, hold up to
/// <see cref="VideoPresentSyncGroupOptions.MaxStarveHoldTicks"/> then degrade to presenting the ready ones.
/// </para>
/// <para>
/// Drive it from a host loop via <see cref="Tick"/>, or let <see cref="Start"/> run an internal timer.
/// Wiring it into HaPlay's cue engine is deferred until validated on real multi-output hardware — see
/// <c>Doc/HaPlay-MultiOutput-Sync.md</c>.
/// </para>
/// </remarks>
public sealed class VideoPresentSyncGroup : IDisposable
{
    private readonly IReadOnlyPlayhead _reference;
    private readonly VideoPresentSyncGroupOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<int, ISyncPresentableVideoOutput> _members = new();
    private int _nextId;
    private int _starveHolds;
    private Timer? _timer;
    private bool _disposed;

    public VideoPresentSyncGroup(IReadOnlyPlayhead reference, VideoPresentSyncGroupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _reference = reference;
        _options = options ?? new VideoPresentSyncGroupOptions();
        if (_options.MaxStarveHoldTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxStarveHoldTicks must be >= 0");
    }

    /// <summary>Registers a video output to present in lock-step with the rest of the group.</summary>
    public VideoPresentSyncMemberHandle AddMember(ISyncPresentableVideoOutput member)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = ++_nextId;
            _members[id] = member;
            return new VideoPresentSyncMemberHandle(id);
        }
    }

    /// <summary>Removes a member. Returns <c>false</c> if the handle was not registered.</summary>
    public bool RemoveMember(VideoPresentSyncMemberHandle handle)
    {
        lock (_gate) return _members.Remove(handle.Id);
    }

    /// <summary>Number of registered members (active or not).</summary>
    public int MemberCount
    {
        get { lock (_gate) return _members.Count; }
    }

    /// <summary>
    /// Runs one coordinated present across all members for the current reference position. Call from a host
    /// loop (ideally the same tick that drives the rest of playback) or let <see cref="Start"/> drive it.
    /// </summary>
    public VideoPresentSyncTickResult Tick()
    {
        lock (_gate)
        {
            if (_disposed || !_reference.IsRunning)
            {
                _starveHolds = 0;
                return default;
            }

            // Snapshot active members once (the dictionary can change between ticks, not during one).
            ISyncPresentableVideoOutput[]? active = null;
            var activeCount = 0;
            foreach (var m in _members.Values)
            {
                if (!m.IsPresentable) continue;
                (active ??= new ISyncPresentableVideoOutput[_members.Count])[activeCount++] = m;
            }

            if (activeCount == 0)
            {
                _starveHolds = 0;
                return default;
            }

            var target = _reference.CurrentPosition - _options.PresentLatency;
            var eligible = target + _options.EarlyTolerance;

            // Find the oldest of each ready member's newest-due PTS: the newest frame EVERY ready member can
            // show. Presenting there keeps a faster member from getting ahead of a slower one.
            var readyCount = 0;
            var groupPts = TimeSpan.MaxValue;
            for (var i = 0; i < activeCount; i++)
            {
                if (active![i].TryPeekReadyPts(eligible, out var pts))
                {
                    readyCount++;
                    if (pts < groupPts) groupPts = pts;
                }
            }

            // Nobody is due to advance — normal between-frames hold (every device keeps its last frame).
            if (readyCount == 0)
            {
                _starveHolds = 0;
                return default;
            }

            var allReady = readyCount == activeCount;
            if (!allReady)
            {
                // Some members would advance but others have fallen behind. Hold so the ready members don't
                // tear ahead of the laggards — bounded, so a wedged output can't freeze the whole wall.
                _starveHolds++;
                if (_options.MaxStarveHoldTicks > 0 && _starveHolds <= _options.MaxStarveHoldTicks)
                    return new VideoPresentSyncTickResult(
                        Presented: false, HeldForLaggingMember: true,
                        GroupTargetPts: default, PresentedMembers: 0,
                        LaggingMembers: activeCount - readyCount);
                // Budget exhausted: degrade to presenting the ready members so the show keeps moving.
            }
            else
            {
                _starveHolds = 0;
            }

            var presented = 0;
            for (var i = 0; i < activeCount; i++)
            {
                if (active![i].PresentUpTo(groupPts) == VideoSyncPresentOutcome.Presented)
                    presented++;
            }

            return new VideoPresentSyncTickResult(
                Presented: presented > 0,
                HeldForLaggingMember: false,
                GroupTargetPts: groupPts,
                PresentedMembers: presented,
                LaggingMembers: activeCount - readyCount);
        }
    }

    /// <summary>Starts an internal timer that calls <see cref="Tick"/> at <paramref name="interval"/>. Idempotent (restarts).</summary>
    public void Start(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer?.Dispose();
            _timer = new Timer(_ => Tick(), null, interval, interval);
        }
    }

    /// <summary>Stops the internal timer (no-op if not started). Manual <see cref="Tick"/> still works.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _members.Clear();
        }
    }
}
