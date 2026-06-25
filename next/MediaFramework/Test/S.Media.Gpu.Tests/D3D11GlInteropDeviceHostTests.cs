using S.Media.Gpu;
using Xunit;

namespace S.Media.Gpu.Tests;

public sealed class D3D11GlInteropDeviceHostTests
{
    [Fact]
    public void TryCreateOwned_OnNonWindows_ReturnsNullWithMessage()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Null(D3D11GlInteropDeviceHost.TryCreateOwned(out var msg));
        Assert.False(string.IsNullOrEmpty(msg));
    }

    [Fact]
    public void TryCreateOwned_OnWindows_EitherSucceedsOrReportsFailure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var host = D3D11GlInteropDeviceHost.TryCreateOwned(out var failureMessage);
        if (host is null)
        {
            Assert.False(string.IsNullOrEmpty(failureMessage));
            return;
        }

        using (host)
        {
            Assert.NotEqual(0, host.NativeComPointer);
            Assert.NotNull(host.Device.ImmediateContext);
        }
    }

    [Fact]
    public void Dispose_CanBeCalledTwice_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var host = D3D11GlInteropDeviceHost.TryCreateOwned(out _);
        if (host is null)
            return;

        host.Dispose();
        host.Dispose();
    }

    [Fact]
    public void Device_AfterDispose_Throws()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var host = D3D11GlInteropDeviceHost.TryCreateOwned(out _);
        if (host is null)
            return;

        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => host.Device);
    }
}
