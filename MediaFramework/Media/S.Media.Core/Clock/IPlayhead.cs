namespace S.Media.Core.Clock;

/// <summary>
/// Public playhead surface: position, running state, nominal rate, and cooperative seek.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IMediaClock"/> extends this with tick events, start/stop/pause, optional
/// <see cref="IPlaybackClock"/> mastering, and <see cref="IMediaClock.PositionChanged"/>.
/// </para>
/// <para>
/// For a seek-free read-only dependency, use <see cref="PlaybackTimelineClockExtensions.AsPlayhead"/>.
/// </para>
/// <para>
/// Other public clocks: <see cref="MediaClock"/> (driver), <see cref="CompositePlaybackClock"/>,
/// <see cref="VideoPtsClock"/>, and NDI's <c>NDIIngestPlaybackClock</c> (ingest master).
/// </para>
/// </remarks>
public interface IPlayhead
{
    TimeSpan CurrentPosition { get; }

    bool IsRunning { get; }

    /// <summary>Effective speed relative to real time (1.0 = normal).</summary>
    double PlaybackRate { get; }

    void Seek(TimeSpan position);
}
