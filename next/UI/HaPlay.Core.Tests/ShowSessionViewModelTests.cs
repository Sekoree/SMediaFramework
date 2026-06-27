using HaPlay.Core;
using S.Media.Core.Registry;
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
}
