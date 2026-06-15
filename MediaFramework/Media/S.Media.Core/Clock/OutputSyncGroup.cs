using System.Diagnostics;

namespace S.Media.Core.Clock;

/// <summary>Tuning for an <see cref="OutputSyncGroup"/>'s per-member rate controller.</summary>
public sealed record OutputSyncGroupOptions
{
    /// <summary>
    /// Control-loop bandwidth in Hz — how quickly a member locks to the reference. Lower is gentler and
    /// more noise-immune; higher snaps faster but chases jitter. ~0.1&nbsp;Hz locks in a handful of seconds.
    /// </summary>
    public double LoopBandwidthHz { get; init; } = 0.1;

    /// <summary>Damping ratio. <c>1.0</c> = critically damped (no overshoot); &lt;1 locks faster but overshoots.</summary>
    public double DampingRatio { get; init; } = 1.0;

    /// <summary>
    /// Absolute clamp on the applied correction (and, via anti-windup, on the integral term). Crystal
    /// tolerance is ~±50&nbsp;ppm, so a correction near this bound means the member is at the edge of what
    /// a few-ppm resample can pull back.
    /// </summary>
    public double MaxAbsPpm { get; init; } = 50.0;

    /// <summary>
    /// A phase error larger than this is treated as a discontinuity (seek / flush / segment reset) and
    /// resets the controller instead of being chased with a saturated correction.
    /// </summary>
    public TimeSpan ResyncThreshold { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>Opaque handle to a member registered with an <see cref="OutputSyncGroup"/>.</summary>
public readonly record struct OutputSyncMemberHandle(int Id);

/// <summary>
/// Genlock domain (issues-doc #2, Option&nbsp;B): disciplines several independently-mastered playback clocks
/// onto one shared <em>reference</em> timeline. Each member drifts on its own physical crystal; a bounded
/// PI controller derives a per-member ppm rate correction from the member's phase error versus the
/// reference. Feed that correction into the member's audio actuator — Option&nbsp;A's
/// <c>AdaptiveRateAudioOutput</c> via its ppm-provider constructor: slewing the device's effective rate
/// paces the member's master clock, which in turn paces its video, so audio and video both converge.
/// Clocks not added to any group keep their independent behaviour (correct for unrelated program feeds).
/// </summary>
/// <remarks>
/// <para>
/// This is the Phase-1 foundation — the coordinated master-ppm policy the architecture doc lists as
/// "not implemented". Wiring sketch for a two-device composition:
/// <code>
/// var group = new OutputSyncGroup(referenceClock);              // e.g. cue 0's MediaClock
/// var h = group.AddMember(secondCueClock);                      // cue 1's MediaClock
/// var adaptive = new AdaptiveRateAudioOutput(device1, () => group.GetMemberPpm(h));
/// group.Start(TimeSpan.FromMilliseconds(100));                  // or call Tick() from a host loop
/// </code>
/// </para>
/// <para>
/// Lock-step frame <em>present</em> for video-only outputs that have no audio actuator (pure LED/projector
/// walls) is the Phase-2 follow-up that builds on this controller — see <c>Doc/HaPlay-MultiOutput-Sync.md</c>.
/// </para>
/// </remarks>
public sealed class OutputSyncGroup : IDisposable
{
    private readonly IReadOnlyPlayhead _reference;
    private readonly OutputSyncGroupOptions _options;
    private readonly double _kp;   // ppm per second-of-phase
    private readonly double _ki;   // ppm per (second·second-of-phase)
    private readonly double _integralClamp;
    private readonly object _gate = new();
    private readonly Dictionary<int, Member> _members = new();
    private int _nextId;
    private Timer? _timer;
    private long _timerLastTimestamp;
    private bool _disposed;

    private sealed class Member
    {
        public required IReadOnlyPlayhead Clock { get; init; }
        public double Integral;   // accumulated phase error (s·s) feeding the I term
        public double Ppm;        // latest correction (negative ⇒ slow the member)
    }

    public OutputSyncGroup(IReadOnlyPlayhead reference, OutputSyncGroupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _reference = reference;
        _options = options ?? new OutputSyncGroupOptions();
        if (_options.LoopBandwidthHz <= 0) throw new ArgumentOutOfRangeException(nameof(options), "LoopBandwidthHz must be > 0");
        if (_options.DampingRatio <= 0) throw new ArgumentOutOfRangeException(nameof(options), "DampingRatio must be > 0");

        // Standard 2nd-order PLL gains from bandwidth + damping. The plant gain is 1e-6 (ppm → s/s), so the
        // raw gains are large; expressing them via bandwidth/damping keeps the knobs intuitive.
        var omega = 2.0 * Math.PI * _options.LoopBandwidthHz;
        _ki = omega * omega * 1e6;
        _kp = 2.0 * _options.DampingRatio * omega * 1e6;
        _integralClamp = _ki > 0 ? _options.MaxAbsPpm / _ki : 0.0;
    }

    /// <summary>Registers a clock to discipline toward the reference. Read its correction via <see cref="GetMemberPpm"/>.</summary>
    public OutputSyncMemberHandle AddMember(IReadOnlyPlayhead memberClock)
    {
        ArgumentNullException.ThrowIfNull(memberClock);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = ++_nextId;
            _members[id] = new Member { Clock = memberClock };
            return new OutputSyncMemberHandle(id);
        }
    }

    /// <summary>Removes a member. Returns <c>false</c> if the handle was not registered.</summary>
    public bool RemoveMember(OutputSyncMemberHandle handle)
    {
        lock (_gate) return _members.Remove(handle.Id);
    }

    /// <summary>
    /// Latest ppm correction for a member (negative = slow it down, positive = speed it up). Thread-safe;
    /// wire into <c>AdaptiveRateAudioOutput</c>'s ppm provider. Returns 0 for an unknown/removed handle.
    /// </summary>
    public double GetMemberPpm(OutputSyncMemberHandle handle)
    {
        lock (_gate) return _members.TryGetValue(handle.Id, out var m) ? m.Ppm : 0.0;
    }

    /// <summary>
    /// Runs one control update for every member using the elapsed time since the previous update. Call from
    /// a host loop, or let <see cref="Start"/> drive it. A non-positive <paramref name="elapsed"/> is ignored.
    /// </summary>
    public void Tick(TimeSpan elapsed)
    {
        var dt = elapsed.TotalSeconds;
        if (dt <= 0) return;
        lock (_gate)
        {
            if (_disposed) return;
            var refRunning = _reference.IsRunning;
            var refPos = _reference.CurrentPosition;
            foreach (var m in _members.Values)
                UpdateMemberLocked(m, refRunning, refPos, dt);
        }
    }

    private void UpdateMemberLocked(Member m, bool refRunning, TimeSpan refPos, double dt)
    {
        // Discipline only while both clocks advance; a paused side would accrue bogus phase error.
        if (!refRunning || !m.Clock.IsRunning)
        {
            m.Integral = 0;
            m.Ppm = 0;
            return;
        }

        var phaseError = (m.Clock.CurrentPosition - refPos).TotalSeconds; // + ⇒ member ahead of reference

        // A large jump is a seek/flush, not crystal drift — reset rather than chase it.
        if (Math.Abs(phaseError) > _options.ResyncThreshold.TotalSeconds)
        {
            m.Integral = 0;
            m.Ppm = 0;
            return;
        }

        // PI on phase: the integral cancels the constant crystal offset, the proportional term nulls residual
        // phase. Anti-windup clamps the integral's contribution to ±MaxAbsPpm.
        m.Integral += phaseError * dt;
        if (_integralClamp > 0)
            m.Integral = Math.Clamp(m.Integral, -_integralClamp, _integralClamp);

        var u = -(_kp * phaseError + _ki * m.Integral);
        m.Ppm = Math.Clamp(u, -_options.MaxAbsPpm, _options.MaxAbsPpm);
    }

    /// <summary>Starts an internal timer that calls <see cref="Tick"/> at <paramref name="interval"/>. Idempotent (restarts).</summary>
    public void Start(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer?.Dispose();
            _timerLastTimestamp = Stopwatch.GetTimestamp();
            _timer = new Timer(OnTimer, null, interval, interval);
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

    private void OnTimer(object? _)
    {
        var now = Stopwatch.GetTimestamp();
        var prev = Interlocked.Exchange(ref _timerLastTimestamp, now);
        Tick(Stopwatch.GetElapsedTime(prev, now));
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
