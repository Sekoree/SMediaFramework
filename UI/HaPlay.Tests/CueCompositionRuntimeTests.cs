using HaPlay.Playback;
using HaPlay.ViewModels;
using S.Media.Core.Clock;
using Xunit;

namespace HaPlay.Tests;

public sealed class CueCompositionRuntimeTests
{
    [Fact]
    public void SetClockMaster_ThenEnsurePumpStarted_StartsExactlyOnce()
    {
        // Regression for the Phase 5.4 double-start bug — when the engine called
        // SetClockMaster (which started a slaved MediaClock) and then AddLayer
        // (which called EnsurePumpStarted), a second MediaClock + driver thread
        // would spawn because EnsurePumpStarted only checked the (always-null)
        // Stopwatch _pumpTask field. This test ensures one and only one pump
        // start happens across the typical engine call sequence.
        var outputs = new OutputManagementViewModel();
        var composition = new CueComposition { Id = Guid.NewGuid(), Name = "Test", Width = 320, Height = 180, FrameRateNum = 30, FrameRateDen = 1 };
        using var runtime = new CueCompositionRuntime(composition, [], outputs);

        runtime.SetClockMaster(new FakeMasterClock());
        runtime.EnsurePumpStarted();
        runtime.EnsurePumpStarted();
        runtime.EnsurePumpStarted();

        Assert.Equal(1, runtime.PumpStartCount);
    }

    [Fact]
    public void EnsurePumpStarted_BeforeSetClockMaster_StaysSingleStart()
    {
        // The "no master yet" path also has to stay single-shot — the runtime
        // creates one MediaClock with master=null and later swaps the master
        // in via MediaClock.SetMaster (same driver thread, same GL context).
        var outputs = new OutputManagementViewModel();
        var composition = new CueComposition { Id = Guid.NewGuid(), Name = "Test", Width = 320, Height = 180, FrameRateNum = 30, FrameRateDen = 1 };
        using var runtime = new CueCompositionRuntime(composition, [], outputs);

        runtime.EnsurePumpStarted();
        runtime.SetClockMaster(new FakeMasterClock());
        runtime.EnsurePumpStarted();

        Assert.Equal(1, runtime.PumpStartCount);
        // Stats should report the runtime as mastered after SetClockMaster.
        Assert.True(runtime.GetStats().ClockMastered);
    }

    [Fact]
    public void SetClockMaster_SecondCallIsIgnored()
    {
        // Two cues firing into the same composition can each resolve a master; the runtime
        // must take the first and ignore subsequent ones so concurrent cues don't fight for
        // the slave clock's master assignment. The pump still starts exactly once via
        // EnsurePumpStarted, regardless of how many masters tried to claim it.
        var outputs = new OutputManagementViewModel();
        var composition = new CueComposition { Id = Guid.NewGuid(), Name = "Test", Width = 320, Height = 180, FrameRateNum = 30, FrameRateDen = 1 };
        using var runtime = new CueCompositionRuntime(composition, [], outputs);

        var first = new FakeMasterClock();
        var second = new FakeMasterClock();
        runtime.SetClockMaster(first);
        runtime.SetClockMaster(second);
        runtime.EnsurePumpStarted();

        Assert.Equal(1, runtime.PumpStartCount);
        Assert.True(runtime.GetStats().ClockMastered);
    }

    [Fact]
    public void LeasedLineCount_StaysZeroWhenNdiCarrierCannotBeAcquired()
    {
        var outputs = new OutputManagementViewModel();
        var line = new OutputLineViewModel(
            new NDIOutputDefinition(
                Guid.NewGuid(),
                "NDI",
                "ndi",
                null,
                NDIOutputStreamMode.VideoAndAudio,
                AudioChannelCount: 2,
                AudioSampleRate: 48000),
            _ => { },
            outputs);
        var composition = new CueComposition { Id = Guid.NewGuid(), Name = "Test", Width = 320, Height = 180, FrameRateNum = 30, FrameRateDen = 1 };
        using var runtime = new CueCompositionRuntime(composition, [line], outputs);

        Assert.Equal(0, runtime.LeasedLineCount);
        Assert.False(runtime.DrivesLine(line.Definition.Id));
    }

    private sealed class FakeMasterClock : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => TimeSpan.Zero;
        public bool IsAdvancing => true;
    }
}
