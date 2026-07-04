using S.Media.Core.Video;
using S.Media.Decode.FFmpeg.Video;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <em>skips</em> (rather than fails) when the FFmpeg native libraries
/// cannot actually be used — a CI runner without FFmpeg (Windows), or with a distro FFmpeg whose major version
/// doesn't match the generated bindings (functions then throw <see cref="NotSupportedException"/> from the
/// dynamic loader). Mirrors the <c>LibAssFact</c>/<c>FFmpegNativeFact</c> pattern in the framework test
/// projects; this copy probes the swscale CPU frame converter — the path the HaPlay tests behind it need.
/// </summary>
public sealed class FFmpegNativeFactAttribute : FactAttribute
{
    private static readonly string? UnavailableReason = Probe();

    private static string? Probe()
    {
        try
        {
            // CanConvert opens (and frees) a real swscale context — the exact native path the tests need.
            return VideoCpuFrameConverter.CanConvert(PixelFormat.Uyvy, PixelFormat.Bgra32, 4, 2)
                ? null
                : "FFmpeg swscale reports UYVY→BGRA unconvertible on this runner";
        }
        catch (Exception ex)
        {
            // A throw from the probe is an environment problem (missing/mismatched FFmpeg natives — the
            // dynamic bindings throw NotSupportedException on a version mismatch), not a code defect.
            return $"FFmpeg native (swscale) not usable on this runner: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public FFmpegNativeFactAttribute()
    {
        if (UnavailableReason is not null)
            Skip = UnavailableReason;
    }
}
