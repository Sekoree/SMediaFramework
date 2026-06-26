using Xunit;

namespace S.Media.Session.Tests;

public sealed class ShowSessionTests
{
    private static ShowDocument TwoAudioCues() => new(
        Version: 1,
        Cues: [new CueDefinition("cue1", 1, "One"), new CueDefinition("cue2", 2, "Two")],
        Clips: [new ShowClipBinding("cue1", "fake://1"), new ShowClipBinding("cue2", "fake://2")],
        Compositions: [],
        Outputs: [],
        Routes: [],
        Devices: []);

    [Fact]
    public async Task InvokeAsync_MarshalsCommandAndReturnsResult()
    {
        await using var session = new ShowSession(MediaRegistry.Build(_ => { }));
        Assert.Equal(42, await session.InvokeAsync(() => Task.FromResult(42)));
    }

    [Fact]
    public async Task InvokeAsync_IsReentrant_RunsInlineWithoutDeadlock()
    {
        await using var session = new ShowSession(MediaRegistry.Build(_ => { }));

        // A command that itself marshals onto the session thread must run inline (the dispatcher is busy
        // running us) rather than enqueue-and-await — otherwise it self-deadlocks.
        var result = await session.InvokeAsync(async () =>
            await session.InvokeAsync(() => Task.FromResult(7)));

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task InvokeAsync_FromAnotherSessionDispatcher_DoesNotRunInline()
    {
        await using var sessionA = new ShowSession(MediaRegistry.Build(_ => { }));
        await using var sessionB = new ShowSession(MediaRegistry.Build(_ => { }));

        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = sessionB.InvokeAsync(async () =>
        {
            blockerStarted.SetResult();
            await releaseB.Task;
        });
        await blockerStarted.Task;

        var ranInline = await sessionA.InvokeAsync(async () =>
        {
            var bWorkRan = false;
            var bWork = sessionB.InvokeAsync(() =>
            {
                bWorkRan = true;
                return Task.FromResult(7);
            });

            var observedInline = bWorkRan;
            releaseB.SetResult();
            Assert.Equal(7, await bWork);
            await blocker;
            return observedInline;
        });

        Assert.False(ranInline);
    }

    [Fact]
    public async Task Post_RunsActionOnSessionThread()
    {
        await using var session = new ShowSession(MediaRegistry.Build(_ => { }));
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        session.Post(() => tcs.SetResult());

        Assert.True(await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2))) == tcs.Task,
            "Post did not run the action on the session thread.");
    }

    [Fact]
    public async Task GoAsync_FiresCuesInNumberOrder_ThenReportsNotReady()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(TwoAudioCues());

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        Assert.Equal(CueExecutionStatus.NotReady, await session.GoAsync()); // past the last cue

        var log = await session.GetCueExecutionLogAsync();
        Assert.Equal(["cue1", "cue2"], log.Select(e => e.CueId));
    }

    [Fact]
    public async Task LoadDocumentAsync_ReplacesExistingDocumentOnDispatcher()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());

        var replacement = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue3", 3, "Three")],
            Clips: [new ShowClipBinding("cue3", "fake://3")],
            Compositions: [],
            Outputs: [],
            Routes: [],
            Devices: []);

        await session.LoadDocumentAsync(replacement);

        Assert.Equal(["cue3"], (await session.GetCueDefinitionsAsync()).Select(c => c.Id));
        Assert.Empty(await session.GetCueExecutionLogAsync());
        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        Assert.Equal("cue3", Assert.Single(await session.GetCueExecutionLogAsync()).CueId);
    }

    [Fact]
    public async Task FireCueAsync_FiresSpecificCue()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(TwoAudioCues());

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("cue2"));
        Assert.Equal("cue2", Assert.Single(await session.GetCueExecutionLogAsync()).CueId);
    }

    [Fact]
    public async Task SnapshotAsync_ReturnsTheGroupAfterACueFires()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(TwoAudioCues());
        await session.GoAsync();

        var snapshot = await session.SnapshotAsync();

        var group = Assert.Single(snapshot);
        Assert.Equal(ShowSession.DefaultGroup, group.GroupId);
    }

    [Fact]
    public async Task StopAsync_FreezesSessionClock()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.GoAsync();
        await Task.Delay(50);

        await session.StopAsync();
        var stopped = Assert.Single(await session.SnapshotAsync());
        await Task.Delay(50);
        var later = Assert.Single(await session.SnapshotAsync());

        Assert.False(later.IsRunning);
        Assert.Equal(stopped.SessionTime, later.SessionTime);
    }

    [Fact]
    public async Task GetCompositionStatsAsync_UnknownComposition_ReturnsNull()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        Assert.Null(await session.GetCompositionStatsAsync("nope"));
    }

    [Fact]
    public async Task ApplyCompositionMappingAsync_AppliesToKnown_RejectsUnknown()
    {
        var doc = new ShowDocument(
            Version: 1, Cues: [], Clips: [],
            Compositions: [new ShowComposition("screen", "S", 320, 240)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(doc);

        var mapping = new ClipOutputMappingSpec(
            [new ClipOutputMappingSection("s", Enabled: true, 0, 0, 1, 1, 0, 0, 320, 240)]);

        Assert.True(await session.ApplyCompositionMappingAsync("screen", mapping));
        Assert.True(await session.ApplyCompositionMappingAsync("screen", null)); // clearing is valid
        Assert.False(await session.ApplyCompositionMappingAsync("missing", mapping));
    }
}
