using Xunit;

namespace HaPlay.Tests;

// Proves the Mond -> ShowSession bridge: a control script drives the show through the `show` global, so a MIDI/OSC
// trigger can GO / fire a cue / seek / stop. The handle resolves to the host-wired IControlShowActions (here a
// recorder); ShowSessionControlActions binds it to a real ShowSession in production.
public class ControlShowBridgeTests
{
    private sealed class RecordingShowActions : IControlShowActions
    {
        public List<string> Calls { get; } = [];
        public void Go(string? groupId = null) => Calls.Add($"go:{groupId ?? "-"}");
        public void FireCue(string cueId) => Calls.Add($"fireCue:{cueId}");
        public void Seek(TimeSpan position, string? groupId = null) => Calls.Add($"seek:{position.TotalSeconds}:{groupId ?? "-"}");
        public void Stop(string? groupId = null) => Calls.Add($"stop:{groupId ?? "-"}");
    }

    private static ControlScriptFileHost CreateHost(string scriptSource, IControlShowActions? showActions)
    {
        var services = new ControlScriptRuntimeServices(showActions: showActions);
        var sources = new InMemoryControlScriptSourceProvider(
            new Dictionary<string, string> { ["test.mnd"] = scriptSource });
        return new ControlScriptFileHost(sources, runtimeServices: services);
    }

    [Fact]
    public void ShowGlobal_DispatchesCueAndTransportActions()
    {
        var recorder = new RecordingShowActions();
        var host = CreateHost(
            "return { run: fun() { show.go(); show.fireCue('intro'); show.seek(5.0); show.stop('main'); } };",
            recorder);

        host.Invoke("test.mnd", "run");

        Assert.Equal(
            new[] { "go:-", "fireCue:intro", "seek:5:-", "stop:main" },
            recorder.Calls);
    }

    [Fact]
    public void ShowGlobal_IsNoOpWhenNoShowWired()
    {
        // No show bound -> calls are silently ignored (a control profile may be used without a show).
        var host = CreateHost("return { run: fun() { show.go(); return 1; } };", showActions: null);

        Assert.Equal(1, (int)(double)host.Invoke("test.mnd", "run"));
    }
}
