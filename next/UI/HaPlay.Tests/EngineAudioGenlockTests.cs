using HaPlay.Playback;
using S.Media.Time;
using Xunit;

namespace HaPlay.Tests;

public sealed class EngineAudioGenlockTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    [Fact]
    public void First_device_is_the_reference_and_stays_uncorrected()
    {
        using var genlock = new EngineAudioGenlock(autoTick: false);

        Assert.False(genlock.Register(A, new SimClock()));   // first device → reference (caller leaves it unwrapped)
        Assert.False(genlock.IsActive);                       // a lone reference can't drift relative to anything
        Assert.Equal(0.0, genlock.GetPpm(A));

        Assert.True(genlock.Register(B, new SimClock()));     // second device → disciplined member
        Assert.True(genlock.IsActive);
        Assert.Equal(0.0, genlock.GetPpm(A));                 // reference never gets a correction
    }

    [Fact]
    public void Register_is_idempotent_and_returns_the_current_role()
    {
        using var genlock = new EngineAudioGenlock(autoTick: false);
        var refClock = new SimClock();
        var memberClock = new SimClock();

        Assert.False(genlock.Register(A, refClock));
        Assert.False(genlock.Register(A, refClock));          // re-register reference → still reference
        Assert.True(genlock.Register(B, memberClock));
        Assert.True(genlock.Register(B, memberClock));        // re-register member → still member
    }

    [Fact]
    public void Disciplines_a_drifting_member_toward_the_reference()
    {
        using var genlock = new EngineAudioGenlock(autoTick: false);
        var reference = new SimClock();
        var member = new SimClock { NativeRatePpm = 40 };     // member crystal runs 40 ppm fast
        member.CorrectionPpm = () => genlock.GetPpm(B);

        genlock.Register(A, reference);
        genlock.Register(B, member);

        const double dt = 0.05;
        for (var i = 0; i < 4000; i++)                        // 200 s simulated
        {
            reference.Advance(dt);
            member.Advance(dt);
            genlock.Tick(TimeSpan.FromSeconds(dt));
        }

        var phaseMs = (member.ElapsedSinceStart - reference.ElapsedSinceStart).TotalMilliseconds;
        Assert.True(Math.Abs(phaseMs) < 1.0, $"member phase {phaseMs:F4} ms did not converge");
        Assert.InRange(genlock.GetPpm(B), -50.0, -30.0);      // ~-40 ppm cancels the +40 ppm offset
    }

    [Fact]
    public void Releasing_the_reference_promotes_the_next_device()
    {
        using var genlock = new EngineAudioGenlock(autoTick: false);
        genlock.Register(A, new SimClock());                  // reference
        genlock.Register(B, new SimClock());                  // member
        Assert.True(genlock.IsActive);

        genlock.Unregister(A);                                // reference released → B promoted
        Assert.False(genlock.IsActive);                       // only B remains → nothing to discipline
        Assert.Equal(0.0, genlock.GetPpm(B));                 // B is now the (uncorrected) reference

        Assert.True(genlock.Register(C, new SimClock()));     // a new device joins B as a member
        Assert.True(genlock.IsActive);
    }

    [Fact]
    public void GetPpm_is_zero_for_unknown_and_removed_lines()
    {
        using var genlock = new EngineAudioGenlock(autoTick: false);
        genlock.Register(A, new SimClock());
        genlock.Register(B, new SimClock());

        Assert.Equal(0.0, genlock.GetPpm(Guid.NewGuid()));    // never registered
        genlock.Unregister(B);
        Assert.Equal(0.0, genlock.GetPpm(B));                 // removed
    }

    /// <summary>A fake <see cref="IPlaybackClock"/> advancing at (1 + (native+correction) ppm).</summary>
    private sealed class SimClock : IPlaybackClock
    {
        private double _posSeconds;
        public double NativeRatePpm;
        public Func<double> CorrectionPpm = () => 0.0;
        public bool IsAdvancing { get; set; } = true;
        public TimeSpan ElapsedSinceStart => TimeSpan.FromSeconds(_posSeconds);

        public void Advance(double dtSeconds)
        {
            if (!IsAdvancing) return;
            _posSeconds += dtSeconds * (1.0 + (NativeRatePpm + CorrectionPpm()) * 1e-6);
        }
    }
}
