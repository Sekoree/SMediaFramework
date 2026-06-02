using S.Media.Core.Audio;
using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class SoundboardTests
{
    private static readonly AudioFormat Stereo = new(48_000, 2);

    private static AudioClip MakeClip(double seconds, float amp = 0.5f)
    {
        var frames = (int)(seconds * Stereo.SampleRate);
        var buf = new float[frames * Stereo.Channels];
        Array.Fill(buf, amp);
        return AudioClip.FromSamples(Stereo, buf);
    }

    [Fact]
    public void Fire_PlaysCue_DeliversAudio_AndRaisesCompletedWhenFinished()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        var output = new RecordingOutput(Stereo);
        var outId = router.AddOutput(output);
        using var board = new Soundboard(router);
        board.AddCue("kick", MakeClip(0.02), outId);

        router.Start();
        var voice = board.Fire("kick");
        Assert.NotNull(voice);
        var completed = false;
        voice!.Completed += _ => completed = true;
        Assert.True(voice.IsActive);

        // The short clip plays out; reaping detects completion and raises the event.
        Assert.True(SpinUntil(() => { board.Reap(); return completed; }, 2000), "cue should finish + raise Completed");
        Assert.False(voice.IsActive);
        Assert.True(output.SawNonSilentAudio, "cue audio should have reached the output");

        router.Stop();
    }

    [Fact]
    public void ChokeGroup_FiringSecondCue_StopsFirst_SecondKeepsPlaying()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        var outId = router.AddOutput(new RecordingOutput(Stereo));
        using var board = new Soundboard(router);
        board.AddCue("a", MakeClip(2.0), outId, chokeGroup: "g");
        board.AddCue("b", MakeClip(2.0), outId, chokeGroup: "g");
        router.Start();

        var a = board.Fire("a");
        Assert.NotNull(a);
        Assert.True(a!.IsActive);

        var b = board.Fire("b"); // same choke group → stops 'a' (release fade)
        Assert.NotNull(b);

        Assert.True(SpinUntil(() => { board.Reap(); return !a.IsActive; }, 2000), "choke should stop the first cue");
        Assert.True(b!.IsActive); // the newer cue keeps playing
        router.Stop();
    }

    [Fact]
    public void Fire_UnknownCue_ReturnsNull()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        using var board = new Soundboard(router);
        Assert.Null(board.Fire("does-not-exist"));
    }

    [Fact]
    public void Fire_OneShotAlreadySounding_ReturnsNull()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        var outId = router.AddOutput(new RecordingOutput(Stereo));
        using var board = new Soundboard(router);
        board.AddCue("a", MakeClip(2.0), outId, mode: AudioClipPlayerMode.OneShot);

        var first = board.Fire("a");
        var second = board.Fire("a");

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void AddCue_UnknownOutput_Throws()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        using var board = new Soundboard(router);

        Assert.Throws<ArgumentException>(() => board.AddCue("a", MakeClip(0.1), "missing"));
    }

    [Fact]
    public void Fire_WhenOutputWasRemoved_RollsBackRouterSource()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        var outId = router.AddOutput(new RecordingOutput(Stereo));
        using var board = new Soundboard(router);
        board.AddCue("a", MakeClip(0.1), outId);
        Assert.True(router.RemoveOutput(outId));
        var beforeSources = router.SourceIds.Count;

        Assert.Throws<ArgumentException>(() => board.Fire("a"));

        Assert.Equal(beforeSources, router.SourceIds.Count);
    }

    [Fact]
    public void Fire_WhenRouteCreationFailsAfterAddSource_RollsBackRouterSource()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        var outId = router.AddOutput(new RecordingOutput(Stereo));
        using var board = new Soundboard(router);
        board.AddCue("a", MakeClip(0.1), outId, map: new ChannelMap([0, 2]));
        var beforeSources = router.SourceIds.Count;

        Assert.Throws<InvalidOperationException>(() => board.Fire("a"));

        Assert.Equal(beforeSources, router.SourceIds.Count);
    }

    [Fact]
    public void Dispose_OwningRouter_HardStopsVoices_AndDisposesRouter()
    {
        var router = new AudioRouter(48_000, chunkSamples: 480);
        var outId = router.AddOutput(new RecordingOutput(Stereo));
        var board = new Soundboard(router, ownsRouter: true);
        board.AddCue("a", MakeClip(2.0), outId);
        router.Start();
        var v = board.Fire("a");
        Assert.NotNull(v);

        board.Dispose();

        Assert.False(v!.IsActive); // hard-stopped (not just fading)
        Assert.Throws<ObjectDisposedException>(() => router.AddOutput(new RecordingOutput(Stereo)));
    }

    [Fact]
    public void Fire_RacingDisposeWithBorrowedRouter_RollsBackCreatedVoice()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        var outId = router.AddOutput(new RecordingOutput(Stereo));
        var board = new Soundboard(router);
        board.AddCue("a", MakeClip(2.0), outId, chokeGroup: "g");
        var sourceCountBefore = router.SourceIds.Count;

        try
        {
            Soundboard.AfterTryFireBeforeLiveAddForTests = board.Dispose;

            Assert.Throws<ObjectDisposedException>(() => board.Fire("a"));

            Assert.Equal(sourceCountBefore, router.SourceIds.Count);
        }
        finally
        {
            Soundboard.AfterTryFireBeforeLiveAddForTests = null;
            board.Dispose();
        }
    }

    [Fact]
    public void Fire_RacingDisposeWithOwnedRouter_DoesNotUseDisposedRouterAfterRollback()
    {
        var router = new AudioRouter(48_000, chunkSamples: 480);
        var outId = router.AddOutput(new RecordingOutput(Stereo));
        var board = new Soundboard(router, ownsRouter: true);
        board.AddCue("a", MakeClip(2.0), outId);

        try
        {
            Soundboard.AfterTryFireBeforeLiveAddForTests = board.Dispose;

            Assert.Throws<ObjectDisposedException>(() => board.Fire("a"));
            Assert.Throws<ObjectDisposedException>(() => router.AddOutput(new RecordingOutput(Stereo)));
        }
        finally
        {
            Soundboard.AfterTryFireBeforeLiveAddForTests = null;
            board.Dispose();
        }
    }

    [Fact]
    public void Dispose_RaisesCompletedOnceForLiveVoice()
    {
        using var router = new AudioRouter(48_000, chunkSamples: 480);
        var outId = router.AddOutput(new RecordingOutput(Stereo));
        var board = new Soundboard(router);
        board.AddCue("a", MakeClip(2.0), outId);
        var voice = board.Fire("a");
        Assert.NotNull(voice);
        var completed = 0;
        voice!.Completed += _ => Interlocked.Increment(ref completed);

        board.Dispose();
        board.Dispose();

        Assert.Equal(1, Volatile.Read(ref completed));
    }

    private static bool SpinUntil(Func<bool> cond, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return cond();
    }

    private sealed class RecordingOutput(AudioFormat fmt) : IAudioOutput
    {
        private volatile bool _nonSilent;
        public AudioFormat Format { get; } = fmt;
        public bool SawNonSilentAudio => _nonSilent;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            if (_nonSilent) return;
            foreach (var s in packedSamples)
            {
                if (s != 0f) { _nonSilent = true; break; }
            }
        }
    }
}
