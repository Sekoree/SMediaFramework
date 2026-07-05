using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Covers <c>MediaPlayerViewModel.ConfirmShowSessionEnded</c> — the deck poll's end-of-track decision
/// under the ShowSession path. A coordinated SEEK transiently pauses the clip (IsRunning=false) while it reseeks
/// the demux; without debouncing that transient, the poll mistook it for end-of-track and tore the deck down
/// ("freezes then stops" after a few seeks). This pins the guard: mid-seek/scrub is never "ended", a real end
/// must persist across two ticks, and a timeline-generation change (the session's NXT-04 discontinuity signal —
/// any seek/pause/resume/clip swap, including ones the deck did not initiate) restarts the window outright.</summary>
public sealed class MediaPlayerDeckEndDetectionTests
{
    // Every case runs with a STABLE generation unless it tests the generation reset itself: same value in,
    // lastGeneration pre-seeded to it (the poll has already seen one tick at this generation).
    private static bool Confirm(
        bool isRunning, bool isPlaying, bool isScrubbing, bool seekInFlight, ref int ticks)
    {
        var lastGeneration = 7;
        return MediaPlayerViewModel.ConfirmShowSessionEnded(
            isRunning, isPlaying, isScrubbing, seekInFlight,
            timelineGeneration: 7, ref lastGeneration, ref ticks);
    }

    [Fact]
    public void RunningClip_IsNeverEnded_AndResetsTheCounter()
    {
        var ticks = 1; // pretend a prior transient started counting
        Assert.False(Confirm(isRunning: true, isPlaying: true, isScrubbing: false, seekInFlight: false, ref ticks));
        Assert.Equal(0, ticks); // a running tick clears any in-flight count
    }

    [Fact]
    public void SingleStoppedTick_IsNotYetEnded_ButAccumulates()
    {
        var ticks = 0;
        // First stopped-while-playing tick: not enough to conclude the track ended (could be a seek transient).
        Assert.False(Confirm(isRunning: false, isPlaying: true, isScrubbing: false, seekInFlight: false, ref ticks));
        Assert.Equal(1, ticks);
    }

    [Fact]
    public void TwoConsecutiveStoppedTicks_ConfirmEnd()
    {
        var ticks = 0;
        Confirm(false, true, false, false, ref ticks); // tick 1 → 1
        Assert.True(Confirm(false, true, false, false, ref ticks)); // tick 2 → end
    }

    [Fact]
    public void SeekInFlight_NeverEnds_AndResetsAnyPartialCount()
    {
        var ticks = 1; // a stopped tick was already seen
        // A seek is now in flight (the clip is transiently paused mid-reseek) — must NOT be treated as ended,
        // and the partial count is cleared so the seek's pause can't combine with a later tick to reach 2.
        Assert.False(Confirm(isRunning: false, isPlaying: true, isScrubbing: false, seekInFlight: true, ref ticks));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void ResumeInFlight_NeverEnds_AndResetsAnyPartialCount()
    {
        // Regression: resume flips the deck's IsPlaying=true immediately, but the session's Play() prefills
        // and starts the audio hardware before the clip clock runs — IsRunning stays false (same generation)
        // long enough to span poll ticks. That read as a natural end and auto-advanced; with a single-item
        // playlist the "next" item is the same item, so pause→resume restarted from the beginning. The deck
        // now raises the in-flight flag (same parameter as a seek arc) for the whole awaited resume.
        var ticks = 1; // a stopped tick was already seen before the resume began
        Assert.False(Confirm(isRunning: false, isPlaying: true, isScrubbing: false, seekInFlight: true, ref ticks));
        Assert.False(Confirm(isRunning: false, isPlaying: true, isScrubbing: false, seekInFlight: true, ref ticks));
        Assert.Equal(0, ticks); // however long the hardware start takes, no count accumulates
    }

    [Fact]
    public void Scrubbing_NeverEnds()
    {
        var ticks = 1;
        Assert.False(Confirm(isRunning: false, isPlaying: true, isScrubbing: true, seekInFlight: false, ref ticks));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void StoppedWhileNotPlaying_IsNotEnd()
    {
        // Paused deck (IsPlaying=false): the clip is stopped but the operator paused it — not an end-of-track.
        var ticks = 0;
        Assert.False(Confirm(false, false, false, false, ref ticks));
        Assert.False(Confirm(false, false, false, false, ref ticks));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void TransientSeekPause_BetweenRunningTicks_DoesNotReachEnd()
    {
        // Realistic sequence: playing → one seek-transient stopped tick → playing again. Never confirms end.
        var ticks = 0;
        Assert.False(Confirm(true, true, false, false, ref ticks));  // playing
        Assert.False(Confirm(false, true, false, false, ref ticks)); // transient
        Assert.False(Confirm(true, true, false, false, ref ticks));  // playing
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void TimelineGenerationChange_RestartsTheWindow_EvenWhenStoppedTicksWouldConfirm()
    {
        // The authoritative discontinuity path (NXT-04): a seek the deck did NOT initiate (control surface,
        // REST API) bumps the session's generation — its transient pause must restart the window even though
        // the deck's own seek-in-flight flag is false and the stopped state spans multiple ticks.
        var ticks = 1;           // one stopped tick already seen at generation 7
        var lastGeneration = 7;
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(
            isRunning: false, isPlaying: true, isScrubbing: false, seekInFlight: false,
            timelineGeneration: 8, ref lastGeneration, ref ticks)); // the external seek bumped 7 → 8
        Assert.Equal(0, ticks);            // window restarted
        Assert.Equal(8, lastGeneration);   // new generation adopted

        // Stable generation again: end still confirms after TWO fresh persistent ticks (no false immunity).
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(
            false, true, false, false, timelineGeneration: 8, ref lastGeneration, ref ticks));
        Assert.True(MediaPlayerViewModel.ConfirmShowSessionEnded(
            false, true, false, false, timelineGeneration: 8, ref lastGeneration, ref ticks));
    }

    [Fact]
    public void FirstTickAfterPollStart_AdoptsTheGenerationWithoutCounting()
    {
        // The poll's tracker starts at -1; the first tick must adopt the live generation and not count toward
        // end (whatever the run state is on that first observation).
        var ticks = 0;
        var lastGeneration = -1;
        Assert.False(MediaPlayerViewModel.ConfirmShowSessionEnded(
            isRunning: false, isPlaying: true, isScrubbing: false, seekInFlight: false,
            timelineGeneration: 3, ref lastGeneration, ref ticks));
        Assert.Equal(0, ticks);
        Assert.Equal(3, lastGeneration);
    }
}
