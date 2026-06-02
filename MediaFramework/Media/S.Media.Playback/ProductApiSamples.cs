namespace S.Media.Playback;

/// <summary>Small sample builders used by docs/tests to show app-level API composition.</summary>
public static class ProductApiSamples
{
    public static MediaPlayerController CreateVlcStyleFileController(string path) =>
        new(MediaGraphBuilder.File(path).Build());

    public static CueGraph CreateCuePlayerSample()
    {
        var graph = new CueGraph();
        graph.AddCue(
            new CueDefinition(
                "cue-1",
                1,
                "Intro",
                FollowOnCueId: "cue-2",
                StopTargetIds: ["program"],
                AutoContinue: true,
                PreloadKey: "intro"),
            _ => ValueTask.CompletedTask);
        graph.AddCue(
            new CueDefinition(
                "cue-2",
                2,
                "Main",
                GroupId: "show",
                FaultPolicy: CueFaultPolicy.FadeToBlackOrSilence),
            _ => ValueTask.CompletedTask);
        return graph;
    }

    public static SoundboardGrid CreateSoundboardGridSample()
    {
        var grid = new SoundboardGrid(memoryBudgetBytes: 64 * 1024 * 1024);
        grid.SetPad(new SoundboardPadDefinition("A1", "kick", "Kick", SoundboardPadMode.Retrigger));
        grid.SetPad(new SoundboardPadDefinition("A2", "loop", "Loop", SoundboardPadMode.LatchToggle, GroupId: "loops"));
        grid.BindTrigger(new SoundboardGridBinding("A1", "midi", "note36"));
        grid.SetFeedback(new SoundboardPadFeedback("A1", SoundboardLedState.Ready, "Kick"));
        grid.TryPreload("kick", 1024 * 1024);
        return grid;
    }
}
