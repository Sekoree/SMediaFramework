namespace S.Media.Core.Clock;

public interface IMediaClock
{
    public TimeSpan CurrentPosition { get; }

    public event EventHandler<TimeSpan>? PositionChanged;

    public event EventHandler? AudioTick;
    public event EventHandler? VideoTick;

    public void Start();
    public void Stop();
    public void Reset();

    public void Pause();

    public bool IsRunning { get; }

    public void Seek(TimeSpan position);

    /// <summary>
    /// Slave the clock's position to an external <see cref="IPlaybackClock"/>
    /// (typically the audio sink). Pass <c>null</c> to revert to the internal
    /// stopwatch. Position is preserved across the swap — no jump.
    /// </summary>
    public void SetMaster(IPlaybackClock? master);
}
