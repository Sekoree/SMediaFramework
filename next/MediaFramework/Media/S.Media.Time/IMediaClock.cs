namespace S.Media.Time;

/// <summary>
/// Master playback driver: <see cref="IPlayhead"/> plus tick events, transport controls,
/// and optional slaving to an <see cref="IPlaybackClock"/> (typically the audio output).
/// </summary>
public interface IMediaClock : IPlayhead
{
    public event EventHandler<TimeSpan>? PositionChanged;

    public event EventHandler? AudioTick;
    public event EventHandler? VideoTick;

    public void Start();
    public void Stop(CancellationToken cancellationToken = default);
    public void Reset();

    /// <param name="cancellationToken">Thrown through while blocking on the timing driver shutdown.</param>
    public void Pause(CancellationToken cancellationToken = default);

    /// <summary>
    /// Slave the clock's position to an external <see cref="IPlaybackClock"/>
    /// (typically the audio output). Pass <c>null</c> to revert to the internal
    /// stopwatch. Position is preserved across the swap — no jump.
    /// </summary>
    public void SetMaster(IPlaybackClock? master);
}
