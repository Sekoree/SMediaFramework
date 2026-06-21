using System.Runtime.InteropServices;
using S.Media.Core.Diagnostics;

namespace S.Media.Interop;

/// <summary>
/// Opaque-handle plumbing shared by every C-ABI subsystem. A handle is a <see cref="GCHandle"/> to a managed
/// object, surfaced to C as an <see cref="IntPtr"/>. Resolving a closed/garbage handle returns
/// <see langword="null"/> rather than crashing, so a misbehaving host gets an error code, not a segfault.
/// </summary>
internal static class Handles
{
    /// <summary>Pins <paramref name="target"/> and returns its handle.</summary>
    public static IntPtr Alloc(object target) => GCHandle.ToIntPtr(GCHandle.Alloc(target));

    /// <summary>Returns the target as <typeparamref name="T"/>, or null if the handle is zero/freed/not a T.</summary>
    public static T? Resolve<T>(IntPtr handle) where T : class
    {
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var gch = GCHandle.FromIntPtr(handle);
            return gch.IsAllocated ? gch.Target as T : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Frees the handle and, when <paramref name="dispose"/> is set, disposes its target first. No-op on a
    /// zero handle; tolerant of double-free / garbage pointers (best-effort).
    /// </summary>
    public static void Free(IntPtr handle, bool dispose)
    {
        if (handle == IntPtr.Zero)
            return;
        try
        {
            var gch = GCHandle.FromIntPtr(handle);
            if (!gch.IsAllocated)
                return;
            if (dispose && gch.Target is IDisposable d)
                MediaDiagnostics.SwallowDisposeErrors(d.Dispose, "S.Media.Interop.Handles.Free");
            gch.Free();
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "S.Media.Interop.Handles.Free");
        }
    }
}
