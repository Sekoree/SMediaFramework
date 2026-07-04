using S.Media.Core.Audio;
using S.Media.Decode.FFmpeg.Audio;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that <em>skips</em> (rather than fails) when the FFmpeg native libraries the
/// test needs cannot actually be used — a CI runner without FFmpeg (Windows), or with a distro FFmpeg whose
/// major version doesn't match the generated bindings (functions then throw <see cref="NotSupportedException"/>
/// from the dynamic loader). Mirrors <c>LibAssFactAttribute</c>. The probe creates a real resampler once and
/// caches the verdict.
/// </summary>
public sealed class FFmpegNativeFactAttribute : FactAttribute
{
    private static readonly string? UnavailableReason = Probe();

    private static string? Probe()
    {
        try
        {
            using var resampler = AudioResampler.Create(new AudioFormat(48_000, 2), new AudioFormat(48_000, 2));
            return null;
        }
        catch (Exception ex)
        {
            // Any failure to CREATE a trivial same-rate resampler is an environment problem (missing/mismatched
            // natives), not a code defect — the tests behind this attribute all start with exactly this call.
            return $"FFmpeg native (swresample) not usable on this runner: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public FFmpegNativeFactAttribute()
    {
        if (UnavailableReason is not null)
            Skip = UnavailableReason;
    }
}
