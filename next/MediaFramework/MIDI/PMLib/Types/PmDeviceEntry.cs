using System.Runtime.InteropServices;

namespace PMLib.Types;

/// <summary>
/// A safe, fully managed snapshot of a PortMidi device's metadata.
/// All string fields are eagerly copied from native memory at creation time
/// and remain valid after <see cref="PMUtil.Terminate"/>.
/// </summary>
/// <param name="Id">PortMidi device ID (stable within a single <c>Pm_Initialize</c>/<c>Pm_Terminate</c> session).</param>
/// <param name="Name">Human-readable device name (e.g. <c>"USB MidiSport 1x1"</c>).</param>
/// <param name="Interface">Underlying MIDI API (e.g. <c>"ALSA"</c>, <c>"CoreMIDI"</c>, <c>"MMSystem"</c>).</param>
/// <param name="IsInput"><see langword="true"/> if the device supports MIDI input.</param>
/// <param name="IsOutput"><see langword="true"/> if the device supports MIDI output.</param>
/// <param name="IsVirtual"><see langword="true"/> if this is a virtual (software) device.</param>
/// <param name="IsOpen"><see langword="true"/> if a stream is currently open on this device.</param>
public readonly record struct PmDeviceEntry(
    int     Id,
    string? Name,
    string? Interface,
    bool    IsInput,
    bool    IsOutput,
    bool    IsVirtual,
    bool    IsOpen)
{
    /// <summary>
    /// Creates a <see cref="PmDeviceEntry"/> from a PortMidi device ID, marshalling the
    /// device info pointer and eagerly copying the native string fields.
    /// Returns <see langword="null"/> if the device ID is out of range.
    /// </summary>
    internal static PmDeviceEntry? TryFromNative(int id)
    {
        var ptr = Native.Pm_GetDeviceInfo(id);
        if (ptr == nint.Zero) return null;

        var info = Marshal.PtrToStructure<PmDeviceInfo>(ptr);

        // Eagerly resolve the string properties while the native strings are still alive.
        return new PmDeviceEntry(
            Id:        id,
            Name:      info.Name,      // calls Marshal.PtrToStringUTF8 now
            Interface: info.Interf,    // calls Marshal.PtrToStringUTF8 now
            IsInput:   info.Input    != 0,
            IsOutput:  info.Output   != 0,
            IsVirtual: info.IsVirtual != 0,
            IsOpen:    info.Opened   != 0);
    }
}

