using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace S.Media.OpenGL;

/// <summary>
/// Small helpers for Win32 D3D11 ↔ GL interop: validate COM pointers and read DXGI adapter identity for diagnostics.
/// </summary>
public static class D3D11InteropUtility
{
    /// <summary>
    /// Returns true when <paramref name="deviceComPtr"/> is a usable <c>ID3D11Device</c> COM pointer (adds/releases a probe reference).
    /// </summary>
    public static bool TryValidateDeviceComPointer(nint deviceComPtr, out string? failureMessage)
    {
        failureMessage = null;
        if (!OperatingSystem.IsWindows())
        {
            failureMessage = "D3D11 is only available on Windows.";
            return false;
        }

        if (deviceComPtr == 0)
        {
            failureMessage = "null D3D11 device pointer.";
            return false;
        }

        try
        {
            using var dev = new ID3D11Device(deviceComPtr);
            if (dev.NativePointer == 0)
            {
                failureMessage = "ID3D11Device.NativePointer is null after construction.";
                return false;
            }

            _ = dev.FeatureLevel;
            return true;
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="texture2DComPtr"/> is a usable <c>ID3D11Texture2D</c> COM pointer (adds/releases a probe reference).
    /// </summary>
    public static bool TryValidateTexture2DComPointer(nint texture2DComPtr, out string? failureMessage)
    {
        failureMessage = null;
        if (!OperatingSystem.IsWindows())
        {
            failureMessage = "D3D11 is only available on Windows.";
            return false;
        }

        if (texture2DComPtr == 0)
        {
            failureMessage = "null D3D11 texture2D pointer.";
            return false;
        }

        try
        {
            using var tex = new ID3D11Texture2D(texture2DComPtr);
            if (tex.NativePointer == 0)
            {
                failureMessage = "ID3D11Texture2D.NativePointer is null after construction.";
                return false;
            }

            _ = tex.Description;
            return true;
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// DXGI adapter LUID for the adapter the device was created on (packed into <see langword="long"/> for logging / comparisons).
    /// </summary>
    public static bool TryGetAdapterLuid(nint deviceComPtr, out long adapterLuidPacked)
    {
        adapterLuidPacked = 0;
        if (!TryValidateDeviceComPointer(deviceComPtr, out _))
            return false;

        try
        {
            using var dev = new ID3D11Device(deviceComPtr);
            using var dxgiDevice = dev.QueryInterfaceOrNull<IDXGIDevice>();
            if (dxgiDevice is null)
                return false;

            using var adapter = dxgiDevice.GetAdapter();
            using var adapter1 = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
            if (adapter1 is null)
                return false;

            var desc = adapter1.Description1;
            adapterLuidPacked = PackLuid(desc.Luid);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// DXGI adapter LUID for the <see cref="ID3D11Device"/> that owns <paramref name="texture2DComPtr"/> (packed into
    /// <see langword="long"/> for logging / comparisons with <see cref="TryGetAdapterLuid"/> on the GL uploader device).
    /// </summary>
    public static bool TryGetAdapterLuidFromTexture(nint texture2DComPtr, out long adapterLuidPacked)
    {
        adapterLuidPacked = 0;
        if (!TryValidateTexture2DComPointer(texture2DComPtr, out _))
            return false;

        try
        {
            using var tex = new ID3D11Texture2D(texture2DComPtr);
            var devPtr = tex.Device?.NativePointer ?? 0;
            if (devPtr == 0)
                return false;
            return TryGetAdapterLuid(devPtr, out adapterLuidPacked);
        }
        catch
        {
            return false;
        }
    }

    private static long PackLuid(Luid luid) =>
        unchecked((long)(((ulong)(uint)luid.HighPart << 32) | luid.LowPart));
}
