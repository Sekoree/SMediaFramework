using Xunit;

namespace S.Media.Source.YouTube.Tests;

/// <summary>
/// LIVE-network integration (review: opt-in only — YouTube's internal surface changes independently of
/// this repo, so these must never gate CI). Enable with <c>MFP_YOUTUBE_LIVE_TESTS=1</c>. Uses the real
/// gateway + the real FFmpeg remuxer and plays the result through the registry.
/// </summary>
public sealed class YouTubeLiveIntegrationTests
{
    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MFP_YOUTUBE_LIVE_TESTS") != "1")
                Skip = "live YouTube tests are opt-in: set MFP_YOUTUBE_LIVE_TESTS=1";
        }
    }

    // "Me at the zoo" — the first YouTube upload; short (19 s), stable, and safe to assume it stays up.
    private const string VideoId = "jNQXAC9IVRw";

    [LiveFact]
    public async Task Prepare_RealVideo_ProducesAPlayableLocalAsset()
    {
        var dir = Directory.CreateTempSubdirectory("yt-live-").FullName;
        try
        {
            var gateway = new YoutubeExplodeGateway();
            var manifest = await gateway.GetManifestAsync(VideoId, CancellationToken.None);
            Assert.NotEmpty(manifest.VideoStreams);
            Assert.NotEmpty(manifest.AudioStreams);

            var preparer = new YouTubePreparer(gateway, dir);
            var phases = new List<YouTubePreparePhase>();
            var prepared = await preparer.PrepareAsync(
                VideoId, YouTubeStreamSelection.Best,
                new Progress<YouTubePrepareProgress>(p => { lock (phases) phases.Add(p.Phase); }));

            Assert.True(new FileInfo(prepared.AssetPath).Length > 50_000, "prepared asset suspiciously small");

            // The prepared asset must play through the provider (the exact cue/deck open path) via the
            // RESOLVED selection — this is what the UI persists after a prepare.
            var registry = MediaRegistry.Build(b => b.Use(new YouTubeSourceModule(preparer)));
            var uri = YouTubeSourceUri.Build(VideoId, prepared.ResolvedSelection);
            Assert.True(registry.TryOpenAudio(uri, options: null, out var audio), "prepared audio must open");
            (audio as IDisposable)?.Dispose();
            Assert.True(registry.TryOpenVideo(uri, options: null, out var video), "prepared video must open");
            (video as IDisposable)?.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
