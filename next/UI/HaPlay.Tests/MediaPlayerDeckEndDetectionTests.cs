using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Covers <c>MediaPlayerViewModel.ConfirmShowSessionEnded</c> — the deck poll's end-of-track decision
/// under the ShowSession path. A coordinated SEEK transiently pauses the clip (IsRunning=false) while it reseeks
/// the demux; without debouncing that transient, the poll mistook it for end-of-track and tore the deck down
/// ("freezes then stops" after a few seeks). This pins the guard: mid-seek/scrub is never "ended", and a real
/// end must persist across two ticks.</summary>
public sealed class MediaPlayerDeckEndDetectionTests
{
    [Fact]
    public void RunningClip_IsNeverEnded_AndResetsTheCounter()
    {
        var ticks = 1; // pretend a prior transient started counting
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(
            isRunning: true, isPlaying: true, isScrubbing: false, seekInFlight: false, ref ticks));
        Assert.Equal(0, ticks); // a running tick clears any in-flight count
    }

    [Fact]
    public void SingleStoppedTick_IsNotYetEnded_ButAccumulates()
    {
        var ticks = 0;
        // First stopped-while-playing tick: not enough to conclude the track ended (could be a seek transient).
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(
            isRunning: false, isPlaying: true, isScrubbing: false, seekInFlight: false, ref ticks));
        Assert.Equal(1, ticks);
    }

    [Fact]
    public void TwoConsecutiveStoppedTicks_ConfirmEnd()
    {
        var ticks = 0;
        MediaPlayerViewModel.ConfirmShowSessionEnded(false, true, false, false, ref ticks); // tick 1 → 1
        Assert.True(MediaPlayerViewModel.ConfirmShowSessionEnded(false, true, false, false, ref ticks)); // tick 2 → end
    }

    [Fact]
    public void SeekInFlight_NeverEnds_AndResetsAnyPartialCount()
    {
        var ticks = 1; // a stopped tick was already seen
        // A seek is now in flight (the clip is transiently paused mid-reseek) — must NOT be treated as ended,
        // and the partial count is cleared so the seek's pause can't combine with a later tick to reach 2.
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(
            isRunning: false, isPlaying: true, isScrubbing: false, seekInFlight: true, ref ticks));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void Scrubbing_NeverEnds()
    {
        var ticks = 1;
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(
            isRunning: false, isPlaying: true, isScrubbing: true, seekInFlight: false, ref ticks));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void StoppedWhileNotPlaying_IsNotEnd()
    {
        // Paused deck (IsPlaying=false): the clip is stopped but the operator paused it — not an end-of-track.
        var ticks = 0;
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(false, false, false, false, ref ticks));
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(false, false, false, false, ref ticks));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void TransientSeekPause_BetweenRunningTicks_DoesNotReachEnd()
    {
        // Realistic sequence: playing → one seek-transient stopped tick → playing again. Never confirms end.
        var ticks = 0;
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(true, true, false, false, ref ticks));  // playing
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(false, true, false, false, ref ticks)); // transient
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(true, true, false, false, ref ticks));  // playing
        Assert.Equal(0, ticks);
    }
}
