namespace S.Media.Core.Audio;

/// <summary>How <see cref="AudioClipPlayer.Fire"/> behaves when a voice is already playing.</summary>
public enum AudioClipPlayerMode
{
    /// <summary>Allow overlapping voices up to <see cref="AudioClipPlayer.MaxPolyphony"/>.</summary>
    Polyphonic,

    /// <summary>Stop the previous voice, then start a fresh one.</summary>
    MonoRetrigger,

    /// <summary>Ignore new fires while a voice is still active.</summary>
    OneShot,

    /// <summary>First fire loops until stopped; second fire clears loop and stops.</summary>
    LatchedLoop,
}
