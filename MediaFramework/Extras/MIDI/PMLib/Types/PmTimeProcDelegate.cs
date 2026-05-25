using System.Runtime.InteropServices;

namespace PMLib.Types;

/// <summary>
/// A time-procedure delegate that returns the current time in milliseconds.
/// Pass an instance converted with
/// <see cref="Marshal.GetFunctionPointerForDelegate{TDelegate}(TDelegate)"/>
/// as the <c>timeProc</c> parameter of <see cref="Native.Pm_OpenInput"/> or
/// <see cref="Native.Pm_OpenOutput"/>.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int PmTimeProcDelegate(nint timeInfo);
