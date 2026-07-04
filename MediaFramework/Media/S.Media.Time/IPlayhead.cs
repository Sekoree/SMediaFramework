namespace S.Media.Time;

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
    /// <summary>
    /// The playhead position on the media timeline. Naming disambiguation for the three
    /// position-like properties in the framework: this is the <em>clock-side</em> playhead;
    /// <c>ISeekableSource.Position</c> is the <em>decoder-side</em> consumed-samples position
    /// (can run ahead of audible output by the buffered amount); and
    /// <c>IPlaybackClock.ElapsedSinceStart</c> is the master clock's raw played-time feed
    /// the playhead is derived from.
    /// </summary>
    TimeSpan CurrentPosition { get; }

    bool IsRunning { get; }

    /// <summary>Effective speed relative to real time (1.0 = normal).</summary>
    double PlaybackRate { get; }

    void Seek(TimeSpan position);
}
