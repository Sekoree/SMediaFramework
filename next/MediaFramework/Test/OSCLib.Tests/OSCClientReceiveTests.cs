using System.Net;
using System.Net.Sockets;
using OSCLib;
using Xunit;

namespace OSCLib.Tests;

public sealed class OSCClientReceiveTests
{
    [Fact]
    public async Task RegisterHandler_ReceivesReplyOnTheConnectedSocket()
    {
        // Stand-in for the X32 (an OSC server): bind a UDP socket and reply to whoever queries it.
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;

        await using var client = new OSCClient(new IPEndPoint(IPAddress.Loopback, peerPort));
        var received = new TaskCompletionSource<OSCMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RegisterHandler("//", (context, _) =>
        {
            received.TrySetResult(context.Message);
            return ValueTask.CompletedTask;
        });

        // The peer waits for the address-only query, then replies with the "current value" to the
        // query's source endpoint — i.e. the client's own connected socket.
        var peerTask = Task.Run(async () =>
        {
            var query = await peer.ReceiveAsync();
            using var encoded = OSCPacketCodec.EncodeToRented(
                OSCPacket.FromMessage(new OSCMessage("/ch/01/mix/fader", [OSCArgument.Float32(0.5f)])));
            await peer.SendAsync(encoded.Memory, query.RemoteEndPoint);
        });

        await client.SendMessageAsync("/ch/01/mix/fader"); // address-only request

        var reply = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await peerTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("/ch/01/mix/fader", reply.Address);
        Assert.Equal(0.5f, Assert.Single(reply.Arguments).AsFloat32());
    }

    [Fact]
    public async Task FixedLocalPort_BindsTheClientSourcePort()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;

        var localPort = GetFreeUdpPort();
        await using var client = new OSCClient(new IPEndPoint(IPAddress.Loopback, peerPort), localPort: localPort);
        client.RegisterHandler("//", (_, _) => ValueTask.CompletedTask); // start the receive loop

        await client.SendMessageAsync("/probe");

        var query = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(localPort, query.RemoteEndPoint.Port);
    }

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
