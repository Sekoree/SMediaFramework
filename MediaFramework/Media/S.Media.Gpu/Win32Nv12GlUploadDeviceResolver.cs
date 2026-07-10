using System.Threading;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;

namespace S.Media.Gpu;

/// <summary>
/// Resolves the Win32 <c>ID3D11Device</c> COM pointer used when constructing <see cref="YuvVideoRenderer"/> for NV12
/// (shared by <c>SDL3GLVideoOutput</c> and Avalonia GL video).
/// </summary>
public sealed class Win32Nv12GlUploadDeviceResolver : IDisposable
{
    private readonly nint _borrowD3D11DeviceComPtrForNv12Gl;
    private readonly bool _createFallbackD3D11InteropDeviceForWin32Nv12;
    private readonly string _logPrefix;

    private IVideoSource? _borrowVideoSourceForWin32Nv12Gl;
    private D3D11GlInteropDeviceHost? _nv12D3d11Host;

    private int _nv12BorrowAdapterDiagLogged;
    private int _nv12OwnedInteropDiagLogged;
    private int _nv12ZeroHostModeLogged;

    public Win32Nv12GlUploadDeviceResolver(nint borrowD3D11DeviceComPtrForNv12Gl,
        bool createFallbackD3D11InteropDeviceForWin32Nv12,
        string logPrefix)
    {
        _borrowD3D11DeviceComPtrForNv12Gl = borrowD3D11DeviceComPtrForNv12Gl;
        _createFallbackD3D11InteropDeviceForWin32Nv12 = createFallbackD3D11InteropDeviceForWin32Nv12;
        _logPrefix = logPrefix;
    }

    public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource) =>
        _borrowVideoSourceForWin32Nv12Gl = videoSource;

    /// <summary>Same flag passed to the ctor: when <see langword="false"/>, Win32 NV12 defers owned interop device creation.</summary>
    public bool CreatesFallbackOwnedInteropDevice => _createFallbackD3D11InteropDeviceForWin32Nv12;

    public nint ResolveDevicePointerForNv12RendererConstruction()
    {
        TryResolveWin32Nv12D3d11DeviceComPtr(out var win32D3d11DevicePtr);
        return win32D3d11DevicePtr;
    }

    /// <summary>Disposes any output-owned <see cref="D3D11GlInteropDeviceHost"/> after the GL renderer is torn down.</summary>
    public void DisposeOwnedInteropHost()
    {
        MediaDiagnostics.SwallowDisposeErrors(() => _nv12D3d11Host?.Dispose(), $"{_logPrefix}: DisposeOwnedInteropHost");
        _nv12D3d11Host = null;
    }

    public void Dispose()
    {
        DisposeOwnedInteropHost();
        _borrowVideoSourceForWin32Nv12Gl = null;
    }

    private void TryResolveWin32Nv12D3d11DeviceComPtr(out nint win32D3d11DevicePtr)
    {
        win32D3d11DevicePtr = 0;
        if (_borrowD3D11DeviceComPtrForNv12Gl != 0)
        {
            if (D3D11InteropUtility.TryValidateDeviceComPointer(_borrowD3D11DeviceComPtrForNv12Gl, out var ctorErr))
            {
                win32D3d11DevicePtr = _borrowD3D11DeviceComPtrForNv12Gl;
                return;
            }

            MediaDiagnostics.LogWarning(
                "{0}: ctor borrowD3D11DeviceComPtrForNv12Gl is not a valid ID3D11Device ({1}) - falling back to libav or owned device.",
                _logPrefix,
                ctorErr);
        }

        if (_createFallbackD3D11InteropDeviceForWin32Nv12)
        {
            if (_nv12D3d11Host is not null)
            {
                win32D3d11DevicePtr = _nv12D3d11Host.NativeComPointer;
                if (win32D3d11DevicePtr != 0)
                    return;

                DisposeOwnedInteropHost();
            }

            _nv12D3d11Host = D3D11GlInteropDeviceHost.TryCreateOwned(out var d3dErr);
            if (_nv12D3d11Host != null)
            {
                win32D3d11DevicePtr = _nv12D3d11Host.NativeComPointer;
                TryLogNv12OwnedInteropAdapterOnce(win32D3d11DevicePtr);
                return;
            }

            if (d3dErr is not null)
            {
                MediaDiagnostics.LogWarning(
                    "{0}: could not create D3D11 device for Win32 NV12 GL upload - trying libav D3D11 device instead: {1}",
                    _logPrefix,
                    d3dErr);
            }
        }

        if (_borrowVideoSourceForWin32Nv12Gl is IHardwareD3D11GlInteropSource hw
            && hw.TryGetHardwareD3D11DeviceForWin32Gl(out var libavPtr)
            && libavPtr != 0)
        {
            if (D3D11InteropUtility.TryValidateDeviceComPointer(libavPtr, out var libavErr))
            {
                win32D3d11DevicePtr = libavPtr;
                TryLogNv12D3d11BorrowAdapterOnce(hw, libavPtr);
                return;
            }

            MediaDiagnostics.LogWarning(
                "{0}: libav D3D11 device pointer is invalid ({1}){2}",
                _logPrefix,
                libavErr,
                _createFallbackD3D11InteropDeviceForWin32Nv12
                    ? " - owned interop device was unavailable."
                    : " - true zero-host mode will not create a output-owned D3D11 device.");
        }

        if (win32D3d11DevicePtr == 0 && Interlocked.Exchange(ref _nv12ZeroHostModeLogged, 1) == 0)
        {
            MediaDiagnostics.LogInformation(
                "{0}: Win32 NV12 true zero-host - skipping output-owned D3D11GlInteropDeviceHost; YuvVideoRenderer will use libav ID3D11Device from the first Win32 NV12 frame (requires LibavD3D11DeviceComPtr on backing or negotiator-borrowed device).",
                _logPrefix);
        }
    }

    private void TryLogNv12D3d11BorrowAdapterOnce(IHardwareD3D11GlInteropSource hw, nint devicePtr)
    {
        if (Interlocked.Exchange(ref _nv12BorrowAdapterDiagLogged, 1) != 0)
            return;

        if (hw.TryGetHardwareD3D11AdapterLuid(out var decodeLuid) && decodeLuid != 0
            && D3D11InteropUtility.TryGetAdapterLuid(devicePtr, out var deviceLuid))
        {
            if (decodeLuid != deviceLuid)
            {
                MediaDiagnostics.LogWarning(
                    "{0}: DXGI adapter LUID from libav decode (packed={1}) differs from LUID derived from the D3D11 device used for GL (packed={2}) - OpenSharedResource / WGL_NV_DX_interop may fail on multi-GPU systems.",
                    _logPrefix,
                    decodeLuid,
                    deviceLuid);
                return;
            }

            MediaDiagnostics.LogInformation(
                "{0}: Win32 NV12 GL using libav D3D11 device (DXGI adapter LUID packed={1}).",
                _logPrefix,
                decodeLuid);
            return;
        }

        MediaDiagnostics.LogInformation(
            "{0}: Win32 NV12 GL using libav D3D11 device (adapter LUID unavailable for diagnostics - decode path may still be valid).",
            _logPrefix);
    }

    private void TryLogNv12OwnedInteropAdapterOnce(nint devicePtr)
    {
        if (Interlocked.Exchange(ref _nv12OwnedInteropDiagLogged, 1) != 0)
            return;

        if (D3D11InteropUtility.TryGetAdapterLuid(devicePtr, out var luid))
        {
            MediaDiagnostics.LogInformation(
                "{0}: Win32 NV12 GL using owned D3D11 interop helper (DXGI adapter LUID packed={1}).",
                _logPrefix,
                luid);
            return;
        }

        MediaDiagnostics.LogInformation(
            "{0}: Win32 NV12 GL using owned D3D11 interop helper (adapter LUID unavailable for diagnostics).",
            _logPrefix);
    }
}
