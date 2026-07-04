namespace PMLib.Types;

/// <summary>Keys for system-dependent device properties passed in a PmSysDepInfo struct.</summary>
public enum PmSysDepPropertyKey : int
{
    /// <summary>No-op key value.</summary>
    None = 0,
    /// <summary>CoreMIDI manufacturer name (string value).</summary>
    CoreMIDIManufacturer = 1,
    /// <summary>ALSA port name (string value).</summary>
    AlsaPortName = 2,
    /// <summary>ALSA client name (string value).</summary>
    AlsaClientName = 3,
}
