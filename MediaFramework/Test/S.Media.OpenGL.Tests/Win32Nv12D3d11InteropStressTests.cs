using S.Media.OpenGL;
using S.Media.OpenGL.Internal;
using Vortice.Direct3D11;
using Xunit;

namespace S.Media.OpenGL.Tests;

/// <summary>
/// Optional lab stress for D3D11 helper churn (no GL context). Set <c>RUN_WIN32_NV12_D3D11_INTEROP_STRESS=1</c>; keep off in CI.
/// Optional round count: <c>WIN32_NV12_D3D11_INTEROP_STRESS_ROUNDS</c> (clamped <c>1_000</c>–<c>500_000</c>, default <c>20_000</c>).
/// Optional keyed-mutex acquire timeout for NV12 uploads: <c>WIN32_NV12_D3D11_KEYED_MUTEX_TIMEOUT_MS</c> (see <c>Nv12Win32SharedHandleGpuUploader</c>).
/// </summary>
public sealed class Win32Nv12D3d11InteropStressTests
{
    private static bool StressEnabled =>
        OperatingSystem.IsWindows()
        && string.Equals(Environment.GetEnvironmentVariable("RUN_WIN32_NV12_D3D11_INTEROP_STRESS"), "1",
            StringComparison.Ordinal);

    private static int StressRounds()
    {
        const int def = 20_000;
        var raw = Environment.GetEnvironmentVariable("WIN32_NV12_D3D11_INTEROP_STRESS_ROUNDS");
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var v))
            return def;
        return Math.Clamp(v, 1_000, 500_000);
    }

    [Fact]
    public void D3D11GlInteropDeviceHost_RepeatedCreateValidateDispose_DoesNotThrow()
    {
        if (!StressEnabled)
            return;

        var rounds = StressRounds();
        for (var i = 0; i < rounds; i++)
        {
            using var host = D3D11GlInteropDeviceHost.TryCreateOwned(out _);
            Assert.NotNull(host);
            Assert.True(D3D11InteropUtility.TryValidateDeviceComPointer(host!.NativeComPointer, out var err), err);
            Assert.True(D3D11InteropUtility.TryGetAdapterLuid(host.NativeComPointer, out var luid));
            Assert.NotEqual(0L, luid);
        }
    }

    [Fact]
    public void KeyedMutexTexture_AcquireSync_ReleaseSync_RoundTrips()
    {
        if (!StressEnabled)
            return;

        using var host = D3D11GlInteropDeviceHost.TryCreateOwned(out _);
        if (host?.Device is null)
            return;

        ID3D11Texture2D tex;
        try
        {
            tex = host.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = 32,
                Height = 32,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.R8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
            });
        }
        catch
        {
            return;
        }

        using (tex)
        {
            using var km = tex.QueryInterfaceOrNull<Vortice.DXGI.IDXGIKeyedMutex>();
            if (km is null)
                return;

            var rounds = Math.Min(StressRounds(), 50_000);
            for (var i = 0; i < rounds; i++)
            {
                km.AcquireSync(0, 2000);
                km.ReleaseSync(0);
            }
        }
    }

    [Fact]
    public void KeyedMutexScope_TryAcquireForGpuRead_RoundTrips_WhenStressEnabled()
    {
        if (!StressEnabled)
            return;

        using var host = D3D11GlInteropDeviceHost.TryCreateOwned(out _);
        if (host?.Device is null)
            return;

        ID3D11Texture2D tex;
        try
        {
            tex = host.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = 32,
                Height = 32,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.R8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
            });
        }
        catch
        {
            return;
        }

        using (tex)
        {
            {
                using var probeKm = tex.QueryInterfaceOrNull<Vortice.DXGI.IDXGIKeyedMutex>();
                if (probeKm is null)
                    return;
            }

            var rounds = Math.Min(StressRounds(), 50_000);
            for (var i = 0; i < rounds; i++)
            {
                Assert.True(D3d11TextureKeyedMutexScope.TryAcquireForGpuRead(tex, out var scope, 2000));
                Assert.NotNull(scope);
                scope!.Dispose();
            }
        }
    }
}
