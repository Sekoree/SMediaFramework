namespace VideoPlaybackSmoke;

/// <summary>Shared defaults for <see cref="VideoPlaybackSmokeSession"/> and the CLI.</summary>
public static class SmokeDefaults
{
    public const string DefaultNDIOutputName = "MFPlayer PlaybackVideo";

    public const int DefaultNDIVideoPumpFrames = 8;

    public const int DefaultAudioChunkSamples = 960;

    public const int DefaultNDIAudioOutputPumpCapacityChunks = 24;

    public const int MaxSdlWindowWidth = 1920;

    public const int MaxSdlWindowHeight = 1080;

    public const int MaxNDIWaitFirstReceiverMs = 300_000;

    public static TimeSpan AudioPrefillDuration => TimeSpan.FromSeconds(3);
}
