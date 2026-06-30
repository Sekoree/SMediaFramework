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
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
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
                ]),
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
            () => overlays.All(o => o.Positions.Any(p => p > TimeSpan.Zero)),
            TimeSpan.FromSeconds(2)), "subtitle overlays were not driven from the active clip position");

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
                        new ShowClipAudioRoute(DeviceId: "hw:1", ChannelMatrix: [0, 1, 0, 1]),
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
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        await session.LoadDocumentAsync(doc);

        Assert.False((await session.GetCompositionStatsAsync("screen"))!.Value.ClockMastered); // free-run before any clip

        await session.GoAsync();
        Assert.True((await session.GetCompositionStatsAsync("screen"))!.Value.ClockMastered); // now mastered to the group
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
