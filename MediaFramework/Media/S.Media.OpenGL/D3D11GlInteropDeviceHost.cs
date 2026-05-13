using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace S.Media.OpenGL;

/// <summary>
/// Owns a default Direct3D 11 device suitable for <see cref="Nv12Win32SharedHandleGpuUploader"/>
/// when the host does not borrow libav's <c>ID3D11Device</c> (same creation flags as <c>SDL3GLVideoSink</c>).
/// </summary>
/// <remarks>
/// This is a small lifetime boundary for the "zero-host" backlog: callers that need a D3D11 device aligned
/// with an OpenGL adapter can create/dispose this object explicitly instead of duplicating
/// <see cref="D3D11.D3D11CreateDevice"/> calls. Adapter matching with the active GL context remains a host concern.
/// </remarks>
public sealed class D3D11GlInteropDeviceHost : IDisposable
{
    private ID3D11Device? _device;

    private D3D11GlInteropDeviceHost(ID3D11Device device) => _device = device;

    /// <summary>Non-zero COM pointer suitable for <see cref="YuvVideoRenderer"/> / <see cref="Nv12Win32SharedHandleGpuUploader.TryCreate"/>.</summary>
    public nint NativeComPointer => _device?.NativePointer ?? 0;

    /// <summary>The underlying device (throws if disposed).</summary>
    public ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(D3D11GlInteropDeviceHost));

    /// <summary>
    /// Creates a hardware D3D11 device with BGRA + video support, trying feature levels 11.1 down to 10.0.
    /// </summary>
    /// <returns><see langword="null"/> on non-Windows hosts or when creation fails (see <paramref name="failureMessage"/>).</returns>
    public static D3D11GlInteropDeviceHost? TryCreateOwned(out string? failureMessage)
    {
        failureMessage = null;
        if (!OperatingSystem.IsWindows())
        {
            failureMessage = "D3D11 is only available on Windows.";
            return null;
        }

        try
        {
            var device = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0,
                ]);
            return new D3D11GlInteropDeviceHost(device);
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Creates a D3D11 device on a specific DXGI adapter (ordinal from DXGI factory enumeration; <c>0</c> is the default adapter).
    /// Use <c>0</c> for the default adapter; higher ordinals enumerate additional GPUs.
    /// </summary>
    public static D3D11GlInteropDeviceHost? TryCreateOwnedOnAdapter(int adapterOrdinal, out string? failureMessage)
    {
        failureMessage = null;
        if (!OperatingSystem.IsWindows())
        {
            failureMessage = "D3D11 is only available on Windows.";
            return null;
        }

        if (adapterOrdinal < 0)
        {
            failureMessage = "adapterOrdinal must be non-negative.";
            return null;
        }

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory.EnumAdapters1((uint)adapterOrdinal, out var adapter).Failure)
            {
                failureMessage = $"No DXGI adapter at ordinal {adapterOrdinal}.";
                return null;
            }

            try
            {
                // Vortice overload is keyed on IDXGIAdapter (IDXGIAdapter1 is a separate generated type).
                IDXGIAdapter dxgiAdapter = adapter;
                var levels = new[]
                {
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0,
                };
                var hr = D3D11.D3D11CreateDevice(
                    dxgiAdapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    levels,
                    out var device);
                if (hr.Failure || device is null)
                {
                    failureMessage = hr.Failure ? hr.ToString() : "D3D11CreateDevice returned null device.";
                    device?.Dispose();
                    return null;
                }

                return new D3D11GlInteropDeviceHost(device);
            }
            finally
            {
                adapter?.Dispose();
            }
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            return null;
        }
    }

    public void Dispose()
    {
        _device?.Dispose();
        _device = null;
    }
}
