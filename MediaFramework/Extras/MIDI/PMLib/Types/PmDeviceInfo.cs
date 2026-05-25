using System.Runtime.InteropServices;

namespace PMLib.Types;

/// <summary>
/// MIDI device information returned by <see cref="Native.Pm_GetDeviceInfo"/>.
/// The structure is owned by PortMidi and is valid between
/// <see cref="Native.Pm_Initialize"/> and <see cref="Native.Pm_Terminate"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PmDeviceInfo
{
    /// <summary>Internal structure version (should be <c>200</c>).</summary>
    public int StructVersion;

    // Native char* fields — accessed through the properties below.
    private nint _interf;
    private nint _name;

    /// <summary>Non-zero if input is available on this device.</summary>
    public int Input;
    /// <summary>Non-zero if output is available on this device.</summary>
    public int Output;
    /// <summary>Non-zero if the device is currently open.</summary>
    public int Opened;
    /// <summary>Non-zero if this is a virtual device.</summary>
    public int IsVirtual;

    /// <summary>Underlying MIDI API, e.g. <c>"ALSA"</c>, <c>"CoreMIDI"</c>, or <c>"MMSystem"</c>.</summary>
    public readonly string? Interf => Marshal.PtrToStringUTF8(_interf);

    /// <summary>Human-readable device name, e.g. <c>"USB MidiSport 1x1"</c>.</summary>
    public readonly string? Name => Marshal.PtrToStringUTF8(_name);
}
