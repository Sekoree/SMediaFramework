using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MALib;

/// <summary>Minimal logging seam for the standalone binding (MALib has no dependency on the media
/// framework's diagnostics). Hosts may assign <see cref="Logger"/>; the default is a no-op.</summary>
public static class MALibDiagnostics
{
    /// <summary>Sink for resolver/binding diagnostics. Assign once at startup; never null.</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    internal static void LogRejectedCandidate(string candidate, uint major, uint minor, uint revision)
    {
        Logger.LogWarning(
            "MALib: rejected miniaudio candidate '{Candidate}' - reported version {Major}.{Minor}.{Revision} "
            + "(0.0.0 = no ma_version export), pinned {PinnedMajor}.{PinnedMinor}.{PinnedRevision}. "
            + "The hand-mirrored ABI accepts only the exact pinned build.",
            candidate, major, minor, revision,
            MiniAudioLibrary.PinnedMajor, MiniAudioLibrary.PinnedMinor, MiniAudioLibrary.PinnedRevision);
    }
}
