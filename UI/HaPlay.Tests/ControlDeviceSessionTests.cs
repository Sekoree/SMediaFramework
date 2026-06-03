using HaPlay.ControlGraph;
using HaPlay.Models;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlDeviceSessionTests
{
    [Fact]
    public async Task EndpointSessionManager_SendAsync_SendsOscMessageOverUdp()
    {
        var port = GetFreeUdpPort();
        var received = new TaskCompletionSource<OSCMessageContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new OSCServer(new OSCServerOptions { Port = port });
        using var registration = server.RegisterHandler("/haplay/test", (context, _) =>
        {
            received.TrySetResult(context);
            return ValueTask.CompletedTask;
        });
        await server.StartAsync();
        await using var manager = new ControlEndpointSessionManager([]);

        await manager.SendAsync("127.0.0.1", port, "/haplay/test", [OSCArgument.Int32(42)]);

        var context = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("/haplay/test", context.Message.Address);
        var arg = Assert.Single(context.Message.Arguments);
        Assert.Equal(42, arg.AsInt32());
    }

    [Fact]
    public async Task OscInputSession_ReceivesOscMessageAndDispatchesToRuntimeSink()
    {
        var port = GetFreeUdpPort();
        var nodeId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var received = new TaskCompletionSource<(Guid NodeId, string Address, IReadOnlyList<OSCArgument> Args, Guid? OriginId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new ControlOscInputSession(
            nodeId,
            new OscInputControlNodeSettings
            {
                EndpointId = endpointId,
                LocalPort = port,
                AddressPattern = "/ch/01/mix/fader",
            },
            (id, address, args, originId, _) =>
            {
                received.TrySetResult((id, address, args, originId));
                return Task.CompletedTask;
            });
        await session.StartAsync();
        await using var client = await OSCClient.CreateAsync("127.0.0.1", port);

        await client.SendMessageAsync("/ch/01/mix/fader", [OSCArgument.Float32(0.75f)]);

        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(nodeId, result.NodeId);
        Assert.Equal(endpointId, result.OriginId);
        Assert.Equal("/ch/01/mix/fader", result.Address);
        Assert.InRange(Assert.Single(result.Args).AsFloat32(), 0.74f, 0.76f);
    }

    [Fact]
    public async Task ControlGraphSession_StartsOscInputAndRelaysToOscOutput()
    {
        var listenPort = GetFreeUdpPort();
        var outputPort = GetFreeUdpPort();
        var inputNodeId = Guid.NewGuid();
        var outputNodeId = Guid.NewGuid();
        var received = new TaskCompletionSource<OSCMessageContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new OSCServer(new OSCServerOptions { Port = outputPort });
        using var registration = server.RegisterHandler("/haplay/out", (context, _) =>
        {
            received.TrySetResult(context);
            return ValueTask.CompletedTask;
        });
        await server.StartAsync();

        var graph = new ControlGraphConfig
        {
            Name = "OSC relay",
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = inputNodeId,
                    Kind = ControlNodeKind.OscInput,
                    Settings = new OscInputControlNodeSettings
                    {
                        LocalPort = listenPort,
                        AddressPattern = "/haplay/in",
                    },
                },
                new ControlNodeConfig
                {
                    Id = outputNodeId,
                    Kind = ControlNodeKind.OscOutput,
                    Settings = new OscOutputControlNodeSettings
                    {
                        Host = "127.0.0.1",
                        Port = outputPort,
                        Address = "/haplay/out",
                        ArgumentMode = ControlOscArgumentMode.FirstScalarAsFloat,
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig { FromNodeId = inputNodeId, ToNodeId = outputNodeId },
            ],
        };
        await using var session = new ControlGraphSession(graph, []);
        await session.StartAsync();
        await using var client = await OSCClient.CreateAsync("127.0.0.1", listenPort);

        await client.SendMessageAsync("/haplay/in", [OSCArgument.Float32(0.25f)]);

        var context = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ControlSessionState.Running, session.Health.State);
        Assert.Equal("/haplay/out", context.Message.Address);
        Assert.InRange(Assert.Single(context.Message.Arguments).AsFloat32(), 0.24f, 0.26f);

        await session.StopAsync();
        Assert.Equal(ControlSessionState.Stopped, session.Health.State);
    }

    private static int GetFreeUdpPort()
    {
        using var udp = new System.Net.Sockets.UdpClient(0);
        return ((System.Net.IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
}
