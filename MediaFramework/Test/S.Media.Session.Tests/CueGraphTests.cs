using Xunit;

namespace S.Media.Session.Tests;

public sealed class CueGraphTests
{
    private static CueDefinition Cue(string id, int number, bool armed = true, bool enabled = true) =>
        new(id, number, $"Cue {number}", Armed: armed, Enabled: enabled);

    [Fact]
    public void Cues_AreOrderedByNumber()
    {
        var graph = new CueGraph();
        graph.AddCue(Cue("c", 3), _ => ValueTask.CompletedTask);
        graph.AddCue(Cue("a", 1), _ => ValueTask.CompletedTask);
        graph.AddCue(Cue("b", 2), _ => ValueTask.CompletedTask);

        Assert.Equal(["a", "b", "c"], graph.Cues.Select(c => c.Id));
    }

    [Fact]
    public void AddCue_DuplicateId_Throws()
    {
        var graph = new CueGraph();
        graph.AddCue(Cue("dup", 1), _ => ValueTask.CompletedTask);
        Assert.Throws<ArgumentException>(() => graph.AddCue(Cue("dup", 2), _ => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task FireAsync_CyclicAutoContinue_TerminatesInsteadOfRecursingForever()
    {
        // NXT-07: a cyclic auto-continue chain (a→b→a) must terminate via the cycle guard, not blow the stack.
        var graph = new CueGraph();
        var aRuns = 0;
        var bRuns = 0;
        graph.AddCue(
            new CueDefinition("a", 1, "A", AutoContinue: true, FollowOnCueId: "b", FaultPolicy: CueFaultPolicy.Continue),
            _ => { Interlocked.Increment(ref aRuns); return ValueTask.CompletedTask; });
        graph.AddCue(
            new CueDefinition("b", 2, "B", AutoContinue: true, FollowOnCueId: "a", FaultPolicy: CueFaultPolicy.Continue),
            _ => { Interlocked.Increment(ref bRuns); return ValueTask.CompletedTask; });

        var fire = graph.FireAsync("a").AsTask();
        var done = await Task.WhenAny(fire, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(fire, done); // completed - did not hang or recurse forever
        await fire;

        // Each cue ran a small bounded number of times (the guard stops before re-firing a cue in the chain).
        Assert.InRange(aRuns, 1, 2);
        Assert.InRange(bRuns, 1, 2);
    }

    [Fact]
    public async Task FireAsync_RunsActionAndLogsFired()
    {
        var graph = new CueGraph();
        var ran = 0;
        graph.AddCue(Cue("c", 1), _ => { Interlocked.Increment(ref ran); return ValueTask.CompletedTask; });

        var status = await graph.FireAsync("c");

        Assert.Equal(CueExecutionStatus.Fired, status);
        Assert.Equal(1, ran);
        var entry = Assert.Single(graph.ExecutionLog);
        Assert.Equal(CueExecutionStatus.Fired, entry.Status);
        Assert.Equal("c", entry.CueId);
    }

    [Fact]
    public async Task FireAsync_DisabledCue_SkipsWithoutRunningAction()
    {
        var graph = new CueGraph();
        var ran = false;
        graph.AddCue(Cue("c", 1, enabled: false), _ => { ran = true; return ValueTask.CompletedTask; });

        var status = await graph.FireAsync("c");

        Assert.Equal(CueExecutionStatus.SkippedDisabled, status);
        Assert.False(ran);
    }

    [Fact]
    public async Task FireAsync_NotArmedCue_Skips()
    {
        var graph = new CueGraph();
        graph.AddCue(Cue("c", 1, armed: false), _ => ValueTask.CompletedTask);

        Assert.Equal(CueExecutionStatus.SkippedNotArmed, await graph.FireAsync("c"));
    }

    [Fact]
    public async Task FireAsync_IsReadyFalse_ReturnsNotReady()
    {
        var graph = new CueGraph();
        graph.AddCue(Cue("c", 1), _ => ValueTask.CompletedTask, isReady: () => false);

        Assert.Equal(CueExecutionStatus.NotReady, await graph.FireAsync("c"));
    }

    [Fact]
    public async Task FireAsync_AutoContinue_FiresFollowOn()
    {
        var graph = new CueGraph();
        var order = new List<string>();
        graph.AddCue(
            new CueDefinition("a", 1, "A", AutoContinue: true, FollowOnCueId: "b"),
            _ => { lock (order) order.Add("a"); return ValueTask.CompletedTask; });
        graph.AddCue(Cue("b", 2), _ => { lock (order) order.Add("b"); return ValueTask.CompletedTask; });

        await graph.FireAsync("a");

        Assert.Equal(["a", "b"], order);
    }

    [Fact]
    public async Task FireAsync_FaultPolicyStopShow_Rethrows()
    {
        var graph = new CueGraph();
        graph.AddCue(
            new CueDefinition("c", 1, "C", FaultPolicy: CueFaultPolicy.StopShow),
            _ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await graph.FireAsync("c"));
        Assert.Equal(CueExecutionStatus.Failed, Assert.Single(graph.ExecutionLog).Status);
    }

    [Fact]
    public void AddCue_UnsupportedFaultPolicy_IsRejected()
    {
        var graph = new CueGraph();
        var error = Assert.Throws<NotSupportedException>(() => graph.AddCue(
            new CueDefinition("c", 1, "C", FaultPolicy: CueFaultPolicy.SkipCue),
            _ => ValueTask.CompletedTask));

        Assert.Contains(nameof(CueFaultPolicy.SkipCue), error.Message);
        Assert.Empty(graph.Cues);
    }

    [Fact]
    public void DeserializeShowFile_UnsupportedAndUnknownFaultPolicies_AreRejected()
    {
        var graph = new CueGraph();
        var json = graph.SerializeShowFile(new CueShowFile(
            1,
            [new CueDefinition("c", 1, "C")],
            [], [], []));

        Assert.Throws<NotSupportedException>(() =>
            CueGraph.DeserializeShowFile(json.Replace("\"FaultPolicy\": 0", "\"FaultPolicy\": 1")));
        var unknown = Assert.Throws<NotSupportedException>(() =>
            CueGraph.DeserializeShowFile(json.Replace("\"FaultPolicy\": 0", "\"FaultPolicy\": 999")));
        Assert.Contains("numeric value 999", unknown.Message);
    }

    [Fact]
    public void SetCueState_TogglesArmAndEnable()
    {
        var graph = new CueGraph();
        graph.AddCue(Cue("c", 1), _ => ValueTask.CompletedTask);

        Assert.True(graph.SetCueState("c", armed: false, enabled: false));
        Assert.True(graph.TryGetCue("c", out var def));
        Assert.False(def.Armed);
        Assert.False(def.Enabled);
        Assert.False(graph.SetCueState("missing", armed: true));
    }

    [Fact]
    public void PanicStopTargets_And_PrewarmKeys_AreDistinctAndSorted()
    {
        var graph = new CueGraph();
        graph.AddCue(
            new CueDefinition("a", 1, "A", StopTargetIds: ["x", "y"], PreloadKey: "k1"),
            _ => ValueTask.CompletedTask);
        graph.AddCue(
            new CueDefinition("b", 2, "B", StopTargetIds: ["y", "z"], PreloadKey: "k1"),
            _ => ValueTask.CompletedTask);

        Assert.Equal(["x", "y", "z"], graph.PanicStopTargets());
        Assert.Equal(["k1"], graph.PrewarmKeys());
    }
}
