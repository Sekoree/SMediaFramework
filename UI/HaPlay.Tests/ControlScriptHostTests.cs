using HaPlay.ControlGraph;
using HaPlay.Models;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptHostTests
{
    [Fact]
    public async Task ScriptTransform_CanMapScalarAndEmitOsc()
    {
        var inputNodeId = Guid.NewGuid();
        var scriptNodeId = Guid.NewGuid();
        var outputNodeId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = inputNodeId,
                    Kind = ControlNodeKind.OscInput,
                    Settings = new OscInputControlNodeSettings { AddressPattern = "/in" },
                },
                new ControlNodeConfig
                {
                    Id = scriptNodeId,
                    Kind = ControlNodeKind.ScriptTransform,
                    Settings = new ScriptTransformControlNodeSettings
                    {
                        Source = "return emit.scalar(math.map(event.value, 0, 1, 0, 100));",
                    },
                },
                new ControlNodeConfig
                {
                    Id = outputNodeId,
                    Kind = ControlNodeKind.OscOutput,
                    Settings = new OscOutputControlNodeSettings
                    {
                        Host = "127.0.0.1",
                        Address = "/script/out",
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = inputNodeId, ToNodeId = scriptNodeId },
                new ControlConnectionConfig { FromNodeId = scriptNodeId, ToNodeId = outputNodeId },
            ],
        };
        var sender = new RecordingOscSender();
        var runtime = new ControlGraphRuntime(graph, sender);

        await runtime.InjectOscMessageAsync(inputNodeId, "/in", [OSCArgument.Float32(0.25f)]);

        var sent = Assert.Single(sender.Sent);
        Assert.Equal("/script/out", sent.Address);
        Assert.InRange(Assert.Single(sent.Arguments).AsFloat32(), 24.9f, 25.1f);
    }

    [Fact]
    public void ScriptHost_StatePersistsAcrossExecutions()
    {
        var host = new ControlScriptHost();
        var settings = new ScriptTransformControlNodeSettings
        {
            Source = "var count = state.get('count'); if (count == undefined) count = 0; count += 1; state.set('count', count); return emit.scalar(count);",
        };
        var input = new ScalarControlEvent(DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0);
        var nodeId = Guid.NewGuid();

        var first = Assert.IsType<ScalarControlEvent>(Assert.Single(host.Execute(settings, input, nodeId)));
        var second = Assert.IsType<ScalarControlEvent>(Assert.Single(host.Execute(settings, input, nodeId)));

        Assert.Equal(1, first.Value);
        Assert.Equal(2, second.Value);
    }

    [Fact]
    public void ScriptHost_RuntimeLoop_ThrowsTimeout()
    {
        var host = new ControlScriptHost();
        var settings = new ScriptTransformControlNodeSettings
        {
            Source = "while (true) { }",
            InstructionLimit = 100,
        };
        var input = new ScalarControlEvent(DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0);

        Assert.Throws<Mond.MondRuntimeException>(() => host.Execute(settings, input, Guid.NewGuid()));
    }

    [Fact]
    public void ScriptHost_CompileError_ReturnsDiagnostic()
    {
        var host = new ControlScriptHost();
        var settings = new ScriptTransformControlNodeSettings { Source = "var =" };
        var input = new ScalarControlEvent(DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0);

        var result = host.ExecuteWithDiagnostics(settings, input, Guid.NewGuid());

        Assert.Empty(result.Events);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ControlScriptDiagnosticStage.Compile, diagnostic.Stage);
    }

    [Fact]
    public async Task Runtime_ScriptFailure_IsIsolatedAndRecorded()
    {
        var inputNodeId = Guid.NewGuid();
        var scriptNodeId = Guid.NewGuid();
        var outputNodeId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = inputNodeId,
                    Kind = ControlNodeKind.OscInput,
                    Settings = new OscInputControlNodeSettings { AddressPattern = "/in" },
                },
                new ControlNodeConfig
                {
                    Id = scriptNodeId,
                    Kind = ControlNodeKind.ScriptTransform,
                    Settings = new ScriptTransformControlNodeSettings { Source = "return event.value.notCallable();" },
                },
                new ControlNodeConfig
                {
                    Id = outputNodeId,
                    Kind = ControlNodeKind.OscOutput,
                    Settings = new OscOutputControlNodeSettings { Address = "/script/out" },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = inputNodeId, ToNodeId = scriptNodeId },
                new ControlConnectionConfig { FromNodeId = scriptNodeId, ToNodeId = outputNodeId },
            ],
        };
        var sender = new RecordingOscSender();
        var runtime = new ControlGraphRuntime(graph, sender);

        await runtime.InjectOscMessageAsync(inputNodeId, "/in", [OSCArgument.Float32(0.25f)]);

        Assert.Empty(sender.Sent);
        var diagnostic = Assert.Single(runtime.ScriptDiagnostics);
        Assert.Equal(ControlScriptDiagnosticStage.Runtime, diagnostic.Stage);
    }

    private sealed class RecordingOscSender : IControlOscSender
    {
        public List<SentOscMessage> Sent { get; } = new();

        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentOscMessage(host, port, address, arguments.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentOscMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);
}
