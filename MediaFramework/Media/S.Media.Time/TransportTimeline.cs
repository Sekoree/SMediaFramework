namespace S.Media.Time;

/// <summary>
/// Immutable source-correlation anchor carried by a <see cref="TransportTimelineSnapshot"/>. It maps one
/// source timestamp onto the transport group's monotonic master timeline. The mapping is re-anchored on a
/// discontinuity (seek, loop, pause/resume, source replacement) and is therefore generation-scoped.
/// </summary>
public readonly record struct TransportSourceCorrelation(
    RebasePolicy Policy,
    TimeSpan SourceAnchor,
    TimeSpan MasterAnchor,
    TimeSpan Offset);

/// <summary>
/// One atomic view of a transport group's authoritative timeline (NXT-04). Consumers use the appropriate
/// coordinate explicitly instead of inferring it from unrelated clocks:
/// <list type="bullet">
/// <item><see cref="MasterTime"/> / <see cref="OutputPresentationTime"/> for output scheduling;</item>
/// <item><see cref="SourceTime"/> for decoded video and subtitle selection;</item>
/// <item><see cref="CueTime"/> for cue-local effects and plugin layers.</item>
/// </list>
/// </summary>
public readonly record struct TransportTimelineSnapshot(
    TimeSpan MasterTime,
    TimeSpan SourceTime,
    TimeSpan CueTime,
    TimeSpan CueOrigin,
    TimeSpan TrimStart,
    TimeSpan? TrimEnd,
    double PlaybackRate,
    bool IsRunning,
    bool IsLive,
    int Generation,
    TransportSourceCorrelation SourceCorrelation)
{
    /// <summary>The master instant outputs should target for this snapshot.</summary>
    public TimeSpan OutputPresentationTime => MasterTime;
}

/// <summary>
/// Read-only timeline supplied to renderers, subtitle feeds, outputs, and plugin surfaces. The same object
/// remains valid for the lifetime of a transport group; its immutable state is atomically replaced at every
/// discontinuity, so consumers may read it without session-dispatcher affinity.
/// </summary>
public interface ITransportTimeline : IPlaybackClock
{
    /// <summary>Captures master/source/cue time and transport state from one generation.</summary>
    TransportTimelineSnapshot GetSnapshot();

    /// <summary>Maps a master instant to source time using the current generation's correlation anchor.</summary>
    TimeSpan SourceTimeAt(TimeSpan masterTime);

    /// <summary>Maps a source timestamp to its due master instant using the current correlation anchor.</summary>
    TimeSpan MasterTimeAt(TimeSpan sourceTime);
}

/// <summary>
/// The authoritative timeline contract for one transport group. It combines the group's monotonic
/// <see cref="SessionClock"/> with the active source playhead, cue origin/trim, rate state, discontinuity
/// generation, and live/file correlation policy. Mutations are dispatcher-owned; reads are lock-free.
/// </summary>
public sealed class TransportTimeline : ITransportTimeline
{
    private sealed record State(
        IReadOnlyPlayhead? Source,
        TimeSpan CueOrigin,
        TimeSpan MasterAnchor,
        TimeSpan SourceAnchor,
        TimeSpan TrimStart,
        TimeSpan? TrimEnd,
        bool IsLive,
        RebasePolicy Policy,
        TimeSpan SourceOffset,
        int Generation);

    private readonly SessionClock _master;
    private State _state = new(
        Source: null,
        CueOrigin: TimeSpan.Zero,
        MasterAnchor: TimeSpan.Zero,
        SourceAnchor: TimeSpan.Zero,
        TrimStart: TimeSpan.Zero,
        TrimEnd: null,
        IsLive: false,
        Policy: RebasePolicy.Scheduled,
        SourceOffset: TimeSpan.Zero,
        Generation: 0);

    public TransportTimeline(SessionClock master) =>
        _master = master ?? throw new ArgumentNullException(nameof(master));

    /// <inheritdoc />
    public TimeSpan ElapsedSinceStart => GetSnapshot().OutputPresentationTime;

    /// <inheritdoc />
    public bool IsAdvancing => GetSnapshot().IsRunning;

    /// <summary>The current discontinuity generation.</summary>
    public int Generation => Volatile.Read(ref _state).Generation;

    /// <summary>
    /// Binds a newly active source and begins a new cue-local timeline. The current master instant becomes
    /// <see cref="TransportTimelineSnapshot.CueOrigin"/>; source time starts at the playhead's current position
    /// (normally <paramref name="trimStart"/> after standby pre-roll).
    /// </summary>
    public void BindSource(
        IReadOnlyPlayhead source,
        TimeSpan trimStart = default,
        TimeSpan? trimEnd = null,
        bool isLive = false,
        TimeSpan sourceOffset = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (trimStart < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(trimStart));
        if (trimEnd is { } end && end < trimStart)
            throw new ArgumentOutOfRangeException(nameof(trimEnd), "trim end must not precede trim start");

        var previous = Volatile.Read(ref _state);
        var masterNow = SafeMasterTime(previous.MasterAnchor);
        var sourceNow = SafeSourceTime(source, trimStart);
        Volatile.Write(ref _state, new State(
            source,
            CueOrigin: masterNow,
            MasterAnchor: masterNow,
            SourceAnchor: sourceNow,
            TrimStart: trimStart,
            TrimEnd: trimEnd,
            IsLive: isLive,
            Policy: isLive ? RebasePolicy.RebaseToLatest : RebasePolicy.Scheduled,
            SourceOffset: sourceOffset,
            Generation: unchecked(previous.Generation + 1)));
    }

    /// <summary>
    /// Re-anchors source↔master correlation after seek, loop, pause/resume, rate change, or a live-source
    /// rebase. The cue origin and trim window remain stable; only the generation and correlation anchor move.
    /// </summary>
    public void MarkDiscontinuity()
    {
        var previous = Volatile.Read(ref _state);
        var masterNow = SafeMasterTime(previous.MasterAnchor);
        MarkDiscontinuityCore(previous, masterNow);
    }

    /// <summary>
    /// Re-anchors after an operation that jumps the source playhead. <paramref name="preservedMasterTime"/>
    /// must be captured before the seek/loop; the session clock is rebaselined so its monotonic coordinate does
    /// not inherit the source jump.
    /// </summary>
    public void MarkDiscontinuity(TimeSpan preservedMasterTime)
    {
        var previous = Volatile.Read(ref _state);
        _master.RebaseReference(preservedMasterTime);
        MarkDiscontinuityCore(previous, preservedMasterTime);
    }

    private void MarkDiscontinuityCore(State previous, TimeSpan masterNow)
    {
        var sourceNow = previous.Source is { } source
            ? SafeSourceTime(source, previous.SourceAnchor)
            : previous.SourceAnchor;
        Volatile.Write(ref _state, previous with
        {
            MasterAnchor = masterNow,
            SourceAnchor = sourceNow,
            Generation = unchecked(previous.Generation + 1),
        });
    }

    /// <summary>Clears the active source while preserving the monotonic master position.</summary>
    public void Clear()
    {
        var previous = Volatile.Read(ref _state);
        var masterNow = SafeMasterTime(previous.MasterAnchor);
        Volatile.Write(ref _state, new State(
            Source: null,
            CueOrigin: masterNow,
            MasterAnchor: masterNow,
            SourceAnchor: TimeSpan.Zero,
            TrimStart: TimeSpan.Zero,
            TrimEnd: null,
            IsLive: false,
            Policy: RebasePolicy.Scheduled,
            SourceOffset: TimeSpan.Zero,
            Generation: unchecked(previous.Generation + 1)));
    }

    /// <inheritdoc />
    public TransportTimelineSnapshot GetSnapshot()
    {
        var state = Volatile.Read(ref _state);
        var masterNow = SafeMasterTime(state.MasterAnchor);
        var sourceTime = SourceTimeAt(state, masterNow);
        var cueTime = sourceTime - state.TrimStart;
        if (cueTime < TimeSpan.Zero)
            cueTime = TimeSpan.Zero;
        if (state.TrimEnd is { } trimEnd)
        {
            var cueEnd = trimEnd - state.TrimStart;
            if (cueTime > cueEnd)
                cueTime = cueEnd;
        }

        var source = state.Source;
        var rate = source is null ? 0d : SafePlaybackRate(source);
        var running = source is not null && SafeIsRunning(source);
        return new TransportTimelineSnapshot(
            MasterTime: masterNow,
            SourceTime: sourceTime,
            CueTime: cueTime,
            CueOrigin: state.CueOrigin,
            TrimStart: state.TrimStart,
            TrimEnd: state.TrimEnd,
            PlaybackRate: rate,
            IsRunning: running,
            IsLive: state.IsLive,
            Generation: state.Generation,
            SourceCorrelation: new TransportSourceCorrelation(
                state.Policy, state.SourceAnchor, state.MasterAnchor, state.SourceOffset));
    }

    /// <inheritdoc />
    public TimeSpan SourceTimeAt(TimeSpan masterTime) =>
        SourceTimeAt(Volatile.Read(ref _state), masterTime);

    /// <inheritdoc />
    public TimeSpan MasterTimeAt(TimeSpan sourceTime)
    {
        var state = Volatile.Read(ref _state);
        var rate = EffectiveRate(state);
        if (rate <= 0d)
            return state.MasterAnchor;
        return state.MasterAnchor + Divide(sourceTime - state.SourceAnchor, rate) + state.SourceOffset;
    }

    private static TimeSpan SourceTimeAt(State state, TimeSpan masterTime)
    {
        var rate = EffectiveRate(state);
        if (rate <= 0d)
            return state.SourceAnchor;
        return state.SourceAnchor + Multiply(masterTime - state.MasterAnchor - state.SourceOffset, rate);
    }

    private static double EffectiveRate(State state) =>
        state.Source is { } source ? Math.Max(0d, SafePlaybackRate(source)) : 0d;

    private TimeSpan SafeMasterTime(TimeSpan fallback)
    {
        try { return _master.Now; }
        catch { return fallback; }
    }

    private static TimeSpan SafeSourceTime(IReadOnlyPlayhead source, TimeSpan fallback)
    {
        try { return source.CurrentPosition; }
        catch { return fallback; }
    }

    private static double SafePlaybackRate(IReadOnlyPlayhead source)
    {
        try
        {
            var rate = source.PlaybackRate;
            return double.IsFinite(rate) && rate >= 0d ? rate : 0d;
        }
        catch { return 0d; }
    }

    private static bool SafeIsRunning(IReadOnlyPlayhead source)
    {
        try { return source.IsRunning; }
        catch { return false; }
    }

    private static TimeSpan Multiply(TimeSpan value, double scale) =>
        TimeSpan.FromTicks(checked((long)Math.Round(value.Ticks * scale)));

    private static TimeSpan Divide(TimeSpan value, double divisor) =>
        TimeSpan.FromTicks(checked((long)Math.Round(value.Ticks / divisor)));
}
