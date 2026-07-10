using System.Threading;
using S.Media.Core.Diagnostics;

namespace HaPlay.Playback;

/// <summary>
/// Process-wide latch that disables hardware video decode once a hardware-decode fault has been seen, so later
/// file opens fall back to software decode (CPU NV12 → plain <c>glTexSubImage2D</c> upload) which has no D3D11VA /
/// VAAPI surface-pool or GPU-interop dependency. Read by <see cref="HaPlayPlaybackHelpers.BuildFileOpenOptions"/>.
/// </summary>
/// <remarks>
/// The latch is deliberately coarse (whole process, not per file): a decode fault almost always means the
/// machine's hardware decode path is unusable for the current driver/content, and software decode is the safe
/// universal fallback. It resets only on app restart. <see cref="DisableAfterFault"/> reports whether <em>this</em>
/// call was the one that tripped it, so a caller can react exactly once (reload in software) and never loop when
/// the software path also fails (e.g. a genuinely corrupt stream).
/// </remarks>
internal static class HardwareVideoDecodeGate
{
    private static int _disabled;

    /// <summary>
    /// Forces software decode for the whole process when <c>HAPLAY_DISABLE_HW_DECODE</c> is set to
    /// <c>1</c>/<c>true</c>/<c>on</c>. An escape hatch for machines whose driver hardware-decode path is
    /// flaky (and a quick A/B to tell a hardware-surface issue apart from a software one) without waiting
    /// for a fault to trip the latch.
    /// </summary>
    private static readonly bool EnvDisabled = IsTruthy(Environment.GetEnvironmentVariable("HAPLAY_DISABLE_HW_DECODE"));

    /// <summary>True until a hardware-decode fault has tripped the gate (or <c>HAPLAY_DISABLE_HW_DECODE</c> is set); <see langword="false"/> thereafter.</summary>
    public static bool HardwareDecodeEnabled => !EnvDisabled && Volatile.Read(ref _disabled) == 0;

    private static bool IsTruthy(string? value) =>
        value is "1"
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Trips the gate (idempotent). Returns <see langword="true"/> only on the first trip so the caller can do a
    /// one-shot software-decode reload without risking a reload loop.
    /// </summary>
    public static bool DisableAfterFault()
    {
        var first = Interlocked.Exchange(ref _disabled, 1) == 0;
        if (first)
            MediaDiagnostics.LogWarning(
                "HardwareVideoDecodeGate: hardware video decode disabled for the rest of this process after a decode " +
                "fault - file playback will use software decode (CPU upload). Restart HaPlay to re-enable hardware decode.");
        return first;
    }
}
