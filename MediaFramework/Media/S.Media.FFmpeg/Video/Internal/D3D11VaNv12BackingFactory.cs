using S.Media.Core.Video;
using static FFmpeg.AutoGen.ffmpeg;
using VorticeD3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using VorticeDxgiResource1 = Vortice.DXGI.IDXGIResource1;
using VorticeDxgiSharedFlags = Vortice.DXGI.SharedResourceFlags;
using VorticeDxgiFormat = Vortice.DXGI.Format;

namespace S.Media.FFmpeg.Video.Internal;

/// <summary>
/// Maps libav <see cref="AVPixelFormat.AV_PIX_FMT_D3D11"/> frames (see libavutil
/// <c>hwcontext_d3d11va.h</c> — <c>AVFrame.data[0]</c> texture, <c>data[1]</c> array index)
/// into <see cref="VideoWin32Nv12Backing"/> via DXGI shared NT handles. When <paramref name="sharedHandleOnly"/> is false,
/// also records libav <c>ID3D11Device</c> / <c>ID3D11Texture2D</c> COM pointers for same-device GL upload (no <c>OpenSharedResource</c>).
/// </summary>
/// <remarks>
/// <c>S.Media.OpenGL.Nv12Win32SharedHandleGpuUploader</c> uses <see cref="VideoWin32Nv12Backing.LibavD3D11Texture2DComPtr"/>
/// when its D3D11 device matches <see cref="VideoWin32Nv12Backing.LibavD3D11DeviceComPtr"/>; otherwise it falls back to
/// the NT-handle path on <see cref="VideoWin32Nv12Backing.SharedLumaNtHandle"/>.
/// </remarks>
internal static unsafe class D3D11VaNv12BackingFactory
{
    internal static VideoWin32Nv12Backing? TryCreateBacking(AVFrame* frame, bool sharedHandleOnly)
    {
        if (!OperatingSystem.IsWindows() || frame == null)
            return null;

        var fmt = (AVPixelFormat)frame->format;
        if (fmt is not (AVPixelFormat.AV_PIX_FMT_D3D11 or AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD))
            return null;

        var pTex = frame->data[0];
        if (pTex == null)
            return null;

        var arraySlice = (int)(nint)frame->data[1];
        if (arraySlice < 0)
            return null;

        var texture = new VorticeD3D11Texture2D((nint)pTex);
        try
        {
            var deviceComPtr = texture.Device?.NativePointer ?? 0;
            using var dxgi = texture.QueryInterface<VorticeDxgiResource1>();
            var sharedHandle = dxgi.CreateSharedHandle(null, VorticeDxgiSharedFlags.None, null);
            if (sharedHandle == 0)
                return null;

            var desc = texture.Description;
            if (desc.Format != VorticeDxgiFormat.NV12)
                return null;

            var yPitch = frame->linesize[0] > 0 ? frame->linesize[0] : Align256((int)desc.Width);
            var uvPitch = frame->linesize[1] > 0 ? frame->linesize[1] : yPitch;
            if (yPitch <= 0 || uvPitch <= 0)
            {
                _ = Kernel32.CloseHandle(sharedHandle);
                return null;
            }

            if (sharedHandleOnly)
                return new VideoWin32Nv12Backing(sharedHandle, 0, yPitch, uvPitch, arraySlice, 0, 0);

            return new VideoWin32Nv12Backing(sharedHandle, 0, yPitch, uvPitch, arraySlice, deviceComPtr, (nint)pTex);
        }
        finally
        {
            texture.Dispose();
        }
    }

    private static int Align256(int width)
    {
        const int a = 256;
        return (width + a - 1) / a * a;
    }

    private static class Kernel32
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(nint hObject);
    }
}
