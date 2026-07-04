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
    public async Task FireCueAsync_CueWithoutClip_IsNotReadyInsteadOfSuccessfulNoOp()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("empty", 1, "Unbound media cue")],
            Clips: [], Compositions: [], Outputs: [], Routes: [], Devices: []));

        Assert.Equal(CueExecutionStatus.NotReady, await session.FireCueAsync("empty"));
        var entry = Assert.Single(await session.GetCueExecutionLogAsync());
        Assert.Equal(CueExecutionStatus.NotReady, entry.Status);
        Assert.All(session.Snapshot(), snapshot =>
        {
            Assert.False(snapshot.IsRunning);
            Assert.Equal(TimeSpan.Zero, snapshot.ClipDuration);
        });
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
    public async Task Snapshot_LockFree_ReflectsFireAndStop_WithoutMarshaling()
    {
        // NXT-16: the synchronous Snapshot() reads a volatile published view, not the dispatcher — it reflects
        // the fired group and then the stop, so a UI position poll never has to queue behind a long command.
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        Assert.Empty(session.Snapshot()); // nothing loaded yet

        await session.LoadDocumentAsync(TwoAudioCues());
        await session.GoAsync();
        var running = Assert.Single(session.Snapshot());
        Assert.Equal(ShowSession.DefaultGroup, running.GroupId);
        Assert.True(running.IsRunning);

        await session.StopAsync();
        Assert.False(Assert.Single(session.Snapshot()).IsRunning);
    }

    [Fact]
    public void Validator_RejectsBadVersionDuplicatesDanglingAndCycles()
    {
        // NXT-12 / NXT-07: every structural problem is reported, and a well-formed show passes.
        Assert.NotEmpty(ShowDocumentValidator.Validate(new ShowDocument(2, [], [], [], [], [], [])));

        Assert.Contains(
            ShowDocumentValidator.Validate(new ShowDocument(1,
                [new CueDefinition("a", 1, "A"), new CueDefinition("b", 1, "B")], [], [], [], [], [])),
            e => e.Contains("duplicate cue number"));

        Assert.Contains(
            ShowDocumentValidator.Validate(new ShowDocument(1,
                [new CueDefinition("a", 1, "A", FollowOnCueId: "ghost")], [], [], [], [], [])),
            e => e.Contains("unknown follow-on"));

        Assert.Contains(
            ShowDocumentValidator.Validate(new ShowDocument(1,
                [new CueDefinition("a", 1, "A", FollowOnCueId: "b", AutoContinue: true),
                 new CueDefinition("b", 2, "B", FollowOnCueId: "a", AutoContinue: true)], [], [], [], [], [])),
            e => e.Contains("cycle"));

        Assert.Contains(
            ShowDocumentValidator.Validate(new ShowDocument(1,
                [new CueDefinition("a", 1, "A")],
                [new ShowClipBinding("a", "x"), new ShowClipBinding("a", "y")], [], [], [], [])),
            e => e.Contains("more than one clip"));

        Assert.Contains(
            ShowDocumentValidator.Validate(new ShowDocument(1,
                [new CueDefinition("a", 1, "A"), new CueDefinition("a", 2, "A2")], [], [], [], [], [])),
            e => e.Contains("duplicate cue id"));

        Assert.Contains(
            ShowDocumentValidator.Validate(new ShowDocument(1,
                [new CueDefinition("a", 1, "")], [], [], [], [], [])),
            e => e.Contains("empty label"));

        Assert.Contains(
            ShowDocumentValidator.Validate(new ShowDocument(1,
                [new CueDefinition("a", 1, "A")],
                [new ShowClipBinding("a", "x", CompositionId: "ghost")], [], [], [], [])),
            e => e.Contains("unknown composition"));

        // An extra fan-out placement pointing at a non-existent composition is caught at load, not dropped silently.
        Assert.Contains(
            ShowDocumentValidator.Validate(new ShowDocument(1,
                [new CueDefinition("a", 1, "A")],
                [new ShowClipBinding("a", "x", CompositionId: "real")
                    { ExtraPlacements = [new ShowClipPlacement("ghost", 1)] }],
                [new ShowComposition("real", "Real", 320, 240)], [], [], [])),
            e => e.Contains("extra placement on unknown composition"));

        Assert.Empty(ShowDocumentValidator.Validate(TwoAudioCues()));
    }

    [Fact]
    public async Task Go_SkipsDisabledCue_AndFiresNextEnabled()
    {
        // NXT-07: GO fires the next ARMED+ENABLED cue — a disabled cue is stepped over, never "fired" as a no-op.
        var doc = new ShowDocument(1,
            [new CueDefinition("a", 1, "A"),
             new CueDefinition("b", 2, "B", Enabled: false),
             new CueDefinition("c", 3, "C")],
            [new ShowClipBinding("a", "fake://a"),
             new ShowClipBinding("b", "fake://b"),
             new ShowClipBinding("c", "fake://c")],
            [], [], [], []);
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync()); // a
        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync()); // skips disabled b → fires c

        var log = await session.GetCueExecutionLogAsync();
        Assert.Contains(log, e => e.CueId == "c" && e.Status == CueExecutionStatus.Fired);
        Assert.DoesNotContain(log, e => e.CueId == "b"); // b was never attempted
    }

    [Fact]
    public async Task LoadDocument_InvalidDocument_Throws_AndLeavesRunningShowIntact()
    {
        // NXT-12: a malformed load must not tear down the live show. Fire a cue, then attempt an invalid load.
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.GoAsync();
        Assert.True(Assert.Single(session.Snapshot()).IsRunning);

        await Assert.ThrowsAsync<ShowDocumentValidationException>(
            () => session.LoadDocumentAsync(new ShowDocument(2, [], [], [], [], [], []))); // unsupported version

        // The original show is untouched: still running, still has its two cues.
        Assert.True(Assert.Single(session.Snapshot()).IsRunning);
        Assert.Equal(2, (await session.GetCueDefinitionsAsync()).Count);
    }

    [Fact]
    public async Task Stop_PreemptsLongPreWait_InsteadOfQueuingBehindIt()
    {
        // NXT-03: a cue with a long pre-wait must not park the serial dispatcher — STOP cancels the in-flight
        // fire and returns promptly instead of queuing behind the (here 30s) pre-wait.
        var doc = new ShowDocument(1,
            [new CueDefinition("slow", 1, "Slow", PreWait: TimeSpan.FromSeconds(30))],
            [new ShowClipBinding("slow", "fake://x")], [], [], [], []);
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);

        var fire = session.FireCueAsync("slow"); // begins the 30s pre-wait on the dispatcher
        await Task.Delay(150);                   // let the fire reach the pre-wait

        var stop = session.StopAsync();
        var winner = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(stop, winner);               // STOP returned well before the 30s pre-wait would elapse
        await stop;

        Assert.Equal(CueExecutionStatus.Failed, await fire); // the fire was cancelled, not run
    }

    [Fact]
    public async Task Stop_PreemptsABlockedColdOpen_InsteadOfWaitingForIt()
    {
        // NXT-03: a STOP cancels an in-flight COLD clip open (here a provider that blocks until cancelled), so
        // STOP returns bounded instead of waiting for the open — the cue token now threads to the media open.
        await using var session = new ShowSession(BlockingOpenProvider.Registry());
        await session.LoadDocumentAsync(new ShowDocument(1,
            [new CueDefinition("c", 1, "C")],
            [new ShowClipBinding("c", "blocking://x")],
            [], [], [], []));

        var fire = session.FireCueAsync("c"); // cold open blocks until cancelled
        await Task.Delay(150);                 // let the fire reach the (blocked) open

        var stop = session.StopAsync();
        var winner = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(stop, winner);             // STOP returned without waiting out the (infinite) open
        await stop;

        Assert.Equal(CueExecutionStatus.Failed, await fire); // the open was aborted, the fire cancelled
    }

    [Fact]
    public async Task Fire_RunsOffDispatcher_SoASlowOpenDoesNotParkOtherCommands()
    {
        // NXT-03 off-dispatcher: a fire's media open runs OFF the serial dispatcher, so a dispatcher-marshaled
        // command (here a seek) is NOT queued behind a slow/blocked open — the loop stays responsive.
        await using var session = new ShowSession(BlockingOpenProvider.Registry());
        await session.LoadDocumentAsync(new ShowDocument(1,
            [new CueDefinition("c", 1, "C")],
            [new ShowClipBinding("c", "blocking://x")],
            [], [], [], []));

        var fire = session.FireCueAsync("c"); // open blocks indefinitely
        await Task.Delay(150);

        var seek = session.SeekAsync(TimeSpan.FromSeconds(1)); // a real dispatcher op (no-op on the idle group)
        var winner = await Task.WhenAny(seek, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(seek, winner); // the seek ran while the fire's open was still blocked → loop not parked
        await seek;

        await session.StopAsync();                           // release the blocked fire
        Assert.Equal(CueExecutionStatus.Failed, await fire);
    }

    [Fact]
    public async Task Reload_DuringASlowFireOpen_DiscardsTheStraddlingClip()
    {
        // NXT-03 off-dispatcher: a reload bumps the show generation and cancels the in-flight fire; the fire,
        // whose open straddled the reload, discards its now-stale clip and leaves the new show clean.
        await using var session = new ShowSession(BlockingOpenProvider.Registry());
        await session.LoadDocumentAsync(new ShowDocument(1,
            [new CueDefinition("c", 1, "C")],
            [new ShowClipBinding("c", "blocking://x")],
            [], [], [], []));

        var fire = session.FireCueAsync("c"); // open blocks
        await Task.Delay(150);

        var reload = session.LoadDocumentAsync(new ShowDocument(1, [], [], [], [], [], []));
        var winner = await Task.WhenAny(reload, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(reload, winner); // the reload completed while the fire's open was blocked
        await reload;

        Assert.Equal(CueExecutionStatus.Failed, await fire);                 // the straddling fire was cancelled
        Assert.All(session.Snapshot(), s => Assert.False(s.IsRunning));      // no leftover clip is running
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
    public async Task SetPausedAsync_FreezesSessionClock_ThenResumes()
    {
        // A LONG-lived fake: natural EOF legitimately flips the player to not-running, so a pause/resume
        // test must keep the source alive through its whole window.
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 100_000));
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.GoAsync();
        await Task.Delay(50);

        await session.SetPausedAsync(true);
        var paused = Assert.Single(await session.SnapshotAsync());
        await Task.Delay(50);
        var stillPaused = Assert.Single(await session.SnapshotAsync());

        Assert.False(stillPaused.IsRunning);
        Assert.Equal(paused.SessionTime, stillPaused.SessionTime); // clock frozen while paused

        await session.SetPausedAsync(false);
        await Task.Delay(50);
        var resumed = Assert.Single(await session.SnapshotAsync());

        Assert.True(resumed.IsRunning);
        Assert.True(resumed.SessionTime > stillPaused.SessionTime); // clock advanced after resume
    }

    [Fact]
    public async Task TimelineGeneration_BumpsOnEveryDiscontinuity_AndIsStableDuringPlayback()
    {
        // The NXT-04 discontinuity contract slice: seek, pause, resume, and clip replacement each bump the
        // group's timeline generation in the snapshot; plain playback progress does not. Pollers (the deck
        // end-confirmation, the end monitor's stall check) key their transient windows off the CHANGE.
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.GoAsync();
        await Task.Delay(50);

        var fired = Assert.Single(await session.SnapshotAsync()).TimelineGeneration;

        await Task.Delay(60); // plain progress — no discontinuity
        Assert.Equal(fired, Assert.Single(await session.SnapshotAsync()).TimelineGeneration);

        await session.SeekAsync(TimeSpan.FromMilliseconds(200));
        var afterSeek = Assert.Single(await session.SnapshotAsync()).TimelineGeneration;
        Assert.True(afterSeek > fired, "seek must bump the timeline generation");

        await session.SetPausedAsync(true);
        var afterPause = Assert.Single(await session.SnapshotAsync()).TimelineGeneration;
        Assert.True(afterPause > afterSeek, "pause must bump the timeline generation");

        await session.SetPausedAsync(false);
        var afterResume = Assert.Single(await session.SnapshotAsync()).TimelineGeneration;
        Assert.True(afterResume > afterPause, "resume must bump the timeline generation");

        await session.StopAsync(fade: false);
        var afterStop = Assert.Single(await session.SnapshotAsync()).TimelineGeneration;
        Assert.True(afterStop > afterResume, "clip replacement (stop) must bump the timeline generation");
    }

    [Fact]
    public async Task TransportTimeline_PublishesTrimmedCueCoordinates_AndReanchorsOnSeek()
    {
        var binding = new ShowClipBinding("c", "fake://v")
        {
            StartOffset = TimeSpan.FromSeconds(2),
            EndOffset = TimeSpan.FromSeconds(5),
            AudioRoutes = [],
        };
        var doc = new ShowDocument(
            1,
            [new CueDefinition("c", 1, "C")],
            [binding],
            [], [], [], []);
        await using var session = new ShowSession(FakeVideoDecoderProvider.Registry(frameCount: 900));
        await session.LoadDocumentAsync(doc);
        await session.GoAsync();

        var fired = Assert.Single(session.Snapshot());
        Assert.Equal(TimeSpan.FromSeconds(2), fired.Timeline.TrimStart);
        Assert.Equal(TimeSpan.FromSeconds(25), fired.Timeline.TrimEnd);
        Assert.False(fired.Timeline.IsLive);
        Assert.Equal(RebasePolicy.Scheduled, fired.Timeline.SourceCorrelation.Policy);
        Assert.InRange(
            (fired.Timeline.CueTime - (fired.Timeline.SourceTime - fired.Timeline.TrimStart)).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));
        var cueOrigin = fired.Timeline.CueOrigin;
        var masterBeforeSeek = fired.Timeline.MasterTime;

        await session.SeekAsync(TimeSpan.FromSeconds(12));
        var sought = Assert.Single(session.Snapshot());

        Assert.True(sought.Timeline.Generation > fired.Timeline.Generation);
        Assert.Equal(cueOrigin, sought.Timeline.CueOrigin);
        Assert.InRange(
            sought.Timeline.MasterTime - masterBeforeSeek,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));
        Assert.InRange(sought.Timeline.SourceTime, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12.5));
        Assert.InRange(sought.Timeline.CueTime, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.5));
    }

    [Fact]
    public async Task LiveComposition_UsesTheTransportTimeline_AndPublishesLiveCorrelation()
    {
        var doc = new ShowDocument(
            1,
            [new CueDefinition("c", 1, "C")],
            [new ShowClipBinding("c", "live://v", CompositionId: "screen") { AudioRoutes = [] }],
            [new ShowComposition("screen", "Screen", 320, 240)],
            [], [], []);
        await using var session = new ShowSession(FakeLiveVideoDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);

        await session.GoAsync();
        var snapshot = Assert.Single(session.Snapshot());

        Assert.True(snapshot.Timeline.IsLive);
        Assert.Equal(RebasePolicy.RebaseToLatest, snapshot.Timeline.SourceCorrelation.Policy);
        Assert.True((await session.GetCompositionStatsAsync("screen"))!.Value.ClockMastered);
    }

    [Fact]
    public async Task LoadDocument_TheCAbiSmokesEmptyShowJson_OnABackendlessSession_Loads()
    {
        // The EXACT call the outbound C-ABI smoke makes (SmpSmoke EMPTY_SHOW + audioBackend: null) — this
        // regressed on CI with an NRE while the smoke step had been unreachable for a while; pin it here so
        // the managed suite catches it before the C client does.
        const string emptyShow =
            "{\"Version\":1,\"Cues\":[],\"Clips\":[],\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), audioBackend: null);

        session.LoadDocument(ShowDocument.FromJson(emptyShow));

        Assert.Empty(await session.GetCueDefinitionsAsync());
    }

    [Fact]
    public async Task SetPausedAsync_NoActiveClip_IsNoOp()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(TwoAudioCues());

        await session.SetPausedAsync(true); // nothing fired yet — must not throw

        Assert.All(await session.SnapshotAsync(), s => Assert.False(s.IsRunning));
    }

    [Fact]
    public async Task WarmUpcomingAsync_PreparesUpcomingCues()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());

        await session.WarmUpcomingAsync(); // explicit + awaited so the prepare completes deterministically

        Assert.Contains("cue1", await session.GetPreparedCueIdsAsync());
    }

    [Fact]
    public async Task GoAsync_ConsumesThePreparedClip_LeavingItArmedNotStandby()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.WarmUpcomingAsync();
        Assert.Contains("cue1", await session.GetPreparedCueIdsAsync());

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync()); // arms cue1 — takes it out of standby

        Assert.DoesNotContain("cue1", await session.GetPreparedCueIdsAsync());
    }

    [Fact]
    public async Task StopCueAsync_StopsTheActiveCue()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.GoAsync(); // fires cue1 → active + running
        Assert.True((await session.SnapshotAsync())[0].IsRunning);

        await session.StopCueAsync("cue1");

        Assert.All(await session.SnapshotAsync(), s => Assert.False(s.IsRunning));
    }

    [Fact]
    public async Task UpdateActivePlacementAsync_FalseWhenCueNotActive()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());

        // Nothing fired → cue1 isn't the active clip → no-op false (and audio cues carry no layer anyway).
        Assert.False(await session.UpdateActivePlacementAsync(
            "cue1", "comp", 0, new ShowVideoPlacement(DestWidth: 0.5, DestHeight: 0.5)));
    }

    [Fact]
    public async Task PreparedCuesChanged_FiresWhenStandbyChanges()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.WarmUpcomingAsync(); // cue1 prepared

        IReadOnlyList<ClipPreparationStatus>? fired = null;
        session.PreparedCuesChanged += s => fired = s;
        await session.GoAsync(); // arms cue1 → prepared set changes → event fires synchronously on the dispatcher

        Assert.NotNull(fired);
    }

    [Fact]
    public async Task ApplyActiveAudioMatrixAsync_AppliesToActiveCue()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), new RecordingAudioBackend());
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.GoAsync(); // cue1 active, master output attached on the backend

        var identity = new float[,] { { 1f, 0f }, { 0f, 1f } };
        Assert.True(await session.ApplyActiveAudioMatrixAsync("cue1", ShowSession.MasterOutputId, identity));
        Assert.False(await session.ApplyActiveAudioMatrixAsync("nope", ShowSession.MasterOutputId, identity));
    }

    [Fact]
    public async Task Snapshot_ReportsActiveAudioFormat_ForHostMatrixSizing()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), new RecordingAudioBackend());
        await session.LoadDocumentAsync(TwoAudioCues());
        await session.GoAsync();

        var snapshot = Assert.Single(session.Snapshot());
        Assert.Equal(2, snapshot.AudioChannels);
        Assert.Equal(48_000, snapshot.AudioSampleRate);
    }

    [Fact]
    public async Task ApplyActiveAudioRoutesAsync_ReAppliesRoutesToTheActiveClip()
    {
        // NXT-06 live audio-route edit: a cue with per-clip audio routes attaches clip{i} outputs at fire; the
        // live edit re-applies each route to its clip{i} in place (via AddRoute's legacy id — NOT ApplyMatrix,
        // which would double it). Asserts the active-clip lookup + apply path; the audible result is HW-verified.
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "One")],
            Clips:
            [
                new ShowClipBinding("cue1", "fake://1")
                {
                    AudioRoutes = [new ShowClipAudioRoute(DeviceId: "hw:0", ChannelMatrix: [0, 1], Gain: 1f)],
                },
            ],
            Compositions: [], Outputs: [], Routes: [], Devices: []);
        await session.LoadDocumentAsync(doc);
        await session.GoAsync(); // cue1 active with its clip0 output attached

        var updated = new[] { new ShowClipAudioRoute(DeviceId: "hw:0", ChannelMatrix: [1, 0], Gain: 0.5f) };
        Assert.True(await session.ApplyActiveAudioRoutesAsync("cue1", updated)); // found + re-applied
        Assert.False(await session.ApplyActiveAudioRoutesAsync("nope", updated)); // not the active clip

        // A count change (an extra route vs the one live clip output) is not mis-patched live — it still returns
        // true (deferred to the next fire) rather than throwing or writing to the wrong output.
        var twoRoutes = new[]
        {
            new ShowClipAudioRoute(DeviceId: "hw:0", ChannelMatrix: [0, 1], Gain: 1f),
            new ShowClipAudioRoute(DeviceId: "hw:1", ChannelMatrix: [0, 1], Gain: 1f),
        };
        Assert.True(await session.ApplyActiveAudioRoutesAsync("cue1", twoRoutes));
    }

    [Fact]
    public async Task ClipAudioRoute_HostAudioFactory_BorrowedOutputUsed_AndNeverDisposedBySession()
    {
        // The audio-output factory seam: a host can supply a BORROWED sink for a route's device (an NDI sender's
        // audio side that must share the carrier emitting the composition's video). The session must WIRE it but
        // never dispose it — only run its Release hook on teardown — mirroring the video-lease ownership contract.
        var borrowed = new TrackingAudioOutput(new AudioFormat(48_000, 2));
        var released = 0;
        await using var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(),
            new RecordingAudioBackend(),
            audioOutputFactory: (deviceId, _) => deviceId == "ndi:cam"
                ? new ClipAudioOutputLease(borrowed, DisposeOutputOnRuntimeDispose: false,
                    Release: () => Interlocked.Increment(ref released))
                : null);
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "One")],
            Clips:
            [
                new ShowClipBinding("cue1", "fake://1")
                {
                    AudioRoutes = [new ShowClipAudioRoute(DeviceId: "ndi:cam", ChannelMatrix: [0, 1], Gain: 1f)],
                },
            ],
            Compositions: [], Outputs: [], Routes: [], Devices: []);
        await session.LoadDocumentAsync(doc);
        await session.GoAsync();

        // The route wired to the host's device (so the backend was NOT asked to create "ndi:cam").
        Assert.True(session.GetActiveAudioPumpStatsByDevice().ContainsKey("ndi:cam"),
            "the route must be wired to the host-provided output's device");
        // The allocation-free single-device lookup (the UI health polls) agrees with the dictionary view.
        Assert.True(session.TryGetActiveAudioPumpStats("ndi:cam", out _));
        Assert.False(session.TryGetActiveAudioPumpStats("no-such-device", out _));

        await session.StopAsync(fade: false);

        Assert.Equal(0, borrowed.DisposeCount);        // borrowed → the session never disposes it
        Assert.True(released >= 1, "the lease's Release hook must run on teardown");
        Assert.False(session.GetActiveAudioPumpStatsByDevice().ContainsKey("ndi:cam")); // cleared on stop
        Assert.False(session.TryGetActiveAudioPumpStats("ndi:cam", out _)); // cleared on stop
    }

    [Fact]
    public async Task RebuildActiveClipAudioOutputs_AddsAndRemovesDeviceOutputsLive_IncludingToZero()
    {
        // Deck hot output add/remove: rebuild the active clip's audio outputs from a fresh route set — unlike the
        // in-place ApplyActiveAudioRoutesAsync, this handles a COUNT change (a line routed/unrouted), down to ZERO
        // device outputs (the clip keeps running on its discard sink). Proven via the per-device pump snapshot.
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "One")],
            Clips:
            [
                new ShowClipBinding("cue1", "fake://1")
                {
                    AudioRoutes =
                    [
                        new ShowClipAudioRoute(DeviceId: "hw:0", ChannelMatrix: [0, 1], Gain: 1f),
                        new ShowClipAudioRoute(DeviceId: "hw:1", ChannelMatrix: [0, 1], Gain: 1f),
                    ],
                },
            ],
            Compositions: [], Outputs: [], Routes: [], Devices: []);
        await session.LoadDocumentAsync(doc);
        await session.GoAsync();

        var devices = session.GetActiveAudioPumpStatsByDevice();
        Assert.True(devices.ContainsKey("hw:0") && devices.ContainsKey("hw:1"), "both fire-time devices routed");

        // Unroute hw:1 (count drops 2→1) — the in-place path would defer this; the rebuild applies it.
        Assert.True(await session.RebuildActiveClipAudioOutputsAsync("cue1",
            [new ShowClipAudioRoute(DeviceId: "hw:0", ChannelMatrix: [0, 1], Gain: 1f)]));
        devices = session.GetActiveAudioPumpStatsByDevice();
        Assert.True(devices.ContainsKey("hw:0"));
        Assert.False(devices.ContainsKey("hw:1"), "the unrouted device is gone");

        // Unroute the LAST device (→ zero) — the clip must not fault; it runs on its discard sink.
        Assert.True(await session.RebuildActiveClipAudioOutputsAsync("cue1", []));
        Assert.Empty(session.GetActiveAudioPumpStatsByDevice());
        Assert.True(Assert.Single(session.Snapshot()).IsActive, "the clip stays active with zero device outputs");

        // Re-route a different device (→ one) — re-attaches live.
        Assert.True(await session.RebuildActiveClipAudioOutputsAsync("cue1",
            [new ShowClipAudioRoute(DeviceId: "hw:2", ChannelMatrix: [0, 1], Gain: 1f)]));
        Assert.True(session.GetActiveAudioPumpStatsByDevice().ContainsKey("hw:2"));

        Assert.False(await session.RebuildActiveClipAudioOutputsAsync("nope", [])); // not the active cue
    }

    [Fact]
    public async Task GetActiveAudioPumpStatsByDevice_ReflectsRoutedDevice_AndClearsOnStop()
    {
        // NXT-06 cutover (cue audio line-health parity): the outputs panel reverse-maps a line to its PortAudio
        // device and reads this per-device pump snapshot, so an audio-only cue line lights up. The key must appear
        // once a cue routes audio to the device and clear when the cue stops.
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "One")],
            Clips:
            [
                new ShowClipBinding("cue1", "fake://1")
                {
                    AudioRoutes = [new ShowClipAudioRoute(DeviceId: "hw:7", ChannelMatrix: [0, 1], Gain: 1f)],
                },
            ],
            Compositions: [], Outputs: [], Routes: [], Devices: []);
        await session.LoadDocumentAsync(doc);

        Assert.Empty(session.GetActiveAudioPumpStatsByDevice()); // idle → nothing driven

        await session.GoAsync(); // cue1 active with its clip0 output on device hw:7
        Assert.True(session.GetActiveAudioPumpStatsByDevice().ContainsKey("hw:7"),
            "an active cue's routed device must appear in the per-device audio-pump snapshot");

        await session.StopAllAsync();
        Assert.False(session.GetActiveAudioPumpStatsByDevice().ContainsKey("hw:7"),
            "stopping the cue must clear its device from the audio-pump snapshot");
    }

    [Fact]
    public async Task PreviewCueAsync_StartsForLoadedCue_StopCleanlyReleases()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), new RecordingAudioBackend());
        await session.LoadDocumentAsync(TwoAudioCues());

        Assert.True(await session.PreviewCueAsync("cue1"));  // loaded cue → preview opens on the preview device
        Assert.False(await session.PreviewCueAsync("nope")); // unknown cue → false (and releases the prior preview)
        await session.StopPreviewAsync();                    // must not throw
    }

    [Fact]
    public async Task SoundboardVoices_FirePolyphonicOnDevices_StopAndStopAll()
    {
        // Soundboard voices (task #10): concurrent one-shots on independent output devices, keyed by tile id.
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);

        await session.FireVoiceAsync("v1", "fake://1", deviceId: "hw:0");
        await session.FireVoiceAsync("v2", "fake://2", deviceId: "hw:1", volume: 0.5f);

        Assert.True(await session.IsVoicePlayingAsync("v1"));
        Assert.True(await session.IsVoicePlayingAsync("v2"));
        Assert.Contains("hw:0", backend.Created.Select(c => c.DeviceId));
        Assert.Contains("hw:1", backend.Created.Select(c => c.DeviceId));

        await session.StopVoiceAsync("v1");
        Assert.False(await session.IsVoicePlayingAsync("v1"));
        Assert.True(await session.IsVoicePlayingAsync("v2")); // polyphonic — stopping one leaves the others

        await session.StopAllVoicesAsync();
        Assert.False(await session.IsVoicePlayingAsync("v2"));
    }

    [Fact]
    public async Task GetCompositionStatsAsync_UnknownComposition_ReturnsNull()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        Assert.Null(await session.GetCompositionStatsAsync("nope"));
    }

    [Fact]
    public async Task SelectedSubtitles_AttachAllWithClipTime_AndDisposeOnStop()
    {
        var overlays = new List<RecordingOverlay>();
        var opened = new List<(string Path, int StreamIndex, int Width, int Height)>();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "Video")],
            Clips:
            [
                new ShowClipBinding("cue1", "fake://video", CompositionId: "screen", Subtitles:
                [
                    new ShowSubtitleSelection(StreamIndex: 7),
                    new ShowSubtitleSelection("/subs/commentary.ass"),
                ])
                {
                    StartOffset = TimeSpan.FromMilliseconds(200),
                },
            ],
            Compositions: [new ShowComposition("screen", "Screen", 4, 4)],
            Outputs: [], Routes: [], Devices: []);

        await using var session = new ShowSession(
            FakeVideoDecoderProvider.Registry(),
            subtitleFactory: (path, streamIndex, width, height) =>
            {
                opened.Add((path, streamIndex, width, height));
                var overlay = new RecordingOverlay();
                overlays.Add(overlay);
                return overlay;
            });
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        Assert.Equal(
            [("fake://video", 7, 16, 16), ("/subs/commentary.ass", -1, 16, 16)],
            opened);

        Assert.True(SpinWait.SpinUntil(
            () => overlays.All(o => o.Positions.LastOrDefault() >= TimeSpan.FromMilliseconds(200)),
            TimeSpan.FromSeconds(2)), "subtitle overlays were not driven from the transport's trimmed source time");
        var timelineSource = Assert.Single(session.Snapshot()).Timeline.SourceTime;
        Assert.All(overlays, overlay => Assert.InRange(
            (overlay.Positions[^1] - timelineSource).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250)));

        await session.StopAsync();
        Assert.All(overlays, overlay => Assert.True(overlay.IsDisposed));
    }

    [Fact]
    public async Task RoutingScene_AppliesMasterOutputChannelMatrix()
    {
        // A 2→4 channel matrix patched to the master output: the clip's 2 source channels fan out, so the
        // master output is created with 4 channels and routed through the map (the N→M application hook).
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "One")],
            Clips: [new ShowClipBinding("cue1", "fake://1")],
            Compositions: [],
            Outputs: [],
            Routes: [new OutputPatchRoute("cue1", ShowSession.MasterOutputId, ChannelMatrix: [0, 1, 0, 1])],
            Devices: []);
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());

        Assert.Equal(4, backend.LastOutputChannels);
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

    [Fact]
    public async Task ApplyOutputMappingAsync_TargetsHostLeaseId()
    {
        var output = new DiscardingVideoOutput();
        var doc = new ShowDocument(
            Version: 1, Cues: [], Clips: [],
            Compositions: [new ShowComposition("screen", "S", 320, 240)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(),
            videoOutputFactory: (_, _, _, _) =>
                [new ClipCompositionOutputLease("line-42", "Projector", output)]);
        session.LoadDocument(doc);

        var mapping = new ClipOutputMappingSpec(
            [new ClipOutputMappingSection("s", Enabled: true, 0, 0, 1, 1, 0, 0, 320, 240)]);

        Assert.True(await session.ApplyOutputMappingAsync("screen", "line-42", mapping));
        Assert.True(await session.ApplyOutputMappingAsync("screen", "line-42", null));
        Assert.False(await session.ApplyOutputMappingAsync("screen", "missing-line", mapping));
        Assert.False(await session.ApplyOutputMappingAsync("missing-comp", "line-42", mapping));
    }

    [Fact]
    public async Task AddRemoveCompositionOutput_HotAttachesAndDetachesOnLiveComposition()
    {
        // Hot add/remove output under the ShowSession path (the GUI's TryAddOutput/TryRemoveOutput re-back): a
        // LOADED composition can gain or lose an output lease WITHOUT a re-fire, so a playing deck starts/stops
        // feeding a newly-selected screen/NDI line live.
        var doc = new ShowDocument(
            Version: 1, Cues: [], Clips: [],
            Compositions: [new ShowComposition("screen", "S", 320, 240)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(doc);

        var lease = new ClipCompositionOutputLease("hot-line", "Projector", new DiscardingVideoOutput());
        Assert.True(await session.AddCompositionOutputAsync("screen", lease));         // attaches live
        Assert.True(await session.RemoveCompositionOutputAsync("screen", "hot-line")); // detaches by lease id
        Assert.False(await session.RemoveCompositionOutputAsync("screen", "hot-line")); // already gone

        Assert.False(await session.AddCompositionOutputAsync("missing", lease));        // no such composition
        Assert.False(await session.RemoveCompositionOutputAsync("missing", "hot-line"));
    }

    [Fact]
    public async Task MultiOutput_AttachesEachDeclaredGroupOutput()
    {
        // Two outputs on the "main" group: a stereo "main" (no route) + a 4-channel "monitor" (2→4 route).
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "One")],
            Clips: [new ShowClipBinding("cue1", "fake://1")],
            Compositions: [],
            Outputs: [],
            Routes: [new OutputPatchRoute("cue1", "monitor", ChannelMatrix: [0, 1, 0, 1])],
            Devices: [])
        {
            AudioOutputs = [new ShowAudioOutput("main"), new ShowAudioOutput("monitor")],
        };
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());

        Assert.Equal(2, backend.OutputCount); // both declared outputs created
        Assert.Contains(2, backend.Created.Select(c => c.Channels)); // "main" stereo
        Assert.Contains(4, backend.Created.Select(c => c.Channels)); // "monitor" remapped 2→4
    }

    [Fact]
    public async Task PerClipAudioRoutes_OverrideGroupOutputs_WithDevicesAndChannelMaps()
    {
        // The cue carries per-clip routes (GUI per-cue audio): device "hw:0" stereo + device "hw:1" 2→4.
        // Even though the group declares its own outputs, the clip must play on exactly its routed devices.
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "One")],
            Clips:
            [
                new ShowClipBinding("cue1", "fake://1")
                {
                    AudioRoutes =
                    [
                        new ShowClipAudioRoute(DeviceId: "hw:0"),
                        new ShowClipAudioRoute(
                            DeviceId: "hw:1", ChannelMatrix: [0, 1, 0, 1], SampleRate: 48_000),
                    ],
                },
            ],
            Compositions: [],
            Outputs: [],
            Routes: [],
            Devices: [])
        {
            AudioOutputs = [new ShowAudioOutput("main"), new ShowAudioOutput("monitor")],
        };
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());

        Assert.Equal(2, backend.OutputCount); // the two clip routes — NOT the (null-device) group outputs
        Assert.Contains(("hw:0", 2), backend.Created.Select(c => (c.DeviceId, c.Channels)));
        Assert.Contains(("hw:1", 4), backend.Created.Select(c => (c.DeviceId, c.Channels))); // 2→4 remap
        Assert.All(backend.Created, created => Assert.Equal(48_000, created.SampleRate));
    }

    [Fact]
    public async Task ExplicitlyEmptyClipAudioRoutes_DoNotCreateImplicitMasterOutput()
    {
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "Silent")],
            Clips:
            [
                new ShowClipBinding("cue1", "fake://1")
                {
                    AudioRoutes = [],
                },
            ],
            Compositions: [], Outputs: [], Routes: [], Devices: []);
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        Assert.Equal(0, backend.OutputCount);
    }

    [Fact]
    public async Task VideoOutputFactory_ConsultedPerComposition_AtLoad()
    {
        // The host video-output factory (the GUI's NDI/SDL/local seam) is invoked per composition at load,
        // with its id + canvas dimensions, so the host can hand back the real outputs to render onto.
        var seen = new List<(string Id, int W, int H)>();
        await using var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(),
            videoOutputFactory: (id, _, w, h) =>
            {
                seen.Add((id, w, h));
                return Array.Empty<ClipCompositionOutputLease>(); // none ⇒ headless discard, but still consulted
            });
        await session.LoadDocumentAsync(new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "C")],
            Clips: [],
            Compositions: [new ShowComposition("screen", "Screen", 1280, 720, 30, 1)],
            Outputs: [], Routes: [], Devices: []));

        Assert.Contains(("screen", 1280, 720), seen);
    }

    [Fact]
    public async Task CompositorFactory_IsConsulted_PerComposition_AtLoad()
    {
        // NXT-11: ShowSession threads the injected compositor factory into each composition, so a host with a GL
        // context can supply a GPU/warp compositor instead of the default CPU one. Prove the seam is consulted.
        var seen = new List<(int W, int H)>();
        await using var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(),
            compositorFactory: fmt =>
            {
                seen.Add((fmt.Width, fmt.Height));
                return new ClipCompositionCompositor(new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU");
            });
        await session.LoadDocumentAsync(new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "C")],
            Clips: [],
            Compositions: [new ShowComposition("screen", "Screen", 640, 480, 30, 1)],
            Outputs: [], Routes: [], Devices: []));

        Assert.Contains((640, 480), seen);
    }

    [Fact]
    public async Task CompositionBoundClip_ClockMastersTheComposition_NotFreeRunning()
    {
        // NXT-04: a composition-bound clip clock-masters its composition to the transport group, so the pump
        // follows the clip's playhead instead of free-running (showing the latest frame, no clock master).
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "C")],
            Clips: [new ShowClipBinding("c", "fake://v", CompositionId: "screen")],
            Compositions: [new ShowComposition("screen", "Screen", 320, 240, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(FakeVideoDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);

        Assert.False((await session.GetCompositionStatsAsync("screen"))!.Value.ClockMastered); // free-run before any clip

        await session.GoAsync();
        Assert.True((await session.GetCompositionStatsAsync("screen"))!.Value.ClockMastered); // now mastered to the group
    }

    [Fact]
    public async Task ClipWithMultiplePlacements_FansVideoToEveryComposition()
    {
        // A cue may place its ONE decoded source onto several compositions at once (PiP, or mirrored to a second
        // canvas). Each placement gets its own composition layer fed by the same clip, and each distinct
        // composition is clock-mastered to the group — so BOTH canvases master, not just the primary.
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "C")],
            Clips:
            [
                new ShowClipBinding("c", "fake://v", CompositionId: "a")
                {
                    ExtraPlacements = [new ShowClipPlacement("b", 0)],
                },
            ],
            Compositions:
            [
                new ShowComposition("a", "A", 320, 240, 30, 1),
                new ShowComposition("b", "B", 320, 240, 30, 1),
            ],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(FakeVideoDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);

        Assert.False((await session.GetCompositionStatsAsync("a"))!.Value.ClockMastered);
        Assert.False((await session.GetCompositionStatsAsync("b"))!.Value.ClockMastered);

        await session.GoAsync();

        Assert.True((await session.GetCompositionStatsAsync("a"))!.Value.ClockMastered);
        Assert.True((await session.GetCompositionStatsAsync("b"))!.Value.ClockMastered); // fanned to the second canvas
    }

    [Fact]
    public async Task ClipWithMultiplePlacements_TearsDownEveryLayer_OnStop()
    {
        // Fan-out must not leak: stopping the cue has to release the composition layer on EVERY canvas it fed,
        // not just the primary. A leaked layer would keep compositing a released clip's last frame forever.
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "C")],
            Clips:
            [
                new ShowClipBinding("c", "fake://v", CompositionId: "a")
                {
                    ExtraPlacements = [new ShowClipPlacement("b", 0)],
                    AudioRoutes = [],
                },
            ],
            Compositions:
            [
                new ShowComposition("a", "A", 320, 240, 30, 1),
                new ShowComposition("b", "B", 320, 240, 30, 1),
            ],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(FakeVideoDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);

        await session.GoAsync();
        Assert.Equal(1, (await session.GetCompositionStatsAsync("a"))!.Value.LayerCount);
        Assert.Equal(1, (await session.GetCompositionStatsAsync("b"))!.Value.LayerCount);

        await session.StopAllAsync();
        Assert.Equal(0, (await session.GetCompositionStatsAsync("a"))!.Value.LayerCount); // released on both canvases
        Assert.Equal(0, (await session.GetCompositionStatsAsync("b"))!.Value.LayerCount);
    }

    [Fact]
    public async Task TestPattern_AddsReplacesAndClearsATopLayer()
    {
        // Calibration-grid injection (Gap 6): show adds one top layer, re-show reuses the same slot (still one),
        // hide removes it, and an unknown composition is rejected (the frame it was handed is disposed, not leaked).
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "C")],
            Clips: [],
            Compositions: [new ShowComposition("a", "A", 320, 240, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(FakeVideoDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);

        Assert.Equal(0, (await session.GetCompositionStatsAsync("a"))!.Value.LayerCount);
        Assert.False(await session.SetCompositionTestPatternAsync("ghost", MakeBgraFrame())); // unknown → rejected

        Assert.True(await session.SetCompositionTestPatternAsync("a", MakeBgraFrame()));
        Assert.Equal(1, (await session.GetCompositionStatsAsync("a"))!.Value.LayerCount);
        Assert.True(await session.SetCompositionTestPatternAsync("a", MakeBgraFrame())); // replace reuses the slot
        Assert.Equal(1, (await session.GetCompositionStatsAsync("a"))!.Value.LayerCount);

        Assert.True(await session.SetCompositionTestPatternAsync("a", null)); // hide
        Assert.Equal(0, (await session.GetCompositionStatsAsync("a"))!.Value.LayerCount);
    }

    [Fact]
    public async Task MultiPlacement_LivePlacementEdit_TargetsTheAddressedLayer()
    {
        // With a clip fanned onto two canvases, a live placement edit must resolve the specific (composition, layer)
        // it addresses — and REJECT a key that matches no layer rather than silently moving the wrong one.
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "C")],
            Clips:
            [
                new ShowClipBinding("c", "fake://v", CompositionId: "a", LayerIndex: 0)
                {
                    ExtraPlacements = [new ShowClipPlacement("b", 0)],
                    AudioRoutes = [],
                },
            ],
            Compositions:
            [
                new ShowComposition("a", "A", 320, 240, 30, 1),
                new ShowComposition("b", "B", 320, 240, 30, 1),
            ],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(FakeVideoDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);
        await session.GoAsync();

        var edit = new ShowVideoPlacement(DestX: 0.25, DestWidth: 0.5, DestHeight: 0.5);
        Assert.True(await session.UpdateActivePlacementAsync("c", "a", 0, edit));  // primary layer
        Assert.True(await session.UpdateActivePlacementAsync("c", "b", 0, edit));  // the fanned-out layer
        Assert.False(await session.UpdateActivePlacementAsync("c", "z", 9, edit)); // no such layer → rejected
    }

    [Fact]
    public async Task GetCompositionStats_SyncLockFree_ReflectsLoadAndRetire()
    {
        // The UI health poll reads composition throughput synchronously (no dispatcher marshaling). It must see
        // the loaded compositions, reject unknown ids, and drop them when a reload retires them.
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "C")],
            Clips: [new ShowClipBinding("c", "fake://v", CompositionId: "screen")],
            Compositions: [new ShowComposition("screen", "Screen", 320, 240, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(FakeVideoDecoderProvider.Registry());

        Assert.Null(session.GetCompositionStats("screen")); // nothing loaded yet

        await session.LoadDocumentAsync(doc);
        var stats = session.GetCompositionStats("screen");
        Assert.NotNull(stats);
        Assert.Equal("screen", stats!.Value.CompositionId);
        Assert.Null(session.GetCompositionStats("nope")); // unknown composition

        await session.LoadDocumentAsync(new ShowDocument(1, [], [], [], [], [], [])); // reload with no compositions
        Assert.Null(session.GetCompositionStats("screen")); // retired
    }

    private static VideoFrame MakeBgraFrame(int w = 4, int h = 4) =>
        new(TimeSpan.Zero, new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1)),
            [new byte[w * h * 4]], [w * 4]);

    [Fact]
    public async Task CompositionBoundClip_UsesOpenedVideoDimensionsForFullCanvasPlacement()
    {
        // The synthetic source is 4x4 while the composition is 8x8. A full-canvas placement must scale the
        // source to the entire canvas; using the canvas as the pre-open source placeholder leaves only 4x4 drawn.
        var output = new PixelRecordingVideoOutput();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "Video")],
            Clips:
            [
                new ShowClipBinding("c", "fake://v", CompositionId: "screen")
                {
                    Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"),
                },
            ],
            Compositions: [new ShowComposition("screen", "Screen", 8, 8, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(
            FakeVideoDecoderProvider.Registry(),
            videoOutputFactory: (_, _, _, _) =>
                [new ClipCompositionOutputLease("screen-out", "Screen", output)],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU"));
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The first submitted frame can precede the opened-dimension scaling on a loaded runner (bottom-right
        // still 0); every full-canvas frame thereafter fills the 8x8 canvas, so poll a few frames for opaque.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (Array.IndexOf(output.Alphas, 255) < 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Contains(255, output.Alphas);
    }

    [Fact]
    public async Task StopAllAsync_FadesCompositionLayerBeforeRelease()
    {
        var output = new PixelRecordingVideoOutput();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "Video")],
            Clips:
            [
                new ShowClipBinding("c", "fake://v", CompositionId: "screen")
                {
                    FadeOut = TimeSpan.FromMilliseconds(180),
                    Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"),
                    AudioRoutes = [],
                },
            ],
            Compositions: [new ShowComposition("screen", "Screen", 8, 8, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(
            // 30s of fake video: the STOP must always win the fade claim — with the default 1s clip a slow
            // CI runner could reach the natural fade-out window first, and the stop then returns without
            // ramping (the documented lost-claim path; flaked on the Windows runner).
            FakeVideoDecoderProvider.Registry(frameCount: 900),
            videoOutputFactory: (_, _, _, _) =>
                [new ClipCompositionOutputLease("screen-out", "Screen", output)],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU"));
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));
        output.ClearAlphas();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.StopAllAsync();
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(140), $"stop returned before its fade ({sw.Elapsed})");
        Assert.Contains(output.Alphas, alpha => alpha is > 0 and < 255);
        Assert.False(Assert.Single(await session.SnapshotAsync()).IsRunning);
    }

    [Fact]
    public async Task NaturalEnd_StartsConfiguredFadeBeforeReleasingClip()
    {
        var output = new PixelRecordingVideoOutput();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "Video")],
            Clips:
            [
                new ShowClipBinding("c", "fake://v", CompositionId: "screen")
                {
                    FadeOut = TimeSpan.FromMilliseconds(250),
                    Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"),
                    AudioRoutes = [],
                },
            ],
            Compositions: [new ShowComposition("screen", "Screen", 8, 8, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(
            FakeVideoDecoderProvider.Registry(),
            videoOutputFactory: (_, _, _, _) =>
                [new ClipCompositionOutputLease("screen-out", "Screen", output)],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU"));
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));
        output.ClearAlphas();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while ((await session.SnapshotAsync()).Single().IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.False((await session.SnapshotAsync()).Single().IsRunning);
        Assert.Contains(output.Alphas, alpha => alpha is > 0 and < 255);
    }

    // A finite video clip whose out-point is trimmed early (EndOffset) so its end-of-clip behaviour fires fast.
    // The synthetic source is 1s/30fps; a 700ms trim puts the out-point at ~300ms.
    private static readonly TimeSpan EndTrim = TimeSpan.FromMilliseconds(700);

    private static (ShowDocument Doc, PixelRecordingVideoOutput Output) EndBehaviorShow(ClipEndBehavior behavior)
    {
        var output = new PixelRecordingVideoOutput();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "Video")],
            Clips:
            [
                new ShowClipBinding("c", "fake://v", CompositionId: "screen")
                {
                    EndBehavior = behavior,
                    EndOffset = EndTrim,
                    Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"),
                    AudioRoutes = [],
                },
            ],
            Compositions: [new ShowComposition("screen", "Screen", 8, 8, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        return (doc, output);
    }

    private static ShowSession EndBehaviorSession(PixelRecordingVideoOutput output) =>
        new(FakeVideoDecoderProvider.Registry(),
            videoOutputFactory: (_, _, _, _) => [new ClipCompositionOutputLease("screen-out", "Screen", output)],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU"));

    [Fact]
    public async Task NaturalEnd_Loop_KeepsRunningPastOutPoint_InsteadOfReleasing()
    {
        // NXT-07/14: ClipEndBehavior.Loop must seek back to the in-point and keep running at the out-point, not
        // release like a plain Stop. Observable: the clip is still running well past its (trimmed) out-point.
        var (doc, output) = EndBehaviorShow(ClipEndBehavior.Loop);
        await using var session = EndBehaviorSession(output);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2)); // clip started (position now advances)

        // Past the ~300ms out-point + the monitor's poll/guard: a Stop clip would be released here.
        await Task.Delay(TimeSpan.FromMilliseconds(900));
        var snap = Assert.Single(await session.SnapshotAsync());
        Assert.True(snap.IsRunning, "looping clip should still be running past its out-point");
        Assert.True(snap.ClipDuration > TimeSpan.Zero, "looping clip should still be attached");
    }

    [Fact]
    public async Task NaturalEnd_Freeze_HoldsClipInsteadOfReleasing()
    {
        // NXT-07/14: ClipEndBehavior.FreezeLastFrame pauses on the last frame — the clip stays ATTACHED (held),
        // unlike a plain Stop which releases it. Observable: IsRunning goes false but the player is still attached
        // (ClipDuration > 0), whereas a released clip reports a zero duration.
        var (doc, output) = EndBehaviorShow(ClipEndBehavior.FreezeLastFrame);
        await using var session = EndBehaviorSession(output);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        TransportSnapshot snap;
        do
        {
            await Task.Delay(25);
            snap = Assert.Single(await session.SnapshotAsync());
        }
        while (snap.IsRunning && DateTime.UtcNow < deadline);

        Assert.False(snap.IsRunning, "frozen clip should be paused at its out-point");
        Assert.True(snap.ClipDuration > TimeSpan.Zero, "frozen clip should stay attached (held), not be released");
    }

    [Fact]
    public async Task NaturalEnd_PlainStop_ReleasesClipAtOutPoint()
    {
        // The contrast case that makes the Freeze assertion meaningful: a plain Stop releases the clip at the
        // out-point, so the group's player detaches and the snapshot reports a zero clip duration.
        var (doc, output) = EndBehaviorShow(ClipEndBehavior.Stop);
        await using var session = EndBehaviorSession(output);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Terminal-state poll (see EndAtDuration test): the release that zeroes ClipDuration lands one
        // dispatcher op after IsRunning flips.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        TransportSnapshot snap;
        do
        {
            await Task.Delay(25);
            snap = Assert.Single(await session.SnapshotAsync());
        }
        while ((snap.IsRunning || snap.ClipDuration > TimeSpan.Zero) && DateTime.UtcNow < deadline);

        Assert.False(snap.IsRunning, "clip should have stopped at its out-point");
        Assert.Equal(TimeSpan.Zero, snap.ClipDuration); // released → player detached
    }

    [Fact]
    public async Task AudioExhaustionShortOfMetadataDuration_RaisesClipNaturallyEnded()
    {
        // The 2026-07-03 "loop/repeat stuck at the beginning" report, distilled: the synthetic audio
        // source EXHAUSTS after ~40 ms while its metadata claims 10 s (the VBR-overshoot shape), so the
        // position never reaches the out-point — natural end must come from the stall detection. That
        // requires MediaPlayer.IsRunning to go FALSE at router natural-EOF and Position to clamp to the
        // duration; before the fix the EOF flush rewound the clock epoch, the transport read
        // "running at 0:00" forever, and every EOF consumer (deck loop/auto-advance, this event) hung.
        var doc = ShowDocument.Empty with
        {
            Cues = [new CueDefinition("c", 1, "Audio")],
            Clips =
            [
                new ShowClipBinding("c", "fake://clip")
                {
                    NotifyNaturalEnd = true,
                    AudioRoutes = [new ShowClipAudioRoute("dev", [0, 1], 1f, 48_000)],
                },
            ],
        };
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), new RecordingAudioBackend());
        var naturallyEnded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ClipNaturallyEnded += cueId => naturallyEnded.TrySetResult(cueId);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());

        Assert.Equal("c", await naturallyEnded.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var snap = Assert.Single(await session.SnapshotAsync());
        Assert.Equal(TimeSpan.Zero, snap.ClipDuration); // released, not stuck "running at 0:00"
    }

    private static ShowDocument TwoGroupAudioCues() => new(
        Version: 1,
        Cues: [new CueDefinition("cue1", 1, "A", GroupId: "A"), new CueDefinition("cue2", 2, "B", GroupId: "B")],
        Clips: [new ShowClipBinding("cue1", "fake://1"), new ShowClipBinding("cue2", "fake://2")],
        Compositions: [], Outputs: [], Routes: [], Devices: []);

    [Fact]
    public async Task SetAllPausedAsync_PausesAndResumesEveryActiveGroup()
    {
        // NXT-04/06: pause parity with StopAllAsync. The single-group SetPausedAsync only touches one group, so a
        // multi-group cue show would keep the others running on pause — SetAllPausedAsync freezes them together.
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoGroupAudioCues());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("cue1")); // group A
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("cue2")); // group B

        // The standalone session also carries an implicit empty "main" master group; assert on the two cue groups.
        static bool Running(IReadOnlyList<TransportSnapshot> s, string g) => s.Single(x => x.GroupId == g).IsRunning;

        // IsRunning is driven by the per-group transport, which can lag a just-awaited pause/resume by a
        // scheduler tick on a loaded runner — poll the snapshot until it settles (times out → still asserts).
        async Task<IReadOnlyList<TransportSnapshot>> SettledSnapshot(Func<IReadOnlyList<TransportSnapshot>, bool> until)
        {
            // Poll WITH a yield: SnapshotAsync can complete synchronously, so a tight await-loop would hot-spin
            // and starve the pause/resume continuation on a 2-core runner (it then never settles). Delay yields.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            var s = await session.SnapshotAsync();
            while (!until(s) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(15);
                s = await session.SnapshotAsync();
            }
            return s;
        }

        var fired = await SettledSnapshot(s => Running(s, "A") && Running(s, "B"));
        Assert.True(Running(fired, "A"));
        Assert.True(Running(fired, "B"));

        // Single-group pause hits only its group — the other keeps running (the gap SetAllPausedAsync closes).
        await session.SetPausedAsync(true, "A");
        var afterOne = await SettledSnapshot(s => !Running(s, "A") && Running(s, "B"));
        Assert.False(Running(afterOne, "A"));
        Assert.True(Running(afterOne, "B"));

        // All-groups pause freezes both …
        await session.SetAllPausedAsync(true);
        var afterAll = await SettledSnapshot(s => !Running(s, "A") && !Running(s, "B"));
        Assert.False(Running(afterAll, "A"));
        Assert.False(Running(afterAll, "B"));

        // … and resumes both.
        await session.SetAllPausedAsync(false);
        var afterResume = await SettledSnapshot(s => Running(s, "A") && Running(s, "B"));
        Assert.True(Running(afterResume, "A"));
        Assert.True(Running(afterResume, "B"));
    }

    [Fact]
    public async Task FireCuesAsync_FiresEveryCueInTheGroup_Together()
    {
        // NXT-04/06: the fire-time start barrier fires a simultaneous cue group together (opens overlap), so both
        // cues end up active — vs a sequential loop that starts them staggered. Both groups run after one call.
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoGroupAudioCues());

        var statuses = await session.FireCuesAsync(["cue1", "cue2"]);
        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, s => Assert.Equal(CueExecutionStatus.Fired, s));

        static bool Running(IReadOnlyList<TransportSnapshot> s, string g) => s.Single(x => x.GroupId == g).IsRunning;
        var snap = await session.SnapshotAsync();
        Assert.True(Running(snap, "A"));
        Assert.True(Running(snap, "B"));
    }

    [Fact]
    public async Task FireCuesAsync_SingleCue_DelegatesToFireCue()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(TwoGroupAudioCues());

        var statuses = await session.FireCuesAsync(["cue1"]);
        Assert.Equal([CueExecutionStatus.Fired], statuses);
        Assert.True((await session.SnapshotAsync()).Single(x => x.GroupId == "A").IsRunning);
    }

    [Fact]
    public async Task SeekManyAsync_SeeksEachTargetedGroup_BehindOneBarrier()
    {
        // NXT-04: the group-seek barrier seeks several groups together. Both groups must land at their target — a
        // sequential loop that dropped the second group would leave it near the head.
        var outA = new PixelRecordingVideoOutput();
        var outB = new PixelRecordingVideoOutput();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("a", 1, "A", GroupId: "A"), new CueDefinition("b", 2, "B", GroupId: "B")],
            Clips:
            [
                new ShowClipBinding("a", "fake://va", CompositionId: "compA")
                    { Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"), AudioRoutes = [] },
                new ShowClipBinding("b", "fake://vb", CompositionId: "compB")
                    { Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"), AudioRoutes = [] },
            ],
            Compositions: [new ShowComposition("compA", "A", 8, 8, 30, 1), new ShowComposition("compB", "B", 8, 8, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(
            FakeVideoDecoderProvider.Registry(),
            videoOutputFactory: (compId, name, _, _) =>
                [new ClipCompositionOutputLease($"{compId}-out", name, compId == "compA" ? outA : outB)],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU"));
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("a"));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("b"));
        await outA.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await outB.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await session.SeekManyAsync([("A", TimeSpan.FromMilliseconds(600)), ("B", TimeSpan.FromMilliseconds(600))]);

        static TimeSpan Pos(IReadOnlyList<TransportSnapshot> s, string g) => s.Single(x => x.GroupId == g).ClipPosition;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        IReadOnlyList<TransportSnapshot> snap;
        do
        {
            await Task.Delay(25);
            snap = await session.SnapshotAsync();
        }
        while ((Pos(snap, "A") < TimeSpan.FromMilliseconds(450) || Pos(snap, "B") < TimeSpan.FromMilliseconds(450))
               && DateTime.UtcNow < deadline);

        Assert.True(Pos(snap, "A") >= TimeSpan.FromMilliseconds(450), $"group A did not seek forward (was {Pos(snap, "A")})");
        Assert.True(Pos(snap, "B") >= TimeSpan.FromMilliseconds(450), $"group B did not seek forward (was {Pos(snap, "B")})");
    }

    [Fact]
    public async Task EndAtDuration_StopsAHeldClip_ViaTheMonitor_WithoutSourceEof()
    {
        // NXT-06 text cue: a held (text/still) source never signals EOF, so EndAtDuration ends the clip at its
        // reported duration via the time-based monitor. This is what keeps a resize/live-edit re-read from ending
        // it early — the clip stops on the clock, not on how many frames got read.
        var output = new PixelRecordingVideoOutput();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "Text")],
            Clips:
            [
                new ShowClipBinding("c", "held://x", CompositionId: "screen")
                {
                    EndAtDuration = true,
                    Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"),
                    AudioRoutes = [],
                },
            ],
            Compositions: [new ShowComposition("screen", "Screen", 8, 8, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(
            UnboundedHeldProvider.Registry(TimeSpan.FromMilliseconds(400)),
            videoOutputFactory: (_, _, _, _) => [new ClipCompositionOutputLease("o", "Screen", output)],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU"));
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2)); // playing (source never EOFs)

        // Poll for the TERMINAL state (stopped AND released): IsRunning flips one dispatcher op before the
        // release zeroes ClipDuration, so a snapshot can observe the stop mid-teardown (flaked on CI).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        TransportSnapshot snap;
        do
        {
            await Task.Delay(25);
            snap = Assert.Single(await session.SnapshotAsync());
        }
        while ((snap.IsRunning || snap.ClipDuration > TimeSpan.Zero) && DateTime.UtcNow < deadline);

        Assert.False(snap.IsRunning);                        // stopped at ~its duration by the monitor
        Assert.Equal(TimeSpan.Zero, snap.ClipDuration);      // released (not merely frozen)
    }

    [Fact]
    public async Task NotifyNaturalEnd_BarePlainStopClip_ReleasesAndRaisesClipNaturallyEnded()
    {
        // A bare plain-Stop file cue (no trim/fade/loop) previously started NO end monitor, so at EOF it just
        // idled — cue auto-follow never fired. NotifyNaturalEnd opts the clip into the monitor: at the
        // duration out-point it is released and ClipNaturallyEnded is raised for the host's auto-follow.
        var output = new PixelRecordingVideoOutput();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "File")],
            Clips:
            [
                new ShowClipBinding("c", "held://x", CompositionId: "screen")
                {
                    NotifyNaturalEnd = true,
                    Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"),
                    AudioRoutes = [],
                },
            ],
            Compositions: [new ShowComposition("screen", "Screen", 8, 8, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(
            UnboundedHeldProvider.Registry(TimeSpan.FromMilliseconds(400)),
            videoOutputFactory: (_, _, _, _) => [new ClipCompositionOutputLease("o", "Screen", output)],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU"));
        var naturallyEnded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ClipNaturallyEnded += cueId => naturallyEnded.TrySetResult(cueId);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("c", await naturallyEnded.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var snap = Assert.Single(await session.SnapshotAsync());
        Assert.Equal(TimeSpan.Zero, snap.ClipDuration); // released, not held
    }

    [Fact]
    public async Task Snapshot_IsActive_WhileAHeldClipIsUp()
    {
        // A held (text/still) clip is on screen with a clock that may report IsRunning=false. The snapshot must
        // still say IsActive=true so the UI's end-detection poll doesn't declare it ended (which cleared now-playing
        // and let an edit's document rebuild abruptly tear it down).
        var output = new PixelRecordingVideoOutput();
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "Held")],
            Clips:
            [
                new ShowClipBinding("c", "held://x", CompositionId: "screen")
                {
                    Placement = new ShowVideoPlacement(DestWidth: 1, DestHeight: 1, Fit: "Stretch"),
                    AudioRoutes = [],
                },
            ],
            Compositions: [new ShowComposition("screen", "Screen", 8, 8, 30, 1)],
            Outputs: [], Routes: [], Devices: []);
        await using var session = new ShowSession(
            UnboundedHeldProvider.Registry(TimeSpan.FromSeconds(30)), // long — stays up for the assertion
            videoOutputFactory: (_, _, _, _) => [new ClipCompositionOutputLease("o", "Screen", output)],
            compositorFactory: fmt => new ClipCompositionCompositor(
                new S.Media.Compositor.CpuVideoCompositor(fmt), true, "TEST-CPU"));
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        await output.FirstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(Assert.Single(await session.SnapshotAsync()).IsActive);
    }

    private sealed class PixelRecordingVideoOutput : IVideoOutput
    {
        private readonly object _gate = new();
        private readonly List<int> _alphas = [];
        private VideoFormat _format;
        public TaskCompletionSource FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BottomRightAlpha { get; private set; }
        public int[] Alphas { get { lock (_gate) return _alphas.ToArray(); } }
        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [PixelFormat.Bgra32];
        public void Configure(VideoFormat format) => _format = format;
        public void Submit(VideoFrame frame)
        {
            try
            {
                var offset = (frame.Format.Height - 1) * frame.Strides[0] + (frame.Format.Width - 1) * 4 + 3;
                BottomRightAlpha = frame.Planes[0].Span[offset];
                lock (_gate) _alphas.Add(BottomRightAlpha);
                FirstFrame.TrySetResult();
            }
            finally
            {
                frame.Dispose();
            }
        }

        public void ClearAlphas()
        {
            lock (_gate) _alphas.Clear();
        }
    }

    /// <summary>An output that records how many times it was disposed — to prove the borrow contract.</summary>
    private sealed class DisposeCountingVideoOutput : IVideoOutput, IDisposable
    {
        private VideoFormat _format;
        public int DisposeCount { get; private set; }
        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = Array.Empty<PixelFormat>();
        public void Configure(VideoFormat format) => _format = format;
        public void Submit(VideoFrame frame) => frame.Dispose();
        public void Dispose() => DisposeCount++;
    }

    private static ShowDocument OneComposition() => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "C")],
        Clips: [],
        Compositions: [new ShowComposition("screen", "Screen", 320, 240, 30, 1)],
        Outputs: [], Routes: [], Devices: []);

    [Fact]
    public async Task BorrowedVideoOutput_NotDisposed_OnReloadOrDispose()
    {
        // NXT-01: a host-borrowed output (DisposeOutputOnRuntimeDispose=false) must survive document reload AND
        // session disposal — the host owns its lifetime. Disposing it would tear down the SDL/NDI window the host
        // reuses across plays (a concrete use-after-reload defect before this fix).
        var borrowed = new DisposeCountingVideoOutput();
        var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(),
            videoOutputFactory: (id, name, _, _) =>
                [new ClipCompositionOutputLease($"{id}_out", name, borrowed, DisposeOutputOnRuntimeDispose: false)]);

        await session.LoadDocumentAsync(OneComposition());
        await session.LoadDocumentAsync(OneComposition()); // reload retires the first composition's lease
        Assert.Equal(0, borrowed.DisposeCount);

        await session.DisposeAsync();
        Assert.Equal(0, borrowed.DisposeCount);
    }

    [Fact]
    public async Task SessionOwnedVideoOutput_Disposed_OnDispose()
    {
        // Complement: a lease that opts INTO disposal (e.g. a session-created output) IS disposed on retire.
        var owned = new DisposeCountingVideoOutput();
        var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(),
            videoOutputFactory: (id, name, _, _) =>
                [new ClipCompositionOutputLease($"{id}_out", name, owned, DisposeOutputOnRuntimeDispose: true)]);

        await session.LoadDocumentAsync(OneComposition());
        await session.DisposeAsync();
        Assert.True(owned.DisposeCount >= 1);
    }

    [Fact]
    public async Task RoutingScene_MatchesRouteSourceId_ForSameOutput()
    {
        // Two cues route to the same master output, each with a different matrix. The route source id is the
        // clip binding's cue id, so cue2's route must not leak onto cue1 just because it appears first.
        var doc = new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("cue1", 1, "One"), new CueDefinition("cue2", 2, "Two")],
            Clips: [new ShowClipBinding("cue1", "fake://1"), new ShowClipBinding("cue2", "fake://2")],
            Compositions: [],
            Outputs: [],
            Routes:
            [
                new OutputPatchRoute("cue2", ShowSession.MasterOutputId, ChannelMatrix: [0, 1, 0, 1, 0, 1]),
                new OutputPatchRoute("cue1", ShowSession.MasterOutputId, ChannelMatrix: [0, 1, 0, 1]),
            ],
            Devices: []);
        var backend = new RecordingAudioBackend();
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(), backend);
        await session.LoadDocumentAsync(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());
        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());

        Assert.Equal(new[] { 4, 6 }, backend.Created.Select(c => c.Channels));
    }
}

internal sealed class RecordingOverlay : IVideoOverlaySource
{
    private readonly object _gate = new();
    private readonly List<TimeSpan> _positions = [];

    public IReadOnlyList<TimeSpan> Positions
    {
        get { lock (_gate) return _positions.ToArray(); }
    }

    public bool IsDisposed { get; private set; }

    public VideoFrame? RenderAt(TimeSpan position)
    {
        lock (_gate)
            _positions.Add(position);
        return null;
    }

    public void Dispose() => IsDisposed = true;
}
