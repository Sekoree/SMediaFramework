using OSCLib;
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

    private sealed class NullOSCSender : IControlOSCSender
    {
        public ValueTask SendAsync(string host, int port, string address, IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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

    // The device-driving runtime (ControlSystemRuntimeSession/ControlScriptRuntimeSession) now also accepts the show
    // actions, so a script triggered by a MIDI/OSC device - not just a directly-invoked one - can drive the show.
    [Fact]
    public async Task RuntimeSessionScript_DrivesShow_WhenShowActionsWired()
    {
        var recorder = new RecordingShowActions();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Scripts =
            [
                new ControlScriptConfig
                {
                    Id = Guid.NewGuid(),
                    Name = "Drive show",
                    ScriptPath = "Scripts/show.mnd",
                    Triggers = [new ControlScriptTriggerConfig { Kind = ControlScriptTriggerKind.Manual, FunctionName = "run" }],
                },
            ],
        };
        var session = new ControlScriptRuntimeSession(
            config,
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/show.mnd"] =
                    """
                    export fun run(event, context) {
                        show.go();
                        show.fireCue("intro");
                    }
                    """,
            }),
            new NullOSCSender(),
            showActions: recorder);

        await session.DispatchManualAsync();

        Assert.Contains("go:-", recorder.Calls);
        Assert.Contains("fireCue:intro", recorder.Calls);
    }
}
