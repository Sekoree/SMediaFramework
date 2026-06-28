using HaPlay.Core;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Session;
using Xunit;

namespace HaPlay.Core.Tests;

public class ShowSessionViewModelTests
{
    private const string EmptyShow =
        "{\"Version\":1,\"Cues\":[],\"Clips\":[],\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";

    [Fact]
    public async Task Load_then_Go_setsStatus_andTransport_withoutThrowing()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);

        vm.LoadShow(EmptyShow);
        Assert.Equal("show loaded", vm.StatusMessage);

        await vm.GoCommand.ExecuteAsync(null);

        // GO completed (empty show = no-op); no exception leaked to the bound command, and transport is queryable.
        Assert.Equal("GO", vm.StatusMessage);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void LoadShow_invalidJson_reportsViaStatus_notThrow()
    {
        var registry = MediaRegistry.Build(_ => { });
        var vm = new ShowSessionViewModel(new ShowSession(registry));

        vm.LoadShow("{ not valid json");

        Assert.StartsWith("load failed:", vm.StatusMessage);
    }

    [Fact]
    public async Task Refresh_reportsCueCount_fromLoadedShow()
    {
        const string oneCueShow =
            "{\"Version\":1,\"Cues\":[{\"Id\":\"cue1\",\"Number\":1,\"Label\":\"X\"}]," +
            "\"Clips\":[],\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);

        vm.LoadShow(oneCueShow);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.CueCount);
    }

    [Fact]
    public async Task FireSelectedCue_firesTheListSelection_withoutThrowing()
    {
        const string oneCueShow =
            "{\"Version\":1,\"Cues\":[{\"Id\":\"cue1\",\"Number\":1,\"Label\":\"X\"}]," +
            "\"Clips\":[],\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);

        vm.LoadShow(oneCueShow);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Single(vm.Cues);
        Assert.Equal("cue1", vm.Cues[0].Id);

        vm.SelectedCue = vm.Cues[0];
        await vm.FireSelectedCueCommand.ExecuteAsync(null);

        Assert.Equal("fire cue 1", vm.StatusMessage);
    }

    [Fact]
    public async Task AttachPreview_attachesWhenShowHasComposition_elseNoOp()
    {
        const string compositionShow =
            "{\"Version\":1,\"Cues\":[],\"Clips\":[]," +
            "\"Compositions\":[{\"Id\":\"screen\",\"Name\":\"S\",\"Width\":320,\"Height\":240,\"FrameRateNum\":24,\"FrameRateDen\":1}]," +
            "\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);

        vm.LoadShow(EmptyShow);
        Assert.False(await vm.AttachPreviewAsync(new DiscardingVideoOutput()));

        vm.LoadShow(compositionShow);
        Assert.True(await vm.AttachPreviewAsync(new DiscardingVideoOutput()));
    }

    [Fact]
    public async Task AddAndRemoveCue_updatesCount_andSurvivesJsonRoundTrip()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);
        vm.LoadShow(EmptyShow);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.CueCount);

        await vm.AddCueCommand.ExecuteAsync(null);
        await vm.AddCueCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.CueCount);
        Assert.Equal(2, vm.Cues.Count);

        // The edited document saves to JSON and round-trips into a fresh VM with the same cues.
        await using var session2 = new ShowSession(MediaRegistry.Build(_ => { }));
        var vm2 = new ShowSessionViewModel(session2);
        vm2.LoadShow(vm.ToShowJson());
        await vm2.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm2.CueCount);

        vm.SelectedCue = vm.Cues[0];
        await vm.RemoveSelectedCueCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.CueCount);
    }

    [Fact]
    public async Task RenameSelectedCue_updatesLabel_inListAndJson()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);
        vm.LoadShow(EmptyShow);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.AddCueCommand.ExecuteAsync(null);

        vm.SelectedCue = vm.Cues[0];
        vm.NewCueLabel = "Intro music";
        await vm.RenameSelectedCueCommand.ExecuteAsync(null);

        Assert.Equal("Intro music", vm.Cues[0].Label);
        Assert.Contains("Intro music", vm.ToShowJson());
    }

    [Fact]
    public async Task SetClipForSelectedCue_bindsMediaPath_inSavedJson()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);
        vm.LoadShow(EmptyShow);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.AddCueCommand.ExecuteAsync(null);

        vm.SelectedCue = vm.Cues[0];
        await vm.SetClipForSelectedCueAsync("/media/intro.mp4");

        Assert.Contains("/media/intro.mp4", vm.ToShowJson());
    }

    [Fact]
    public async Task MoveCueDown_reordersAndRenumbers()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);
        vm.LoadShow(EmptyShow);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.AddCueCommand.ExecuteAsync(null);
        await vm.AddCueCommand.ExecuteAsync(null);

        var firstId = vm.Cues[0].Id;
        vm.SelectedCue = vm.Cues[0];
        await vm.MoveCueDownCommand.ExecuteAsync(null);

        // The moved cue is now second (renumbered 2); the list reads 1..N in order.
        Assert.Equal(firstId, vm.Cues[1].Id);
        Assert.Equal(1, vm.Cues[0].Number);
        Assert.Equal(2, vm.Cues[1].Number);
    }

    [Fact]
    public async Task CueLog_recordsFiredCues()
    {
        const string oneCueShow =
            "{\"Version\":1,\"Cues\":[{\"Id\":\"cue1\",\"Number\":1,\"Label\":\"X\"}]," +
            "\"Clips\":[],\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);
        vm.LoadShow(oneCueShow);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.SelectedCue = vm.Cues[0];
        await vm.FireSelectedCueCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.CueLog);
    }

    [Fact]
    public async Task Seek_doesNotThrow_withoutActiveClip()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var vm = new ShowSessionViewModel(session);
        vm.LoadShow(EmptyShow);

        vm.SeekSeconds = 1.5;
        await vm.SeekCommand.ExecuteAsync(null);

        Assert.StartsWith("seek ", vm.StatusMessage);
    }
}
