using S.Media.Core.Audio;
using S.Media.NDI.Clock;
using S.Media.Routing;

namespace S.Media.NDI.Audio;

/// <summary>NDI-specific <see cref="AudioRouter"/> clock helpers.</summary>
public static class AudioRouterNDIExtensions
{
    /// <summary>
    /// Paces <paramref name="router"/> from <paramref name="ingestClock"/> media time
    /// (replaces the internal <c>IngestSlavedRouterClock</c> wiring).
    /// </summary>
    public static void SlaveToNDI(this AudioRouter router, NDIIngestPlaybackClock ingestClock) =>
        router.SlaveToIngest(ingestClock);
}
