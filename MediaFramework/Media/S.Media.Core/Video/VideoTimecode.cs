using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace S.Media.Core.Video;

/// <summary>
/// SMPTE 12M timecode attached to a video frame (<see cref="VideoFrame.Timecode"/>). Carries the
/// hours/minutes/seconds/frames quadruple plus the drop-frame flag and source frame rate so it can
/// be re-encoded into NDI's timecode slot, displayed as a string, or round-tripped via frame number.
/// </summary>
/// <remarks>
/// <para>
/// Drop-frame is a SMPTE convention used for 29.97 (NTSC) and 59.94 fps streams: frames 0 and 1 of
/// each minute (or 0/1/2/3 at 59.94) are skipped except every tenth minute. This compensates for
/// the offset between integer-frame time and real time so 1 hour of timecode ≈ 1 hour of wall
/// clock. Drop-frame applies only to 29.97 / 59.94 — 23.976 is NOT drop-frame.
/// </para>
/// <para>
/// Use <see cref="VideoTimecodeMath.IsDropFrameRate"/> to test eligibility. Constructing a drop-frame
/// timecode at a non-eligible rate throws.
/// </para>
/// </remarks>
public readonly record struct VideoTimecode
{
    public int Hours { get; }
    public int Minutes { get; }
    public int Seconds { get; }
    public int Frames { get; }
    public bool IsDropFrame { get; }
    public Rational FrameRate { get; }

    public VideoTimecode(int hours, int minutes, int seconds, int frames, bool isDropFrame, Rational frameRate)
    {
        if (hours < 0 || hours > 23)
            throw new ArgumentOutOfRangeException(nameof(hours), "must be 0..23");
        if ((uint)minutes >= 60)
            throw new ArgumentOutOfRangeException(nameof(minutes), "must be 0..59");
        if ((uint)seconds >= 60)
            throw new ArgumentOutOfRangeException(nameof(seconds), "must be 0..59");
        if (frames < 0)
            throw new ArgumentOutOfRangeException(nameof(frames), "must be >= 0");
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameRate), "must be a positive rational");
        if (isDropFrame && !VideoTimecodeMath.IsDropFrameRate(frameRate))
            throw new ArgumentException(
                $"drop-frame timecode is only valid at 29.97 or 59.94 fps; got {frameRate.Numerator}/{frameRate.Denominator}",
                nameof(isDropFrame));

        var nominalFps = VideoTimecodeMath.NominalIntegerFps(frameRate);
        if (frames >= nominalFps)
            throw new ArgumentOutOfRangeException(nameof(frames), $"must be < nominal fps {nominalFps}");

        Hours = hours;
        Minutes = minutes;
        Seconds = seconds;
        Frames = frames;
        IsDropFrame = isDropFrame;
        FrameRate = frameRate;
    }

    /// <summary>Format as <c>HH:MM:SS:FF</c> (or <c>HH:MM:SS;FF</c> when drop-frame).</summary>
    public string ToTimecodeString()
    {
        var sep = IsDropFrame ? ';' : ':';
        return $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}{sep}{Frames:D2}";
    }

    public override string ToString() => ToTimecodeString();

    /// <summary>
    /// Frame number counting from <c>00:00:00:00</c>, accounting for drop-frame skips. Useful as a
    /// monotonic id; equals SMPTE "total frame count" semantics.
    /// </summary>
    public long ToFrameNumber() =>
        VideoTimecodeMath.ToFrameNumber(Hours, Minutes, Seconds, Frames, FrameRate, IsDropFrame);

    /// <summary>
    /// 100-ns ticks since <c>00:00:00:00</c>. Computed as <c>frame_number / fps</c> in ticks —
    /// this is what NDI's timecode slot consumes.
    /// </summary>
    public long ToTicksAtRate()
    {
        var fn = ToFrameNumber();
        // ticks = fn * (denominator / numerator) * 10_000_000.
        // Use 128-bit math via decimal to avoid overflow on long durations.
        return (long)((decimal)fn * FrameRate.Denominator * 10_000_000m / FrameRate.Numerator);
    }

    /// <summary>Build a timecode from a frame-count since 00:00:00:00 at <paramref name="frameRate"/>.</summary>
    public static VideoTimecode FromFrameNumber(long frameNumber, Rational frameRate, bool dropFrame)
    {
        if (frameNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(frameNumber), "must be >= 0");
        VideoTimecodeMath.FromFrameNumber(frameNumber, frameRate, dropFrame,
            out var h, out var m, out var s, out var f);
        return new VideoTimecode(h, m, s, f, dropFrame, frameRate);
    }

    /// <summary>
    /// Parse <c>HH:MM:SS:FF</c> or <c>HH:MM:SS;FF</c>. Returns false on any malformed input. The
    /// separator before the frames component selects drop-frame (<c>;</c>) vs non-drop (<c>:</c>).
    /// </summary>
    public static bool TryParse(string s, Rational frameRate, [NotNullWhen(true)] out VideoTimecode? result)
    {
        result = null;
        if (string.IsNullOrEmpty(s) || s.Length < 11) return false;
        if (s[2] != ':' || s[5] != ':') return false;
        var sep = s[8];
        var df = sep == ';';
        if (sep != ':' && sep != ';') return false;
        if (!int.TryParse(s.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h)) return false;
        if (!int.TryParse(s.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return false;
        if (!int.TryParse(s.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var sec)) return false;
        if (!int.TryParse(s.AsSpan(9, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var fr)) return false;

        if (df && !VideoTimecodeMath.IsDropFrameRate(frameRate)) return false;
        if (h is < 0 or > 23 || (uint)m >= 60 || (uint)sec >= 60) return false;
        if (fr < 0 || fr >= VideoTimecodeMath.NominalIntegerFps(frameRate)) return false;

        result = new VideoTimecode(h, m, sec, fr, df, frameRate);
        return true;
    }
}

/// <summary>Drop-frame math + frame-rate classification helpers for <see cref="VideoTimecode"/>.</summary>
public static class VideoTimecodeMath
{
    /// <summary>True for the two SMPTE drop-frame rates: 30000/1001 (29.97) and 60000/1001 (59.94).</summary>
    public static bool IsDropFrameRate(Rational rate)
    {
        if (rate.Denominator <= 0) return false;
        return (rate.Numerator == 30000 && rate.Denominator == 1001)
            || (rate.Numerator == 60000 && rate.Denominator == 1001);
    }

    /// <summary>
    /// Nominal integer FPS used for timecode framing — 30 for 29.97, 60 for 59.94, otherwise
    /// rounded from the rational value.
    /// </summary>
    public static int NominalIntegerFps(Rational rate)
    {
        if (rate.Denominator <= 0 || rate.Numerator <= 0) return 30;
        // 30000/1001 → 30, 60000/1001 → 60, 24000/1001 → 24, otherwise round to nearest.
        var f = (double)rate.Numerator / rate.Denominator;
        return (int)Math.Round(f, MidpointRounding.AwayFromZero);
    }

    /// <summary>Convert (h:m:s:f) to a monotonic frame number, applying SMPTE drop-frame skips when requested.</summary>
    public static long ToFrameNumber(int h, int m, int s, int f, Rational rate, bool dropFrame)
    {
        var fps = NominalIntegerFps(rate);
        long total = (long)h * 3600 * fps + (long)m * 60 * fps + (long)s * fps + f;
        if (!dropFrame) return total;
        // SMPTE drop-frame: drop `dropCount` frames at the start of every minute except every 10th.
        // 29.97 → drop 2; 59.94 → drop 4.
        var dropCount = (rate.Numerator == 60000) ? 4 : 2;
        var totalMinutes = (long)h * 60 + m;
        var droppedMinutes = totalMinutes - (totalMinutes / 10);
        return total - droppedMinutes * dropCount;
    }

    /// <summary>Inverse of <see cref="ToFrameNumber"/>; respects drop-frame.</summary>
    public static void FromFrameNumber(long frameNumber, Rational rate, bool dropFrame,
        out int h, out int m, out int s, out int f)
    {
        var fps = NominalIntegerFps(rate);
        long fn = frameNumber;
        if (dropFrame)
        {
            // Inverse drop-frame: re-insert skipped numbers.
            // Standard reference algorithm (Annex of SMPTE 12M-1 / Wikipedia "SMPTE timecode").
            var dropCount = (rate.Numerator == 60000) ? 4 : 2;
            long framesPerMinute = fps * 60L - dropCount;
            long framesPer10Minutes = fps * 60L * 10L - dropCount * 9L;
            long d = fn / framesPer10Minutes;
            long mInside = fn % framesPer10Minutes;
            long adjusted;
            if (mInside > dropCount)
                adjusted = fn + dropCount * 9L * d + dropCount * ((mInside - dropCount) / framesPerMinute);
            else
                adjusted = fn + dropCount * 9L * d;
            fn = adjusted;
        }
        var totalSeconds = fn / fps;
        f = (int)(fn % fps);
        s = (int)(totalSeconds % 60);
        var totalMin = totalSeconds / 60;
        m = (int)(totalMin % 60);
        h = (int)(totalMin / 60 % 24);
    }
}
