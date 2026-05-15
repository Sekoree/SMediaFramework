using S.Media.Core.Clock;

namespace S.Media.NDI.Clock;

/// <summary>
/// Optional <see cref="IPlaybackClock"/> that tracks mux presentation time from both A/V paths
/// before NDI send — the playhead is <c>max(video PTS, audio PTS)</c> seen since <see cref="Reset"/> or first notify.
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="NotifyVideoPresentation"/> / <see cref="NotifyAudioPresentation"/> (or <see cref="NotifyPresentation"/>)
/// from the threads that submit to <see cref="Video.NDIVideoSender"/> / <see cref="Audio.NDIAudioSink"/> so
/// <see cref="MediaClock.SetMaster"/> can follow the same envelope as mux PTS without duplicating clock math in each sink.
/// </para>
/// <para>
/// After a coordinated seek, call <see cref="Reset"/> so the next presentation time establishes a new origin.
/// </para>
/// <para>
/// <see cref="ElapsedSinceStart"/> is monotonic while advancing; <see cref="Pause"/> freezes the reported value.
/// </para>
/// </remarks>
public sealed class NDIEgressMuxPlayheadClock : IPlaybackClock
{
    private readonly Lock _gate = new();

    private long _originTicks = -1;
    private long _maxMuxTicks;
    private bool _advancing = true;
    private TimeSpan _frozenElapsed;

    /// <inheritdoc />
    public bool IsAdvancing
    {
        get
        {
            lock (_gate) return _advancing;
        }
    }

    /// <inheritdoc />
    public TimeSpan ElapsedSinceStart
    {
        get
        {
            lock (_gate)
            {
                if (_originTicks < 0)
                    return TimeSpan.Zero;
                if (!_advancing)
                    return _frozenElapsed;
                var delta = _maxMuxTicks - _originTicks;
                return delta <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(delta);
            }
        }
    }

    /// <summary>Updates the mux playhead from either stream (whichever presentation time is greater wins).</summary>
    public void NotifyPresentation(TimeSpan muxPresentation)
    {
        var t = muxPresentation.Ticks;
        lock (_gate)
        {
            if (!_advancing)
                return;
            if (_originTicks < 0)
                _originTicks = t;
            if (t > _maxMuxTicks)
                _maxMuxTicks = t;
        }
    }

    /// <summary>Convenience — same as <see cref="NotifyPresentation"/>.</summary>
    public void NotifyVideoPresentation(TimeSpan presentationTime) => NotifyPresentation(presentationTime);

    /// <summary>Convenience — same as <see cref="NotifyPresentation"/>.</summary>
    public void NotifyAudioPresentation(TimeSpan presentationTime) => NotifyPresentation(presentationTime);

    /// <summary>Freezes <see cref="ElapsedSinceStart"/> at its current value.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_originTicks < 0)
            {
                _advancing = false;
                _frozenElapsed = TimeSpan.Zero;
                return;
            }

            _frozenElapsed = _maxMuxTicks <= _originTicks
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(_maxMuxTicks - _originTicks);
            _advancing = false;
        }
    }

    /// <summary>Resumes advancing from the last known mux end (no jump on resume).</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _advancing = true;
        }
    }

    /// <summary>Clears session origin and max — call after seek or when reusing the clock for a new file.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _originTicks = -1;
            _maxMuxTicks = 0;
            _advancing = true;
            _frozenElapsed = TimeSpan.Zero;
        }
    }
}
