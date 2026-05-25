namespace JackLib.Types;

// ── Port type string constants ─────────────────────────────────────────────

/// <summary>Standard JACK port type strings (JACK_DEFAULT_AUDIO_TYPE / JACK_DEFAULT_MIDI_TYPE).</summary>
public static class JackPortType
{
    /// <summary>"32 bit float mono audio" — standard JACK audio port type.</summary>
    public const string DefaultAudio = "32 bit float mono audio";

    /// <summary>"8 bit raw midi" — standard JACK MIDI port type.</summary>
    public const string DefaultMidi  = "8 bit raw midi";
}

// ── Enums ──────────────────────────────────────────────────────────────────

/// <summary>Options for <c>jack_client_open</c> (jack_options_t).</summary>
[Flags]
public enum JackOptions : uint
{
    NullOption    = 0x00,
    NoStartServer = 0x01,
    UseExactName  = 0x02,
    ServerName    = 0x04,
    LoadName      = 0x08,
    LoadInit      = 0x10,
    SessionID     = 0x20,
}

/// <summary>Status word returned from several JACK operations (jack_status_t).</summary>
[Flags]
public enum JackStatus : uint
{
    None          = 0,
    Failure       = 0x0001,
    InvalidOption = 0x0002,
    NameNotUnique = 0x0004,
    ServerStarted = 0x0008,
    ServerFailed  = 0x0010,
    ServerError   = 0x0020,
    NoSuchClient  = 0x0040,
    LoadFailure   = 0x0080,
    InitFailure   = 0x0100,
    ShmFailure    = 0x0200,
    VersionError  = 0x0400,
    BackendError  = 0x0800,
    ClientZombie  = 0x1000,
}

/// <summary>
/// Port flags (JackPortFlags).
/// Note: the C API passes these as <c>unsigned long</c> (64-bit on Linux LP64).
/// </summary>
[Flags]
public enum JackPortFlags : ulong
{
    None       = 0,
    IsInput    = 0x1,
    IsOutput   = 0x2,
    IsPhysical = 0x4,
    CanMonitor = 0x8,
    IsTerminal = 0x10,
}

/// <summary>Latency callback mode (JackLatencyCallbackMode).</summary>
public enum JackLatencyCallbackMode : int
{
    CaptureLatency  = 0,
    PlaybackLatency = 1,
}

// ── Managed delegate types ─────────────────────────────────────────────────

/// <summary>
/// Process callback (JackProcessCallback).
/// Called on the JACK RT thread — MUST be real-time safe (no alloc, no blocking).
/// </summary>
/// <param name="nframes">Number of frames in this cycle.</param>
/// <param name="arg">User data pointer (from registration call).</param>
/// <returns>0 to continue, non-zero to stop the engine.</returns>
public delegate int JackProcessDelegate(uint nframes, nint arg);

/// <summary>Server shutdown callback (JackShutdownCallback).</summary>
public delegate void JackShutdownDelegate(nint arg);

/// <summary>Buffer-size changed callback (JackBufferSizeCallback).</summary>
public delegate int JackBufferSizeDelegate(uint nframes, nint arg);

/// <summary>Sample-rate changed callback (JackSampleRateCallback).</summary>
public delegate int JackSampleRateDelegate(uint nframes, nint arg);

/// <summary>XRun (overrun/underrun) callback (JackXRunCallback).</summary>
public delegate int JackXRunDelegate(nint arg);

