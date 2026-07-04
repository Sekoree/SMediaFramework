using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace S.Media.Gpu;

/// <summary>
/// Small helpers for Win32 D3D11 ↔ GL interop: validate COM pointers and read DXGI adapter identity for diagnostics.
/// </summary>
public static class D3D11InteropUtility
{
    /// <summary>
    /// Wraps a borrowed <see cref="ID3D11Device"/> COM pointer for the duration of a scope.
    /// The scope adds a COM reference before constructing the Vortice wrapper; dispose releases that reference.
    /// </summary>
    public readonly struct BorrowedD3D11DeviceScope : IDisposable
    {
        private readonly ID3D11Device? _device;

        public BorrowedD3D11DeviceScope(nint borrowedDeviceComPtr)
        {
            if (borrowedDeviceComPtr != 0 && OperatingSystem.IsWindows())
                _device = AddRefAndWrapDevice(borrowedDeviceComPtr);
        }

        public nint NativePointer => _device?.NativePointer ?? 0;
        public ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(BorrowedD3D11DeviceScope));

        public void Dispose() => _device?.Dispose();
    }

    /// <summary>
    /// Wraps a borrowed <see cref="ID3D11Texture2D"/> COM pointer for the duration of a scope.
    /// The scope adds a COM reference before constructing the Vortice wrapper; dispose releases that reference.
    /// </summary>
    public readonly struct BorrowedD3D11Texture2DScope : IDisposable
    {
        private readonly ID3D11Texture2D? _texture;

        public BorrowedD3D11Texture2DScope(nint borrowedTextureComPtr)
        {
            if (borrowedTextureComPtr != 0 && OperatingSystem.IsWindows())
                _texture = AddRefAndWrapTexture2D(borrowedTextureComPtr);
        }

        public nint NativePointer => _texture?.NativePointer ?? 0;
        public ID3D11Texture2D Texture => _texture ?? throw new ObjectDisposedException(nameof(BorrowedD3D11Texture2DScope));

        public void Dispose() => _texture?.Dispose();
    }

    internal static ID3D11Device AddRefAndWrapDevice(nint borrowedDeviceComPtr)
    {
        Marshal.AddRef(borrowedDeviceComPtr);
        try
        {
            return new ID3D11Device(borrowedDeviceComPtr);
        }
        catch
        {
            Marshal.Release(borrowedDeviceComPtr);
            throw;
        }
    }

    internal static ID3D11Texture2D AddRefAndWrapTexture2D(nint borrowedTextureComPtr)
    {
        Marshal.AddRef(borrowedTextureComPtr);
        try
        {
            return new ID3D11Texture2D(borrowedTextureComPtr);
        }
        catch
        {
            Marshal.Release(borrowedTextureComPtr);
            throw;
        }
    }

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
            using var borrowed = new BorrowedD3D11DeviceScope(deviceComPtr);
            var dev = borrowed.Device;
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
            using var borrowed = new BorrowedD3D11Texture2DScope(texture2DComPtr);
            var tex = borrowed.Texture;
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
        if (!OperatingSystem.IsWindows() || deviceComPtr == 0)
            return false;

        try
        {
            using var borrowed = new BorrowedD3D11DeviceScope(deviceComPtr);
            return TryGetAdapterLuid(borrowed.Device, out adapterLuidPacked);
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
        if (!OperatingSystem.IsWindows() || texture2DComPtr == 0)
            return false;

        try
        {
            using var borrowed = new BorrowedD3D11Texture2DScope(texture2DComPtr);
            using var dev = borrowed.Texture.Device;
            if (dev is null || dev.NativePointer == 0)
                return false;
            return TryGetAdapterLuid(dev, out adapterLuidPacked);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetAdapterLuid(ID3D11Device dev, out long adapterLuidPacked)
    {
        adapterLuidPacked = 0;
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

    private static long PackLuid(Luid luid) =>
        unchecked((long)(((ulong)(uint)luid.HighPart << 32) | luid.LowPart));
}
