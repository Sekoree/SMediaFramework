using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Decode.FFmpeg.Audio;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

/// <summary>
/// Skips (rather than fails) when either dependency of the remux end-to-end tests is missing: usable
/// FFmpeg natives (the <see cref="FFmpegNativeFactAttribute"/> probe) or the host <c>ffmpeg</c> CLI
/// (used only to GENERATE the lavfi test inputs - the remuxer itself is in-process libavformat).
/// </summary>
public sealed class RemuxFactAttribute : FactAttribute
{
    private static readonly string? UnavailableReason = Probe();

    private static string? Probe()
    {
        try
        {
            using var resampler = AudioResampler.Create(new AudioFormat(48_000, 2), new AudioFormat(48_000, 2));
        }
        catch (Exception ex)
        {
            return $"FFmpeg natives not usable on this runner: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0 ? null : "ffmpeg CLI returned non-zero";
        }
        catch (Exception ex)
        {
            return $"ffmpeg CLI unavailable: {ex.GetType().Name}";
        }
    }

    public RemuxFactAttribute()
    {
        if (UnavailableReason is not null)
            Skip = UnavailableReason;
    }
}
