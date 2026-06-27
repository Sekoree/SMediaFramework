using System.Net;
using OSCLib;
using Xunit;

namespace OSCLib.Tests;

public sealed class OSCClientSendTests
{
    [Fact]
    public async Task SendAsync_ThrowsWhenEncodedPacketExceedsConfiguredMax()
    {
        await using var client = new OSCClient(
            new IPEndPoint(IPAddress.Loopback, 9),
            new OSCClientOptions { MaxPacketBytes = 16 });

        var ex = await Assert.ThrowsAsync<OSCPacketTooLargeException>(
            async () => await client.SendMessageAsync("/too-large", [OSCArgument.String("payload")]));

        Assert.True(ex.PacketBytes > ex.MaxPacketBytes);
        Assert.Equal(16, ex.MaxPacketBytes);
    }
}
