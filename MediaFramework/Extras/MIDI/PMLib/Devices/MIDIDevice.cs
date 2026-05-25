using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PMLib.Runtime;
using PMLib.Types;

namespace PMLib.Devices;

/// <summary>
/// Base class for an open PortMidi device stream.
/// Call <see cref="Open"/> to open the device and <see cref="Dispose"/> (or <see cref="Close"/>)
/// to release it.
/// </summary>
public abstract class MIDIDevice : IDisposable
{
    protected static readonly ILogger Logger = PMLibLogging.GetLogger("PMLib.Devices");
    private bool _disposed;

    /// <summary>
    /// Native PortMidi stream handle. <see cref="nint.Zero"/> when not open.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Thread safety:</b> This field is not <c>volatile</c> and is not protected by a lock.
    /// Subclasses that read <see cref="Stream"/> from a background thread (e.g. a poll loop) must
    /// ensure the thread is stopped <b>before</b> calling <see cref="Close"/>, which sets this
    /// field to <see cref="nint.Zero"/>. <see cref="MIDIInputDevice"/> follows this pattern:
    /// <c>Close()</c> sets <c>_polling = false</c> and joins the thread before calling
    /// <c>base.Close()</c>.
    /// </remarks>
    protected nint Stream;

    /// <summary>PortMidi device ID passed to the constructor.</summary>
    public int DeviceId { get; }

    /// <summary>Device name cached at construction time (safe after <c>Pm_Terminate</c>).</summary>
    public string? Name { get; }

    /// <summary>Underlying MIDI API cached at construction time (e.g. <c>"ALSA"</c>).</summary>
    public string? Interface { get; }

    /// <summary>Returns <see langword="true"/> when the stream is open.</summary>
    public bool IsOpen => Stream != nint.Zero;

    protected MIDIDevice(int deviceId)
    {
        DeviceId = deviceId;

        // Eagerly copy the name strings so they remain valid after Pm_Terminate.
        var ptr = Native.Pm_GetDeviceInfo(deviceId);
        if (ptr != nint.Zero)
        {
            var info = Marshal.PtrToStructure<PmDeviceInfo>(ptr);
            Name      = info.Name;
            Interface = info.Interf;
        }

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("MIDIDevice created (deviceId={DeviceId}, name={Name}, interf={Interface})", deviceId, Name, Interface);
    }

    /// <summary>Opens the device stream. Returns <see cref="PmError.NoError"/> on success.</summary>
    public abstract PmError Open();

    /// <summary>Closes the device stream.</summary>
    public virtual PmError Close()
    {
        if (!IsOpen) return PmError.BadPtr;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("MIDIDevice closing (deviceId={DeviceId}, name={Name})", DeviceId, Name);

        var err = Native.Pm_Close(Stream);
        Stream = nint.Zero;
        return err;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            Close();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}