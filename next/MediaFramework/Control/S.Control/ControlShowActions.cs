using S.Media.Session;

namespace S.Control;

/// <summary>
/// The show actions a control script can invoke — the bridge from a MIDI/OSC trigger to the running show. So a
/// hardware button or fader can GO, fire a cue, seek, or stop. Fire-and-forget: a control trigger dispatches an
/// action and returns; it does not await the result. Implemented over <see cref="ShowSession"/> by
/// <see cref="ShowSessionControlActions"/>; surfaced to scripts as the <c>show</c> global.
/// </summary>
public interface IControlShowActions
{
    /// <summary>Fire the next cue in a group (GO).</summary>
    void Go(string? groupId = null);

    /// <summary>Fire a specific cue by id.</summary>
    void FireCue(string cueId);

    /// <summary>Seek a group's transport to a position.</summary>
    void Seek(TimeSpan position, string? groupId = null);

    /// <summary>Stop a group's transport.</summary>
    void Stop(string? groupId = null);
}

/// <summary>
/// Binds <see cref="IControlShowActions"/> to a live <see cref="ShowSession"/> — the host wires this so control
/// scripts (e.g. a MIDI GO button) drive the running show. Actions are posted to the session's serial dispatcher;
/// the returned task is intentionally not awaited (a trigger fires and returns).
/// </summary>
public sealed class ShowSessionControlActions : IControlShowActions
{
    private readonly ShowSession _session;

    public ShowSessionControlActions(ShowSession session) =>
        _session = session ?? throw new ArgumentNullException(nameof(session));

    public void Go(string? groupId = null) =>
        _ = groupId is null ? _session.GoAsync() : _session.GoAsync(groupId);

    public void FireCue(string cueId) =>
        _ = _session.FireCueAsync(cueId);

    public void Seek(TimeSpan position, string? groupId = null) =>
        _ = groupId is null ? _session.SeekAsync(position) : _session.SeekAsync(position, groupId);

    public void Stop(string? groupId = null) =>
        _ = groupId is null ? _session.StopAsync() : _session.StopAsync(groupId);
}
