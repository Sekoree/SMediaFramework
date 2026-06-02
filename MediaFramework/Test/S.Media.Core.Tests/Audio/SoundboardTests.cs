using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

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
