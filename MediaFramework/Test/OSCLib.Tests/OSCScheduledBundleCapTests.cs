using System.Net;
using System.Net.Sockets;
using Xunit;

namespace OSCLib.Tests;

/// <summary>
/// The future-dated bundle path dispatches off the receive loop; these tests pin its two safety
/// guarantees: (a) at most <see cref="OSCServerOptions.MaxPendingScheduledBundles"/> dispatches are
/// pending at once - the rest are dropped and counted like oversize packets - and (b) StopAsync does
/// not return while a scheduled dispatch is still in flight (the loop CTS cancels the pending delays,
/// so stop completes promptly even with far-future time tags queued).
/// </summary>
public sealed class OSCScheduledBundleCapTests
{
    [Fact]
    public async Task FutureBundles_BeyondCap_AreDropped_AndStopAsyncDrainsPending()
    {
        const int cap = 4;
        const int sent = 10;
        await using var server = new OSCServer(new OSCServerOptions
        {
            Port = 0,
            IgnoreTimeTagScheduling = false,
            MaxPendingScheduledBundles = cap,
        });
        server.RegisterHandler("//", (_, _) => ValueTask.CompletedTask);
        await server.StartAsync();

        using var sender = new UdpClient();
        sender.Connect(IPAddress.Loopback, server.BoundPort);

        // Far-future time tag: every accepted bundle stays pending until stop cancels its delay.
        var farFuture = OSCTimeTag.FromDateTimeOffset(DateTimeOffset.UtcNow + TimeSpan.FromHours(1));
        using var encoded = OSCPacketCodec.EncodeToRented(OSCPacket.FromBundle(new OSCBundle(
            farFuture, [OSCPacket.FromMessage(new OSCMessage("/never"))])));
        var datagram = encoded.Memory.ToArray();

        for (var i = 0; i < sent; i++)
            await sender.SendAsync(datagram);

        // The receive loop handles datagrams serially, so once the drop counter reflects the overflow
        // the first `cap` bundles are pending and everything past the cap was dropped.
        await WaitUntilAsync(() => server.ScheduledBundleDropCount == sent - cap, TimeSpan.FromSeconds(5));
        Assert.Equal(sent - cap, server.ScheduledBundleDropCount);
        Assert.Equal(cap, server.PendingScheduledBundleCount);

        // StopAsync must return promptly (the pending hour-long delays observe the loop CTS) and must
        // have drained the scheduled dispatches rather than abandoning them mid-flight.
        var stop = server.StopAsync();
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5))));
        await stop;
        // The untrack continuation is not ordered against StopAsync's await, so allow it a beat.
        await WaitUntilAsync(() => server.PendingScheduledBundleCount == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.PendingScheduledBundleCount);
    }

    [Fact]
    public async Task DueBundles_AreNotSubjectToTheScheduledCap()
    {
        await using var server = new OSCServer(new OSCServerOptions
        {
            Port = 0,
            IgnoreTimeTagScheduling = false,
            MaxPendingScheduledBundles = 1,
        });
        var handled = 0;
        var allHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterHandler("/now", (_, _) =>
        {
            if (Interlocked.Increment(ref handled) == 3)
                allHandled.TrySetResult();
            return ValueTask.CompletedTask;
        });
        await server.StartAsync();

        using var sender = new UdpClient();
        sender.Connect(IPAddress.Loopback, server.BoundPort);

        // Past/immediate time tags dispatch inline on the receive loop - the pending cap must not apply.
        using var encoded = OSCPacketCodec.EncodeToRented(OSCPacket.FromBundle(new OSCBundle(
            OSCTimeTag.Immediately, [OSCPacket.FromMessage(new OSCMessage("/now"))])));
        var datagram = encoded.Memory.ToArray();
        for (var i = 0; i < 3; i++)
            await sender.SendAsync(datagram);

        await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.ScheduledBundleDropCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(10);
    }
}
