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
        // query's source endpoint - i.e. the client's own connected socket.
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

        // No free-port probe/handoff: releasing a probe socket and re-binding its number is a
        // time-of-check/time-of-use race under a loaded full-suite run (review P2-1). Instead let the
        // component itself claim a port, retrying a few times if another process wins a candidate.
        var (client, localPort) = await BindClientWithFixedLocalPortAsync(peerPort);
        await using var _ = client;
        client.RegisterHandler("//", (_, _) => ValueTask.CompletedTask); // start the receive loop

        await client.SendMessageAsync("/probe");

        var query = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(localPort, query.RemoteEndPoint.Port);
    }

    private static async Task<(OSCClient Client, int LocalPort)> BindClientWithFixedLocalPortAsync(int peerPort)
    {
        SocketException? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            // Pick a candidate the OS considers free RIGHT NOW; the retry loop covers the gap
            // between releasing this probe and OSCClient binding the same number.
            int candidate;
            using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                candidate = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

            try
            {
                return (new OSCClient(new IPEndPoint(IPAddress.Loopback, peerPort), localPort: candidate), candidate);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                last = ex;
                await Task.Delay(10);
            }
        }

        throw new InvalidOperationException("could not bind any candidate local port after 10 attempts", last);
    }
}
