using S.Media.OpenGL;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class D3D11InteropUtilityTests
{
    [Fact]
    public void TryValidateDeviceComPointer_NullOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.False(D3D11InteropUtility.TryValidateDeviceComPointer((nint)1, out var msg));
        Assert.False(string.IsNullOrEmpty(msg));
    }

    [Fact]
    public void TryValidateDeviceComPointer_Zero_Fails()
    {
        Assert.False(D3D11InteropUtility.TryValidateDeviceComPointer(0, out var msg));
        Assert.False(string.IsNullOrEmpty(msg));
    }

    [Fact]
    public void TryValidateDeviceComPointer_OnWindows_EitherSucceedsOrFailsCleanly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var host = D3D11GlInteropDeviceHost.TryCreateOwned(out _);
        if (host is null)
            return;

        using (host)
        {
            Assert.True(D3D11InteropUtility.TryValidateDeviceComPointer(host.NativeComPointer, out var err));
            Assert.Null(err);
            Assert.True(D3D11InteropUtility.TryGetAdapterLuid(host.NativeComPointer, out var luid));
            Assert.NotEqual(0L, luid);

            using var tex = host.Device!.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
            {
                Width = 64,
                Height = 64,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.R8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = Vortice.Direct3D11.ResourceUsage.Default,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
                MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None,
            });
            Assert.True(D3D11InteropUtility.TryValidateTexture2DComPointer(tex.NativePointer, out var texErr));
            Assert.Null(texErr);
            Assert.True(D3D11InteropUtility.TryGetAdapterLuidFromTexture(tex.NativePointer, out var texLuid));
            Assert.Equal(luid, texLuid);
        }
    }
}
