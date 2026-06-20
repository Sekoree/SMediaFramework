using S.Media.Core.Video;
using System.Runtime.InteropServices;
using static FFmpeg.AutoGen.ffmpeg;
using VorticeD3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using VorticeDxgiResource1 = Vortice.DXGI.IDXGIResource1;
using VorticeDxgiSharedFlags = Vortice.DXGI.SharedResourceFlags;
using VorticeDxgiFormat = Vortice.DXGI.Format;

namespace S.Media.FFmpeg.Video.Internal;

/// <summary>
/// Maps libav <see cref="AVPixelFormat.AV_PIX_FMT_D3D11"/> frames (see libavutil
/// <c>hwcontext_d3d11va.h</c> — <c>AVFrame.data[0]</c> texture, <c>data[1]</c> array index)
/// into <see cref="Win32SharedNv12Backing"/> via DXGI shared NT handles. When <paramref name="sharedHandleOnly"/> is false,
/// also records libav <c>ID3D11Device</c> / <c>ID3D11Texture2D</c> COM pointers for same-device GL upload (no <c>OpenSharedResource</c>).
/// </summary>
/// <remarks>
/// <c>S.Media.OpenGL.Nv12Win32SharedHandleGpuUploader</c> uses <see cref="Win32SharedNv12Backing.LibavD3D11Texture2DComPtr"/>
/// when its D3D11 device matches <see cref="Win32SharedNv12Backing.LibavD3D11DeviceComPtr"/>; otherwise it falls back to
/// the NT-handle path on <see cref="Win32SharedNv12Backing.SharedLumaNtHandle"/>.
/// </remarks>
internal static unsafe class D3D11VaNv12BackingFactory
{
    internal static Win32SharedNv12Backing? TryCreateBacking(AVFrame* frame, bool sharedHandleOnly)
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

        var texture = AddRefAndWrapTexture2D((nint)pTex);
        try
        {
            using var textureDevice = texture.Device;
            var deviceComPtr = textureDevice?.NativePointer ?? 0;
            var desc = texture.Description;
            if (desc.Format != VorticeDxgiFormat.NV12)
                return null;

            var yPitch = frame->linesize[0] > 0 ? frame->linesize[0] : Align256((int)desc.Width);
            var uvPitch = frame->linesize[1] > 0 ? frame->linesize[1] : yPitch;
            if (yPitch <= 0 || uvPitch <= 0)
                return null;

            // Prefer the same-device COM texture path when libav's D3D11 device is available. Some
            // drivers expose decoder pool textures that cannot be exported via CreateSharedHandle, but
            // the GL uploader can still import the texture directly on the borrowed libav device.
            if (!sharedHandleOnly && deviceComPtr != 0)
                return new Win32SharedNv12Backing(0, 0, yPitch, uvPitch, arraySlice, deviceComPtr, (nint)pTex,
                    RetainFrameReference(frame));

            nint sharedHandle;
            try
            {
                using var dxgi = texture.QueryInterface<VorticeDxgiResource1>();
                sharedHandle = dxgi.CreateSharedHandle(null, VorticeDxgiSharedFlags.None, null);
            }
            catch
            {
                return null;
            }

            if (sharedHandle == 0)
                return null;

            if (sharedHandleOnly)
                return new Win32SharedNv12Backing(sharedHandle, 0, yPitch, uvPitch, arraySlice, 0, 0);

            if (deviceComPtr == 0)
                return new Win32SharedNv12Backing(sharedHandle, 0, yPitch, uvPitch, arraySlice, 0, 0);

            try
            {
                return new Win32SharedNv12Backing(sharedHandle, 0, yPitch, uvPitch, arraySlice, deviceComPtr, (nint)pTex,
                    RetainFrameReference(frame));
            }
            catch
            {
                _ = Kernel32.CloseHandle(sharedHandle);
                throw;
            }
        }
        finally
        {
            texture.Dispose();
        }
    }

    private static Action RetainFrameReference(AVFrame* frame)
    {
        var clone = av_frame_alloc();
        if (clone == null)
            throw new OutOfMemoryException("av_frame_alloc (D3D11 frame retain) returned NULL");

        var ret = av_frame_ref(clone, frame);
        if (ret < 0)
        {
            var c = clone;
            av_frame_free(&c);
            FFmpegException.ThrowIfError(ret, nameof(av_frame_ref));
        }

        var clonePtr = (nint)clone;
        return () =>
        {
            var f = (AVFrame*)clonePtr;
            av_frame_free(&f);
        };
    }

    private static int Align256(int width)
    {
        const int a = 256;
        return (width + a - 1) / a * a;
    }

    private static VorticeD3D11Texture2D AddRefAndWrapTexture2D(nint borrowedTextureComPtr)
    {
        Marshal.AddRef(borrowedTextureComPtr);
        try
        {
            return new VorticeD3D11Texture2D(borrowedTextureComPtr);
        }
        catch
        {
            Marshal.Release(borrowedTextureComPtr);
            throw;
        }
    }

    private static class Kernel32
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(nint hObject);
    }
}
