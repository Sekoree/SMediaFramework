using HaPlay.Playback;
using S.Media.Core.Audio;
using Xunit;

namespace HaPlay.Tests;

public sealed class CueAudioSourceAdaptersTests
{
    [Fact]
    public void AudioSourceFanout_BranchesReceiveSameSamples()
    {
        var source = new ArrayAudioSource([1, 2, 3, 4, 5, 6]);
        using var fanout = new AudioSourceFanout(source);
        using var a = (IDisposable)fanout.CreateBranch();
        using var b = (IDisposable)fanout.CreateBranch();
        var branchA = (IAudioSource)a;
        var branchB = (IAudioSource)b;

        Span<float> buf = stackalloc float[2];
        Assert.Equal(2, branchA.ReadInto(buf));
        Assert.Equal(new float[] { 1, 2 }, buf.ToArray());

        buf.Clear();
        Assert.Equal(2, branchB.ReadInto(buf));
        Assert.Equal(new float[] { 1, 2 }, buf.ToArray());

        buf.Clear();
        Assert.Equal(2, branchA.ReadInto(buf));
        Assert.Equal(new float[] { 3, 4 }, buf.ToArray());

        buf.Clear();
        Assert.Equal(2, branchB.ReadInto(buf));
        Assert.Equal(new float[] { 3, 4 }, buf.ToArray());
    }

    [Fact]
    public void PausableAudioSource_DoesNotAdvanceInnerSourceWhilePaused()
    {
        var source = new ArrayAudioSource([1, 2, 3, 4]);
        using var pausable = new PausableAudioSource(source);
        Span<float> buf = stackalloc float[2];

        pausable.IsPaused = true;
        Assert.Equal(0, pausable.ReadInto(buf));
        Assert.Equal(0, source.Position);

        pausable.IsPaused = false;
        Assert.Equal(2, pausable.ReadInto(buf));
        Assert.Equal(new float[] { 1, 2 }, buf.ToArray());
        Assert.Equal(2, source.Position);
    }

    private sealed class ArrayAudioSource : IAudioSource
    {
        private readonly float[] _samples;

        public ArrayAudioSource(float[] samples)
        {
            _samples = samples;
        }

        public AudioFormat Format { get; } = new(48_000, 1);
        public int Position { get; private set; }
        public bool IsExhausted => Position >= _samples.Length;

        public int ReadInto(Span<float> destination)
        {
            var available = Math.Min(destination.Length, _samples.Length - Position);
            if (available <= 0)
                return 0;
            _samples.AsSpan(Position, available).CopyTo(destination);
            Position += available;
            return available;
        }
    }
}
