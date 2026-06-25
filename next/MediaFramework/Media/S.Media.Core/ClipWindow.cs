namespace S.Media.Core;

/// <summary>
/// A trim window over a source timeline: a <see cref="Start"/> offset, an optional <see cref="End"/>,
/// the effective playable <see cref="Duration"/>, and conversions between source-timeline and
/// clip-relative positions. Generalizes the clip/trim logic previously duplicated across cue/clip
/// players, soundboards, and output wrappers — pair it with
/// <see cref="S.Media.Core.Video.RetimingVideoOutput"/> to rebase a clip's frames to a zero-based
/// timeline.
/// </summary>
public readonly record struct ClipWindow(TimeSpan Start, TimeSpan End, TimeSpan Duration, bool HasKnownEnd)
{
    /// <summary>Guard kept away from the exact in/out points: seeking to "the end" can't land past the
    /// last frame, and end-detection fires slightly early so a clip doesn't overrun its boundary.</summary>
    public static readonly TimeSpan DefaultEndGuard = TimeSpan.FromMilliseconds(50);

    /// <summary>Open window over an unknown-duration source (e.g. a live input): start at 0, no end.</summary>
    public static ClipWindow Unbounded { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, HasKnownEnd: false);

    /// <summary>Build a window from start/end trim offsets over a source of the given duration. A
    /// non-positive <paramref name="sourceDuration"/> yields an unbounded window starting at
    /// <paramref name="startOffset"/>.</summary>
    public static ClipWindow FromOffsets(TimeSpan startOffset, TimeSpan endOffset, TimeSpan sourceDuration)
        => FromOffsets(startOffset, endOffset, sourceDuration, DefaultEndGuard);

    public static ClipWindow FromOffsets(TimeSpan startOffset, TimeSpan endOffset, TimeSpan sourceDuration, TimeSpan endGuard)
    {
        var start = startOffset < TimeSpan.Zero ? TimeSpan.Zero : startOffset;
        if (sourceDuration <= TimeSpan.Zero)
            return new ClipWindow(start, TimeSpan.Zero, TimeSpan.Zero, HasKnownEnd: false);

        var maxStart = sourceDuration > endGuard ? sourceDuration - endGuard : TimeSpan.Zero;
        if (start > maxStart)
            start = maxStart;

        var trimmedEnd = endOffset < TimeSpan.Zero ? TimeSpan.Zero : endOffset;
        var end = sourceDuration - trimmedEnd;
        if (end > sourceDuration)
            end = sourceDuration;
        if (end < start)
            end = start;

        return new ClipWindow(start, end, end - start, HasKnownEnd: true);
    }

    /// <summary>Map a clip-relative position (0 = clip start) to an absolute source-timeline position,
    /// clamped into the window (with the end guard so it can't seek past the last frame).</summary>
    public TimeSpan ToSourcePosition(TimeSpan relativePosition) => ToSourcePosition(relativePosition, DefaultEndGuard);

    public TimeSpan ToSourcePosition(TimeSpan relativePosition, TimeSpan endGuard)
    {
        if (relativePosition < TimeSpan.Zero)
            relativePosition = TimeSpan.Zero;

        if (!HasKnownEnd)
            return Start + relativePosition;

        var maxRelative = Duration > endGuard ? Duration - endGuard : TimeSpan.Zero;
        if (relativePosition > maxRelative)
            relativePosition = maxRelative;
        return Start + relativePosition;
    }

    /// <summary>Map an absolute source-timeline position to a clip-relative position (0 = clip start),
    /// clamped to [0, <see cref="Duration"/>] when the end is known.</summary>
    public TimeSpan ToRelativePosition(TimeSpan sourcePosition)
    {
        var relative = sourcePosition - Start;
        if (relative < TimeSpan.Zero)
            return TimeSpan.Zero;
        if (HasKnownEnd && relative > Duration)
            return Duration;
        return relative;
    }

    /// <summary>Whether a source-timeline position has reached the clip's (guarded) end. Always false
    /// for an unbounded window.</summary>
    public bool IsAtEnd(TimeSpan sourcePosition) => IsAtEnd(sourcePosition, DefaultEndGuard);

    public bool IsAtEnd(TimeSpan sourcePosition, TimeSpan endGuard)
    {
        if (!HasKnownEnd)
            return false;
        var threshold = End > endGuard ? End - endGuard : End;
        return sourcePosition >= threshold;
    }
}
