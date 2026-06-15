using S.Media.Core.Clock;
using Xunit;

namespace S.Media.Core.Tests.Clock;

public class OutputSyncGroupTests
{
    [Fact]
    public void Disciplines_member_crystal_offset_toward_reference()
    {
        var reference = new SimClock();                      // nominal rate
        var member = new SimClock { NativeRatePpm = 40 };    // crystal runs 40 ppm fast
        using var group = new OutputSyncGroup(reference);
        var h = group.AddMember(member);
        member.CorrectionPpm = () => group.GetMemberPpm(h);

        const double dt = 0.05;                               // 50 ms control period
        for (var i = 0; i < 4000; i++)                        // 200 s simulated
        {
            reference.Advance(dt);
            member.Advance(dt);
            group.Tick(TimeSpan.FromSeconds(dt));
        }

        var phaseMs = (member.CurrentPosition - reference.CurrentPosition).TotalMilliseconds;
        Assert.True(Math.Abs(phaseMs) < 1.0, $"phase {phaseMs:F4} ms did not converge to the reference");
        // The applied correction should approach -40 ppm, cancelling the +40 ppm crystal offset.
        Assert.InRange(group.GetMemberPpm(h), -50.0, -30.0);
    }

    [Fact]
    public void Pausing_a_clock_resets_the_correction()
    {
        var reference = new SimClock();
        var member = new SimClock { NativeRatePpm = 40 };
        using var group = new OutputSyncGroup(reference);
        var h = group.AddMember(member);
        member.CorrectionPpm = () => group.GetMemberPpm(h);

        for (var i = 0; i < 400; i++) { reference.Advance(0.05); member.Advance(0.05); group.Tick(TimeSpan.FromSeconds(0.05)); }
        Assert.NotEqual(0.0, group.GetMemberPpm(h));         // locked to a non-zero correction

        member.IsRunning = false;
        group.Tick(TimeSpan.FromSeconds(0.05));
        Assert.Equal(0.0, group.GetMemberPpm(h));            // paused ⇒ no correction, controller cleared
    }

    [Fact]
    public void Large_phase_jump_seek_resets_instead_of_chasing()
    {
        var reference = new SimClock();
        var member = new SimClock { NativeRatePpm = 40 };
        using var group = new OutputSyncGroup(reference);
        var h = group.AddMember(member);
        member.CorrectionPpm = () => group.GetMemberPpm(h);

        for (var i = 0; i < 400; i++) { reference.Advance(0.05); member.Advance(0.05); group.Tick(TimeSpan.FromSeconds(0.05)); }
        Assert.NotEqual(0.0, group.GetMemberPpm(h));

        member.Jump(TimeSpan.FromSeconds(5));                // a seek: phase error well beyond ResyncThreshold
        group.Tick(TimeSpan.FromSeconds(0.05));
        Assert.Equal(0.0, group.GetMemberPpm(h));            // discontinuity ⇒ reset, not a saturated chase
    }

    /// <summary>A fake <see cref="IReadOnlyPlayhead"/> whose position advances at (1 + (native+correction) ppm).</summary>
    private sealed class SimClock : IReadOnlyPlayhead
    {
        private double _posSeconds;
        public double NativeRatePpm;
        public Func<double> CorrectionPpm = () => 0.0;
        public bool IsRunning { get; set; } = true;
        public double PlaybackRate => 1.0;
        public TimeSpan CurrentPosition => TimeSpan.FromSeconds(_posSeconds);

        public void Advance(double dtSeconds)
        {
            if (!IsRunning) return;
            var effectivePpm = NativeRatePpm + CorrectionPpm();
            _posSeconds += dtSeconds * (1.0 + effectivePpm * 1e-6);
        }

        public void Jump(TimeSpan delta) => _posSeconds += delta.TotalSeconds;
    }
}
