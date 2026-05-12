using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace S.Media.OpenGL.Internal;

/// <summary>Minimal loader for <see href="https://registry.khronos.org/OpenGL/extensions/NV/WGL_NV_DX_interop.txt">WGL_NV_DX_interop</see> entry points.</summary>
internal sealed unsafe class WglNvDxInterop
{
    /// <summary><c>WGL_TEXTURE_2D_ARB</c> — passed as <c>GLenum type</c> to <c>wglDXRegisterObjectNV</c>.</summary>
    internal const uint Texture2DArb = 0x207A;

    internal const uint AccessReadOnlyNv = 0;

    internal delegate nint WglDxOpenDeviceNV(nint dxDevice);

    internal delegate int WglDxCloseDeviceNV(nint hDevice);

    internal delegate nint WglDxRegisterObjectNV(nint hDevice, nint dxObject, uint glName, uint type, uint access);

    internal delegate int WglDxUnregisterObjectNV(nint hDevice, nint hObject);

    internal delegate int WglDxLockObjectsNV(nint hDevice, int count, nint* hObjects);

    internal delegate int WglDxUnlockObjectsNV(nint hDevice, int count, nint* hObjects);

    internal WglDxOpenDeviceNV? OpenDevice;
    internal WglDxCloseDeviceNV? CloseDevice;
    internal WglDxRegisterObjectNV? RegisterObject;
    internal WglDxUnregisterObjectNV? UnregisterObject;
    internal WglDxLockObjectsNV? LockObjects;
    internal WglDxUnlockObjectsNV? UnlockObjects;

    internal bool IsComplete =>
        OpenDevice != null && CloseDevice != null && RegisterObject != null && UnregisterObject != null &&
        LockObjects != null && UnlockObjects != null;

    internal static bool TryLoad(GL gl, out WglNvDxInterop w)
    {
        w = new WglNvDxInterop();
        if (!OperatingSystem.IsWindows())
            return false;

        nint Get(string name)
        {
            var p = gl.Context.GetProcAddress(name);
            return p == 0 ? 0 : p;
        }

        static T? As<T>(nint p) where T : Delegate =>
            p == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(p);

        w.OpenDevice = As<WglDxOpenDeviceNV>(Get("wglDXOpenDeviceNV"));
        w.CloseDevice = As<WglDxCloseDeviceNV>(Get("wglDXCloseDeviceNV"));
        w.RegisterObject = As<WglDxRegisterObjectNV>(Get("wglDXRegisterObjectNV"));
        w.UnregisterObject = As<WglDxUnregisterObjectNV>(Get("wglDXUnregisterObjectNV"));
        w.LockObjects = As<WglDxLockObjectsNV>(Get("wglDXLockObjectsNV"));
        w.UnlockObjects = As<WglDxUnlockObjectsNV>(Get("wglDXUnlockObjectsNV"));
        return w.IsComplete;
    }
}
