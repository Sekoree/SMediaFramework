namespace VideoPlaybackSmoke;

/// <summary>Shared defaults for <see cref="VideoPlaybackSmokeSession"/> and the CLI.</summary>
public static class SmokeDefaults
{
    public const string DefaultNdiOutputName = "MFPlayer PlaybackVideo";

    public const int DefaultNdiVideoPumpFrames = 8;

    public const int DefaultAudioChunkSamples = 960;

    public const int DefaultNdiAudioSinkPumpCapacityChunks = 24;

    public const int MaxSdlWindowWidth = 1920;

    public const int MaxSdlWindowHeight = 1080;

    public const int MaxNdiWaitFirstReceiverMs = 300_000;

    public static TimeSpan AudioPrefillDuration => TimeSpan.FromSeconds(3);
}
