using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class CueGraphTests
{
    [Fact]
    public void Cues_AreOrderedAndStateCanBeUpdated()
    {
        var graph = new CueGraph();
        graph.AddCue(new CueDefinition("b", 20, "Second"), _ => ValueTask.CompletedTask);
        graph.AddCue(new CueDefinition("a", 10, "First"), _ => ValueTask.CompletedTask);

        Assert.Equal(["a", "b"], graph.Cues.Select(c => c.Id).ToArray());
        Assert.True(graph.SetCueState("a", armed: false, enabled: false));

        Assert.True(graph.TryGetCue("a", out var cue));
        Assert.False(cue.Armed);
        Assert.False(cue.Enabled);
    }

    [Fact]
    public async Task FireAsync_SkipsDisabledUnarmedAndNotReadyCues()
    {
        var graph = new CueGraph();
        var fired = 0;
        graph.AddCue(new CueDefinition("disabled", 1, "Disabled", Enabled: false), _ =>
        {
            fired++;
            return ValueTask.CompletedTask;
        });
        graph.AddCue(new CueDefinition("unarmed", 2, "Unarmed", Armed: false), _ =>
        {
            fired++;
            return ValueTask.CompletedTask;
        });
        graph.AddCue(new CueDefinition("not-ready", 3, "Not Ready"), _ =>
        {
            fired++;
            return ValueTask.CompletedTask;
        }, isReady: () => false);

        Assert.Equal(CueExecutionStatus.SkippedDisabled, await graph.FireAsync("disabled"));
        Assert.Equal(CueExecutionStatus.SkippedNotArmed, await graph.FireAsync("unarmed"));
        Assert.Equal(CueExecutionStatus.NotReady, await graph.FireAsync("not-ready"));
        Assert.Equal(0, fired);
        Assert.Equal(
            [CueExecutionStatus.SkippedDisabled, CueExecutionStatus.SkippedNotArmed, CueExecutionStatus.NotReady],
            graph.ExecutionLog.Select(e => e.Status).ToArray());
    }

    [Fact]
    public async Task FireAsync_AutoContinuesToFollowOnCue()
    {
        var graph = new CueGraph();
        var fired = new List<string>();
        graph.AddCue(
            new CueDefinition("a", 1, "A", FollowOnCueId: "b", AutoContinue: true),
            _ =>
            {
                fired.Add("a");
                return ValueTask.CompletedTask;
            });
        graph.AddCue(new CueDefinition("b", 2, "B"), _ =>
        {
            fired.Add("b");
            return ValueTask.CompletedTask;
        });

        Assert.Equal(CueExecutionStatus.Fired, await graph.FireAsync("a"));

        Assert.Equal(["a", "b"], fired);
        Assert.Equal(["a", "b"], graph.ExecutionLog.Select(e => e.CueId).ToArray());
    }

    [Fact]
    public async Task FireAsync_FaultPolicyControlsWhetherExceptionEscapes()
    {
        var graph = new CueGraph();
        graph.AddCue(
            new CueDefinition("stop", 1, "Stop", FaultPolicy: CueFaultPolicy.StopShow),
            _ => throw new InvalidOperationException("boom"));
        graph.AddCue(
            new CueDefinition("continue", 2, "Continue", FaultPolicy: CueFaultPolicy.Continue),
            _ => throw new InvalidOperationException("soft boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await graph.FireAsync("stop"));
        Assert.Equal(CueExecutionStatus.Failed, await graph.FireAsync("continue"));

        Assert.Equal([CueExecutionStatus.Failed, CueExecutionStatus.Failed], graph.ExecutionLog.Select(e => e.Status).ToArray());
    }

    [Fact]
    public void CueGraph_ExportsShowFileGroupsStopTargetsAndPrewarmKeys()
    {
        var graph = ProductApiSamples.CreateCuePlayerSample();

        Assert.Equal(["cue-2"], graph.GetGroup("show").Select(c => c.Id).ToArray());
        Assert.Equal(["program"], graph.PanicStopTargets());
        Assert.Equal(["intro"], graph.PrewarmKeys());

        var json = graph.SerializeShowFile(graph.ToShowFile(
            outputs: [new OutputPatchRoute("program", "screen")],
            routes: [new OutputPatchRoute("cue-1", "program")],
            devices: ["screen"]));
        var show = CueGraph.DeserializeShowFile(json);

        Assert.Equal(1, show.Version);
        Assert.Equal(2, show.Cues.Count);
        Assert.Single(show.Outputs);
        Assert.Single(show.Routes);
        Assert.Equal(["screen"], show.Devices);
    }
}
