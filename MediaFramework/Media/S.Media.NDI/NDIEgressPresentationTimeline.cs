namespace S.Media.NDI;

/// <summary>
/// Single session anchor for NDI timecodes (100 ns ticks, same unit as <see cref="System.TimeSpan.Ticks"/>)
/// when <see cref="Video.NDIVideoTimecodeMode.PresentationRelativeTicks"/> is active on an <see cref="NDIOutput"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NDIOutput"/> wires one instance to both <see cref="Audio.NDIAudioOutput"/> and <see cref="Video.NDIVideoSender"/>
/// so whichever stream submits first establishes the anchor and both streams stay on one relative timeline.
/// </para>
/// <para>
/// Thread-safe: audio and video pumps may call concurrently.
/// </para>
/// </remarks>
internal sealed class NDIEgressPresentationTimeline
{
    private readonly Lock _gate = new();
    private TimeSpan? _anchor;

    /// <summary>Clears the anchor (for example after seek or before a new configure).</summary>
    public void Reset()
    {
        lock (_gate)
            _anchor = null;
    }

    /// <summary>
    /// Returns <c>(<paramref name="presentationTime"/> − anchor).Ticks</c>, establishing <paramref name="presentationTime"/>
    /// as the anchor on first use. A backward jump of more than one second re-anchors (same rule as <see cref="Video.NDIVideoSender"/>).
    /// </summary>
    public long TimecodeFromPresentationTime(TimeSpan presentationTime)
    {
        lock (_gate)
        {
            if (_anchor is null)
                _anchor = presentationTime;

            var anchor = _anchor.Value;
            var delta = presentationTime - anchor;
            if (delta < TimeSpan.FromSeconds(-1))
            {
                _anchor = presentationTime;
                delta = TimeSpan.Zero;
            }

            return delta < TimeSpan.Zero ? 0L : delta.Ticks;
        }
    }
}
