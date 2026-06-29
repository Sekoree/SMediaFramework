using System.Diagnostics;
using Microsoft.Extensions.Logging;
using S.Media.Time;
using S.Media.Core.Diagnostics;

namespace HaPlay.Playback;

/// <summary>
/// Engine-wide audio genlock (Doc/HaPlay-MultiOutput-Sync.md, Option&nbsp;B): disciplines every active audio
/// device's effective rate toward one reference device, so the cue/soundboard engine's independently-mastered
/// per-device routers don't drift apart over a long show. The <strong>first</strong> registered device is the
/// reference (left untouched — never wrapped, so the master clock is unperturbed); every other device is a
/// member of one <see cref="OutputSyncGroup"/> whose per-member ppm correction drives that device's
/// <c>ClipAudioOutputRuntime</c> <c>AdaptiveRateAudioOutput</c>.
/// </summary>
/// <remarks>
/// <para>
/// Scope is engine-wide (not per-composition) because <c>ClipAudioOutputRuntime</c>s are pooled <em>per
/// physical device</em> and shared across compositions and the soundboard, so a device must receive exactly
/// one correction. See the scope discussion in <c>Doc/HaPlay-MultiOutput-Sync.md</c>.
/// </para>
/// <para>
/// Opt-in and <strong>off by default</strong> (<see cref="IsEnabled"/> via <c>HAPLAY_MULTIOUTPUT_GENLOCK</c>):
/// it needs validation on two real audio devices (a wrong drift direction won't surface in unit tests). When
/// disabled the engine never constructs one and the audio path is byte-identical to before.
/// </para>
/// <para>
/// Thread-safety: <see cref="GetPpm"/> is called from each member's adaptive-rate provider; register/unregister
/// happen on cue start/stop. All public members lock a single gate; the only nested lock is into the
/// <see cref="OutputSyncGroup"/> (which never calls back), so there is no lock-ordering hazard.
/// </para>
/// </remarks>
internal sealed class EngineAudioGenlock : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.EngineAudioGenlock");

    /// <summary>Opt-in toggle: <c>HAPLAY_MULTIOUTPUT_GENLOCK</c> = <c>1</c>/<c>true</c>. Default off.</summary>
    internal static bool IsEnabled =>
        Environment.GetEnvironmentVariable("HAPLAY_MULTIOUTPUT_GENLOCK") is "1" or "true" or "TRUE" or "True";

    private readonly object _gate = new();
    private readonly List<(Guid LineId, IPlaybackClock Clock)> _devices = []; // [0] = reference, rest = members
    private readonly Dictionary<Guid, OutputSyncMemberHandle> _handles = [];
    private readonly Timer? _timer;
    private OutputSyncGroup? _group;
    private long _lastTimerTimestamp;
    private bool _disposed;

    /// <param name="autoTick">
    /// When true (default) an internal timer drives the controller. Tests pass false and call
    /// <see cref="Tick"/> manually for determinism.
    /// </param>
    public EngineAudioGenlock(bool autoTick = true)
    {
        if (!autoTick) return;
        _lastTimerTimestamp = Stopwatch.GetTimestamp();
        _timer = new Timer(_ => OnTimer(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    /// <summary>True while the controller is actually disciplining (≥ 2 devices, i.e. a reference + a member).</summary>
    public bool IsActive
    {
        get { lock (_gate) return _group is not null; }
    }

    /// <summary>
    /// Registers a device's playback clock and returns <c>true</c> when it joined as a disciplined
    /// <em>member</em> (caller should wrap it via <c>ratePpmProvider</c>), or <c>false</c> when it is the
    /// reference (caller must leave it unwrapped). Idempotent: re-registering returns the device's current role.
    /// </summary>
    public bool Register(Guid lineId, IPlaybackClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        lock (_gate)
        {
            if (_disposed) return false;
            var existing = _devices.FindIndex(d => d.LineId == lineId);
            if (existing >= 0) return existing != 0;          // already known: member iff not the reference slot

            _devices.Add((lineId, clock));
            RebuildLocked();
            var isMember = _devices.Count > 1;                // first device is the reference, the rest members
            if (isMember)
                Trace.LogInformation("EngineAudioGenlock: disciplining {Members} member device(s) to the reference",
                    _devices.Count - 1);
            return isMember;
        }
    }

    /// <summary>Removes a device. If it was the reference, the next remaining device is promoted (handoff).</summary>
    public void Unregister(Guid lineId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var idx = _devices.FindIndex(d => d.LineId == lineId);
            if (idx < 0) return;
            var wasReference = idx == 0;
            _devices.RemoveAt(idx);
            RebuildLocked();
            if (wasReference && _devices.Count > 0)
                Trace.LogInformation("EngineAudioGenlock: reference released; promoted a remaining device to reference");
        }
    }

    /// <summary>
    /// Current ppm correction for a device — feed into its <c>AdaptiveRateAudioOutput</c>. Returns 0 for the
    /// reference device, an unknown/removed line, or while fewer than two devices are active (nothing to lock).
    /// </summary>
    public double GetPpm(Guid lineId)
    {
        lock (_gate)
        {
            if (_group is null) return 0.0;
            return _handles.TryGetValue(lineId, out var handle) ? _group.GetMemberPpm(handle) : 0.0;
        }
    }

    /// <summary>Runs one control update (the timer calls this; tests call it directly).</summary>
    internal void Tick(TimeSpan elapsed)
    {
        OutputSyncGroup? group;
        lock (_gate) group = _group;
        group?.Tick(elapsed);   // OutputSyncGroup.Tick is self-locking and a no-op once disposed
    }

    private void OnTimer()
    {
        var now = Stopwatch.GetTimestamp();
        var prev = Interlocked.Exchange(ref _lastTimerTimestamp, now);
        Tick(Stopwatch.GetElapsedTime(prev, now));
    }

    // Rebuilds the group whenever membership changes. Cheap and rare (cue start/stop), so a full rebuild keeps
    // the per-line handle map trivially correct across reference handoff. Members' providers close over the
    // stable lineId (not a handle), so they keep working across a rebuild.
    private void RebuildLocked()
    {
        _group?.Dispose();
        _group = null;
        _handles.Clear();
        if (_devices.Count < 2) return;                       // a reference alone can't drift relative to anything

        var group = new OutputSyncGroup(_devices[0].Clock);
        for (var i = 1; i < _devices.Count; i++)
            _handles[_devices[i].LineId] = group.AddMember(_devices[i].Clock);
        _group = group;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _group?.Dispose();
            _group = null;
            _devices.Clear();
            _handles.Clear();
        }
    }
}
