using S.Media.Core;
using S.Media.FFmpeg;
using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class ClipStandbyEngineTests
{
    public ClipStandbyEngineTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public async Task RefreshStandbyAsync_PreparesAndReleaseReturnsPreparedClip()
    {
        var path = WriteWav();
        await using var engine = new ClipStandbyEngine();
        try
        {
            var spec = Spec("cue-a", path, cacheKey: "v1");

            await engine.RefreshStandbyAsync([spec], new ClipStandbyPolicy());

            Assert.Contains(spec.Key, engine.PreparedKeys);
            var armed = await engine.ArmAsync(spec);
            var player = armed.Player;

            await armed.ReleaseAsync();

            Assert.Contains(spec.Key, engine.PreparedKeys);
            await using var armedAgain = await engine.ArmAsync(spec);
            Assert.Same(player, armedAgain.Player);
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public async Task RefreshStandbyAsync_CacheKeyChangeReplacesPreparedClip()
    {
        var path = WriteWav();
        await using var engine = new ClipStandbyEngine();
        try
        {
            var first = Spec("cue-a", path, cacheKey: "v1");
            var second = Spec("cue-a", path, cacheKey: "v2");

            await engine.RefreshStandbyAsync([first], new ClipStandbyPolicy());
            var armed = await engine.ArmAsync(first);
            var stalePlayer = armed.Player;
            await armed.ReleaseAsync();

            await engine.RefreshStandbyAsync([second], new ClipStandbyPolicy());

            Assert.True(stalePlayer.IsDisposed);
            await using var armedReplacement = await engine.ArmAsync(second);
            Assert.NotSame(stalePlayer, armedReplacement.Player);
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public async Task ArmAsync_CacheKeyMismatchDropsStalePreparedClip()
    {
        var path = WriteWav();
        await using var engine = new ClipStandbyEngine();
        try
        {
            var first = Spec("cue-a", path, cacheKey: "v1");
            var second = Spec("cue-a", path, cacheKey: "v2");

            await engine.RefreshStandbyAsync([first], new ClipStandbyPolicy());
            await using var firstArmed = await engine.ArmAsync(first);
            var stalePlayer = firstArmed.Player;
            await firstArmed.ReleaseAsync();

            await using var secondArmed = await engine.ArmAsync(second);

            Assert.True(stalePlayer.IsDisposed);
            Assert.NotSame(stalePlayer, secondArmed.Player);
            Assert.DoesNotContain(first.Key, engine.PreparedKeys);
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public async Task RemoveStandbyAsync_DisposesPreparedClipAndClearsKey()
    {
        var path = WriteWav();
        await using var engine = new ClipStandbyEngine();
        try
        {
            var spec = Spec("cue-a", path, cacheKey: "v1");
            await engine.RefreshStandbyAsync([spec], new ClipStandbyPolicy());
            var armed = await engine.ArmAsync(spec);
            var player = armed.Player;
            await armed.ReleaseAsync();

            await engine.RemoveStandbyAsync(spec.Id);

            Assert.True(player.IsDisposed);
            Assert.Empty(engine.PreparedKeys);
        }
        finally
        {
            MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public async Task RefreshStandbyAsync_MissingFileReportsFailedStatus()
    {
        await using var engine = new ClipStandbyEngine();
        IReadOnlyList<ClipPreparationStatus> statuses = [];
        engine.StandbyStatesChanged += s => statuses = s;
        var missing = Spec("missing", Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.wav"), "v1");

        await engine.RefreshStandbyAsync([missing], new ClipStandbyPolicy());

        var status = Assert.Single(statuses);
        Assert.Equal(missing.Key, status.Key);
        Assert.Equal(ClipPreparationState.Failed, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
    }

    [Fact]
    public async Task RefreshStandbyAsync_HonorsWindowAndDecoderCap()
    {
        var paths = new[] { WriteWav(), WriteWav(), WriteWav() };
        await using var engine = new ClipStandbyEngine();
        try
        {
            var specs = paths.Select((p, i) => Spec($"cue-{i}", p, $"v{i}")).ToArray();

            await engine.RefreshStandbyAsync(specs, new ClipStandbyPolicy(MaxPreparedDecoders: 2, Window: 3));

            Assert.Equal(["cue-0", "cue-1"], engine.PreparedKeys.Select(k => k.Id).Order().ToArray());
        }
        finally
        {
            foreach (var path in paths)
                MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public async Task StartGroupAsync_ArmsAllThenStartsAll()
    {
        var paths = new[] { WriteWav(durationSeconds: 0.5), WriteWav(durationSeconds: 0.5) };
        await using var engine = new ClipStandbyEngine();
        try
        {
            var specs = paths.Select((p, i) => Spec($"cue-{i}", p, $"v{i}")).ToArray();

            var armed = await engine.StartGroupAsync(specs);
            try
            {
                Assert.Equal(2, armed.Count);
                Assert.All(armed, clip => Assert.True(clip.IsStarted));
                Assert.All(armed, clip => Assert.True(clip.Player.AudioRouter!.IsRunning));
            }
            finally
            {
                foreach (var clip in armed)
                    await clip.DisposeAsync();
            }
        }
        finally
        {
            foreach (var path in paths)
                MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    [Fact]
    public async Task RefreshStandbyAsync_PreparesWindowEntriesInParallel()
    {
        var paths = new[] { WriteWav(), WriteWav() };
        await using var engine = new ClipStandbyEngine();
        try
        {
            using var bothInFlight = new SemaphoreSlim(0);
            var concurrent = 0;
            var sawParallel = false;

            ClipSpec GatedSpec(string id, string path, string cacheKey) => new(
                id,
                ClipMediaSource.FromBuilder(() =>
                {
                    // With parallel preparation both builders are in here together: the second
                    // arrival flags it and releases both. A serial pass never overlaps, so the
                    // first builder just waits out the (bounded) timeout and the flag stays false.
                    if (Interlocked.Increment(ref concurrent) >= 2)
                    {
                        Volatile.Write(ref sawParallel, true);
                        bothInFlight.Release(2);
                    }

                    bothInFlight.Wait(TimeSpan.FromSeconds(5));
                    Interlocked.Decrement(ref concurrent);
                    return MediaPlayer.OpenFile(path);
                }, path),
                ClipWindow.FromOffsets(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                cacheKey);

            var specs = new[] { GatedSpec("cue-0", paths[0], "v0"), GatedSpec("cue-1", paths[1], "v1") };

            await engine.RefreshStandbyAsync(specs, new ClipStandbyPolicy(PrepareParallelism: 2));

            Assert.True(Volatile.Read(ref sawParallel), "standby preparation did not overlap the two opens");
            Assert.Equal(2, engine.PreparedKeys.Count);
        }
        finally
        {
            foreach (var path in paths)
                MediaPlayerSmokeTestHelpers.TryDelete(path);
        }
    }

    private static ClipSpec Spec(string id, string path, string cacheKey) =>
        new(
            id,
            ClipMediaSource.File(path),
            ClipWindow.FromOffsets(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            cacheKey);

    private static string WriteWav(double durationSeconds = 1)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_clip_standby_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, MediaPlayerSmokeTestHelpers.CreateWavBytes(durationSeconds));
        return path;
    }
}
