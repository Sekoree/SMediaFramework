using PMLib;

namespace S.Control;

/// <summary>
/// MIDI-01: a point-in-time capability snapshot of the PortMIDI runtime. PortMIDI exposes no library-version
/// call, so "capability" here is what a host actually needs to reason about MIDI support: whether the native
/// library loads and initializes at all, and how many input/output devices it can see. Surfaced so a UI or log
/// can report "MIDI unavailable / 0 devices" instead of failing opaquely when PortMIDI is missing or headless.
/// </summary>
/// <param name="Available">True when PortMIDI initialized successfully (the native library is present and usable).</param>
/// <param name="InputDeviceCount">Discoverable MIDI input devices (0 when unavailable or none attached).</param>
/// <param name="OutputDeviceCount">Discoverable MIDI output devices.</param>
/// <param name="Detail">Diagnostic message when <paramref name="Available"/> is false (e.g. the init error); else null.</param>
public readonly record struct MIDIRuntimeCapability(
    bool Available,
    int InputDeviceCount,
    int OutputDeviceCount,
    string? Detail = null);

/// <summary>MIDI-01: queries the PortMIDI runtime capability for diagnostics/UX.</summary>
public static class MIDIRuntimeDiagnostics
{
    /// <summary>
    /// Initializes PortMIDI (via the shared ref-counted lease, so it composes with any live control sessions),
    /// counts the discoverable devices, and releases. Never throws: a missing/unusable native library is
    /// reported as <see cref="MIDIRuntimeCapability.Available"/> = <c>false</c> with the reason in
    /// <see cref="MIDIRuntimeCapability.Detail"/>.
    /// </summary>
    public static MIDIRuntimeCapability Query()
    {
        try
        {
            using var lease = ControlMIDILibraryLease.Acquire();
            return new MIDIRuntimeCapability(
                Available: true,
                InputDeviceCount: PMUtil.GetInputDevices().Count,
                OutputDeviceCount: PMUtil.GetOutputDevices().Count);
        }
        catch (Exception ex)
        {
            return new MIDIRuntimeCapability(Available: false, 0, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
