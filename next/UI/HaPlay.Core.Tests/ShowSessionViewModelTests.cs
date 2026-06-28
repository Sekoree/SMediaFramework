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
}
