namespace PMLib.Types;

/// <summary>PortMidi error codes. Zero or positive values indicate success.</summary>
public enum PmError : int
{
    /// <summary>Normal return value indicating no error.</summary>
    NoError = 0,
    /// <summary>No error; also indicates no data available.</summary>
    NoData = 0,
    /// <summary>No error; indicates data is available.</summary>
    GotData = 1,
    /// <summary>Error was returned from the system level. Call <see cref="Native.GetHostErrorText"/> for details.</summary>
    HostError = -10000,
    /// <summary>Out of range device ID, wrong direction, or device already opened.</summary>
    InvalidDeviceId = -9999,
    InsufficientMemory = -9998,
    BufferTooSmall = -9997,
    /// <summary>Buffer overflow — data was lost. See <see cref="Native.Pm_Read"/>.</summary>
    BufferOverflow = -9996,
    /// <summary>Stream is NULL, not opened, or wrong direction.</summary>
    BadPtr = -9995,
    /// <summary>Illegal MIDI data (e.g. missing EOX in a SysEx message).</summary>
    BadData = -9994,
    InternalError = -9993,
    /// <summary>Buffer is already at its maximum size.</summary>
    BufferMaxSize = -9992,
    /// <summary>The function is not implemented on this platform.</summary>
    NotImplemented = -9991,
    /// <summary>The requested interface is not supported.</summary>
    InterfaceNotSupported = -9990,
    /// <summary>Cannot create virtual device because the name is already taken.</summary>
    NameConflict = -9989,
    /// <summary>Output attempted after the USB device was removed.</summary>
    DeviceRemoved = -9988,
}
