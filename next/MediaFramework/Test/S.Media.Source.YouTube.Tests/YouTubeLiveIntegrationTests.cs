using S.Media.Core.Audio;
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
    // Override with MFP_YOUTUBE_LIVE_VIDEO to reproduce a report against a specific video.
    private static readonly string VideoId =
        Environment.GetEnvironmentVariable("MFP_YOUTUBE_LIVE_VIDEO") is { Length: > 0 } id ? id : "jNQXAC9IVRw";

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

    /// <summary>The operator's exact case (2026-07-03 bug report): an AUDIO-ONLY selection, played from
    /// the prepared asset — the clip must report a real duration and actually DECODE (the original live
    /// test only proved the sources open, which lets a zero-length/duration-less remux through as an
    /// "instantly done" clip).</summary>
    [LiveFact]
    public async Task PrepareAudioOnly_AssetHasDuration_AndDecodes()
    {
        var dir = Directory.CreateTempSubdirectory("yt-live-audio-").FullName;
        try
        {
            var gateway = new YoutubeExplodeGateway();
            var preparer = new YouTubePreparer(gateway, dir);
            var prepared = await preparer.PrepareAsync(
                VideoId, YouTubeStreamSelection.Best with { IncludeVideo = false }, progress: null);

            var registry = MediaRegistry.Build(b => b.Use(new YouTubeSourceModule(preparer)));
            var uri = YouTubeSourceUri.Build(VideoId, prepared.ResolvedSelection);
            Assert.True(registry.TryOpenAudio(uri, options: null, out var audio), "prepared audio must open");
            try
            {
                var seekable = Assert.IsAssignableFrom<ISeekableSource>(audio);
                Assert.True(seekable.Duration > TimeSpan.FromSeconds(5),
                    $"audio-only asset reports duration {seekable.Duration} — the clip would end instantly");

                // Decode ~1 second of samples — proves the remuxed stream actually plays, not just probes.
                var buffer = new float[audio.Format.SampleRate * audio.Format.Channels];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = audio.ReadInto(buffer.AsSpan(total));
                    if (read <= 0)
                        break;
                    total += read;
                }

                Assert.True(total >= buffer.Length / 2,
                    $"audio-only asset decoded only {total} samples of the first second");
            }
            finally
            {
                (audio as IDisposable)?.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
